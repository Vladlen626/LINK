# LINK harness audit

2026-10-20. Read-only audit of the agent harness: the texts in `docs/harness/**` and the code in
`src/Link.Desktop/Services` that drives them, compared with the user's own Claude Code harness (`~/.claude/harness`,
hx). Nothing was built or run. The cost estimates below are arithmetic on the prices and shares known as of
2026-10-20, not new measurements. Each is marked as an estimate.

Goal: LINK should give great results at low cost whatever model runs behind it (Opus, Sonnet, Haiku, Codex, a custom
model). The harness should carry the quality, not one model.

## Summary

**Verdict.** The skeleton is model-agnostic and sound. The task state is built by code, Link runs its own check after
Do, the critic judges blind, lessons are gated by exams, MCP is isolated per workspace, and TaskState-based fresh
sessions replace stale ones. Three things keep it from being cheap and safe with any model:

1. **What a run gets is not fully Link's to decide.** Claude runs on the user's own account load the user's personal
   Claude Code setup: 10 hx hook events, a 7.6 KB Russian `CLAUDE.md` that contradicts "answer in English", 14 DR3
   skills, and auto-compaction at 40% of the window (F1).
2. **No decision uses price, and the best quality signals are dropped.** The cost measure ignores which model ran and
   counts cache writes as fresh input. Worker evidence counts exit codes, not the user's send-backs, and never ages.
   So routing cannot learn when a cheaper worker pays (F2, F3). The A/B (Opus alone $2.50–3.96 against plan → Sonnet →
   review $4.28–4.96) is what the price table predicts: Sonnet reads cache at Opus's price.
3. **Correctness rests on the model's own judgement.** The rules assume a strong model. Checks are optional and
   numeric checks exist only where the user configured one. Art is judged from images. Link's JSON blocks get no
   repair. A Haiku-class or custom model fails silently on each of these (F4, F5).

**Top fixes, by effect per effort**

| # | Fix | Effort | Effect |
|---|---|---|---|
| F1 | Run Claude Code with `--setting-sources project,local`, and check the `system/init` event | S | Same harness on every account. −5–6k tokens per request. No foreign hooks or rules. |
| F3 | Count user send-backs and OKs, and unverified "done", in `workers.json`. Key it by the resolved model. Let it age. | S | Routing learns from real quality |
| F2 | Price per model (in, out, cache write, cache read, subscription), cost per accepted step in the evidence, routing by expected cost | M | Team and cheap workers used only where they pay |
| F4 | Per-pack numeric checks run by Link before the critic or the lead: Blender `check.py` first | M | Weaker models can judge. Fewer redos and fewer image tokens. |
| F5 | Structured output for Link's blocks (`--json-schema` / `--output-schema`) plus one repair turn. A short procedure add-on for Light and custom workers. | S–M | Weak models stop ending tasks with "no verdict" |
| F7 | Usage telemetry of skills, map and memory, with a Reflect that proposes pruning backed by numbers | M | The start of every session stops growing |

**Keep as it is:** `TaskState` / `CompactRule`, `AgentCheck` with its two retries, the blind critic run by another
agent, exam-gated lessons, `--strict-mcp-config` and Codex's features off, `AutoEffort` by step kind and `link-next`,
and rules that hold only corrections of behaviour seen in runs.

**Order of work** (details at the end): week 1: F1, F3, F11, F12, the F5 repair turn. Week 2: F2 (the pricing work
under way in another session), F8, F9. Then F4, F6 + F7 (Reflect), the F5 light rules and F10. Each change is checked
on a fixed benchmark (section "Measuring").

## Status on branch `harness-improve`

2026-10-20. Done, with checks in `tests/Link.Desktop.Tests/AgentTests.cs` unless said otherwise:

| # | What changed | Where |
|---|---|---|
| F1 | Claude Code runs with `--setting-sources project,local`: the user's own `~/.claude` setup no longer loads, the workspace's still does. The `system/init` check is not done. | `ClaudeCodeProvider.cs`, `PROJECT_CONTEXT.md` §3 |
| F3, fix 1 | A `Check` step counts for its worker at the user's OK, not before. Sending back a step that waited for the OK counts a failure. An unverified "done" counts for no one, as before. | `TeamRunner.Approve`, `TeamRunner.SendBack`, `MainWindow.Team.cs` |
| F5, fix 2 | A lead's review or an art critic's run that ended well with no `link-review` block that reads is asked once, in its session at low effort, for the block alone. Then the user decides. UI flow: built, not run live. | `TeamPlan.VerdictOnly`, `MainWindow.AskVerdictAgain` |
| F11 | Pack skills are listed for Codex too. A Codex study run gets network access. `training-learn.md` names what to do, not Claude's tools. | `AgentRules.Before`, `AgentTask.ReadsWeb`, `CodexProvider.cs`, `MainWindow.Training.cs` |
| F12 | The cache lifetime is per provider: an hour for Claude Code, half an hour for Codex. | `CompactRule.CacheLasts` |

Left on purpose:
- F2 and F7 are another session's work. F4 waits for the Blender pack's check.
- F3 fixes 2–4 (unverified weight, the resolved model as key, ageing) change what routing learns from and need a
  decision on weights.
- F5 fixes 1, 3 and 4 (schemas, light rules) need live runs on both CLIs first. A lead's answer without a plan is not
  repaired: Link reads it as an ordinary plan the lead carries out.
- F6, F8, F9, F10, and F11's auto-memory and vision items. The last two need F2's model profile.

## 1. The harness as it is

### What a new session gets

The first message of a new session is assembled by `AgentRules.Before` (`AgentRules.cs:18-53`) and sent on stdin
(`CliAgentProvider.StandardInput`, `CliAgentProvider.cs:217-223`). A resumed session gets only the new words. Sizes are
characters without HTML comments. Tokens are estimated at about 4 characters each for English and about 3 for mixed
Russian.

| Layer | Source | Size | Who gets it |
|---|---|---|---|
| CLI's own system prompt and tool schemas | Claude Code / Codex | not measured in Link runs. Codex was ~16k tokens per request start on 2026-09-30, ~3.3k of it plugin skills and hints, now turned off | every request |
| **User-level Claude config** | `~/.claude/CLAUDE.md` 7.6 KB, 14 user skills with ~16 KB of descriptions, hooks of 10 events, `env` `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE=40` | ≈ 5–6k tokens, estimate | Claude Code on the user's own account only (F1) |
| Workspace `CLAUDE.md` / `AGENTS.md`, auto memory `MEMORY.md` | the CLIs themselves; `ClaudeAccounts.MemoryFolder` for Claude | project-dependent | Claude: both. Codex: `AGENTS.md`. |
| Agent rules | `docs/harness/agent-rules.md` | 7.1k chars ≈ 1.8k tokens | every new session |
| Perforce note, pack rules | `perforce.md` 1.2k, `packs/unity/rules.md` 3.4k | ≈ 0.3–0.9k tokens each | per workspace |
| Skill listing | pack skills plus workspace skills, ~350-character descriptions | ≈ 0.5–0.8k tokens | **Claude only**: Codex is assumed to find them, but pack skills live outside the workspace (F11) |
| Role | `roles/<id>.md` plus `packs/<x>/roles/<id>.md` | 2.0–4.2k chars ≈ 0.5–1k tokens | runs with a specialist |
| "Read first" skills | `ProjectRoles.SessionText` (`ProjectRoles.cs:218`), up to 4 (`MostSkills`, line 41), read whole by the agent | 3D artist in Blender: `blender-live` 4.9k + `blender-model` 9.2k + `art-style` 4.2k ≈ 4.6k tokens. Unreal rigger: `ue-mcp-animation` 11.6k chars, 182 lines | runs with a specialist |
| Map | `docs/map.md`, "Read it first" | up to ~100 lines ≈ 1–2k tokens | every session that follows the rule |
| Task / TaskState | the card, or `TaskState.Build`, ≤ 6k chars (`AgentCoordinator.BriefBudget`) | ≤ 1.5k tokens | every new session |

A Blender artist's new session on Claude carries about 10k tokens that Link controls, plus 5–6k that it does not,
before the task starts (estimate). Each is written to the cache once per session and read on every request after.
At Opus prices (cache write at 2× input for the 1-hour TTL, read $0.20/M) the Link part costs roughly $0.10–0.15 per
30-request session. A team task opens 5–10 new sessions (plan, steps, fresh reviews, critic), so the fixed prefix
alone is about $0.5–1.5 of a $4–5 task. That is an estimate from the layer sizes, to be checked against
`usage` of real runs. On Haiku the same prefix costs about 1/40 of that.

### The control loop

| Piece | What it decides | Input | Model-agnostic? |
|---|---|---|---|
| `AutoEffort` | effort per run and per step | phase, failures, the agent's `link-next`, the step kind, brief words | yes, though the vocabulary is Claude's (`low…max`), mapped by `Nearest` |
| `CompactRule` | fresh session from TaskState instead of resuming | context tokens, budget, window, idle against a 60-minute cache | logic yes; the TTL is Claude-subscription-specific (F12) |
| `AgentLoopDetector` | marks a run "looks like a loop" | identical tool name and arguments, the same error ×3, 12 searches without a change | yes; it marks but does not steer (F8) |
| `AgentCheck` | pass or fail after Do, the tail back to the agent ×2 | the user's `CheckCommand` | yes; optional (F4) |
| `TeamRunner` + `WorkerRouting` | who does a step, retry, escalate, review, critic | plan tier, step kind, `WorkerEvidence`, limits | yes, but fixed tiers with no price (F2) |
| `WorkerEvidence` | blocks a worker that fails a kind; may lower the tier for a proven one | done/failed per kind × provider × model alias | yes; weak signals (F3) |
| `ArtCritic` | accept or redo of art | frames (images), the profile | needs vision, judged by a model (F4) |
| `Training` | lessons into skills only after a user-rated exam | the user's 1–5 rating | yes |
| `TaskMetrics`, `TeamVsSolo`, `UsageReport` | nothing: they display | tokens with fixed ratios; ccusage $ per session | display only (F2) |

### Model-specific against model-agnostic

- **Agnostic:** TaskState and fresh sessions; the check-after-Do loop; the team state machine and its reasons in
  words; exam gating; the map and skills as plain files (the Agent Skills format); `link-*` blocks as a convention.
- **Specific to one model or CLI:**
  - user-config inheritance and auto memory: Claude only;
  - the skill listing: Claude only;
  - the 60-minute cache TTL: a Claude subscription;
  - `TaskMetrics` ratios 0.1 and 5: Anthropic's list ratios, with no model price;
  - tool names in texts: `WebFetch`, `Read`, `curl`, `uv` in `training-learn.md`; "open them with Read" in
    `ClaudeCodeProvider.cs:86`;
  - vision is per CLI (`Capabilities.Images`), not per model;
  - rule wording that assumes the CLI "looks around, plans, checks itself" (`agent-rules.md:1-6`), which holds for
    Opus and GPT-5.x and not for a Haiku-class or small model.

## 2. Findings, ranked by effect on quality and cost

Effort: XS < 1 h, S ≈ half a day, M ≈ 1–3 days.

### F1. Claude runs load the user's personal Claude Code setup

- **Problem.** `ClaudeCodeProvider.Arguments` (`ClaudeCodeProvider.cs:44-80`) isolates MCP (`--strict-mcp-config`,
  line 61) but not settings, `CLAUDE.md`, skills or hooks. Today, on the user's own account, every Link run of Claude
  Code gets:
  - **hooks of 10 events**: hx's `SessionStart`, `PostToolUse`, `Stop`, `PreCompact` and others. hx's default profile
    is active in any folder. Its `[hx] budget` text says "stop, report and ask" at 150 calls, but a Link run has no
    dialog. `[hx] title` may ask for an app tool that a `-p` run lacks.
  - **`~/.claude/CLAUDE.md`** (7.6 KB). It says "Отвечай по-русски" ("answer in Russian"), against
    `agent-rules.md:89-92` "In English". It also carries DR3 task naming and git rules.
  - **14 user skills** (~16 KB of descriptions): DR3 playtest, Jira and review skills offered in a Blender task.
  - **`CLAUDE_AUTOCOMPACT_PCT_OVERRIDE=40`** from `settings.json` `env`: the CLI compacts at 40% of the window. Link's
    Context budget, its "Context x / y" line and `CompactRule`'s 80% threshold all assume the CLI's own window.
- **The same task gets a different harness by account.** Other accounts point `CLAUDE_CONFIG_DIR` at
  `%LocalAppData%\Link\ClaudeAccounts\<name>` (`ClaudeAccounts.cs:29-32`) and get none of the above. Evidence from runs
  across accounts is therefore not comparable.
- **Evidence.**
  - `PROJECT_CONTEXT.md:405` still reads that the user's config "is empty (no plugins, no MCP)": that was true on
    2026-09-30, not now.
  - Settings sources are documented as `user`, `project`, `local`
    ([CLI reference](https://code.claude.com/docs/en/cli-reference)).
- **Fix.**
  1. Pass `--setting-sources project,local`. This keeps the workspace's `CLAUDE.md` and `.claude/settings*`.
  2. Give Link's own settings, if any are needed, through `--settings <file>`. The stage-6 plan already wants Link's
     hooks only there.
  3. Parse the `system/init` event (tools, skills, plugins, MCP) and show it in the Harness inspector. Warn when a
     skill or hook that Link did not put there appears.
  4. Verify with one live run that user `CLAUDE.md` and user skills also drop with the source.
  5. If they do not, use `--bare` plus `--add-dir` for Link's own answers (map, note), where the project's `CLAUDE.md`
     is not needed either. Bare mode drops hooks, skills, plugins, MCP, auto memory and `CLAUDE.md`.
  6. Update `PROJECT_CONTEXT.md` §3 once verified.
- **Effect.**
  - Quality: no contradictory language rule, no foreign skills or hooks, the same behaviour on every account.
  - Cost: about −5–6k tokens on every request (estimate).
  - Compaction where Link's budget expects it.
- **Effort.** S: an argument list, a parser field, one live check.

### F2. No decision uses price; the cost measure is model-blind

- **Problem.** "Think expensive, execute cheap" is wired as fixed tiers.
  - `WorkerRouting.Workers` (`WorkerRouting.cs:36-50`): Standard is Codex if set up, else Claude at the default model.
    Strong is Opus xhigh (`AgentModels.cs:657`). Light is Haiku low.
  - Nothing compares what a worker costs against what it saves.
  - The only cost measure, `TokenCount.Equivalent` = uncached + 0.1·cached + 5·output (`TaskMetrics.cs:26`), has no
    model price. Claude's `cache_creation_input_tokens` are added to fresh input (`ClaudeCodeProvider.cs:438-440`,
    `TaskMetrics.cs:15-20`), though a cache write costs 1.25–2× input.
  - Real prices already reach Link and are only displayed: Claude Code's `total_cost_usd` (`AgentRunRecord.CostUsd`)
    and ccusage's per-session cost (`MainWindow.Agent.cs:1147-1150`).
  - `docs/TOKENS_PLAN.md` models Codex and Haiku as Opus × 0.3–0.7 and × 0.15–0.3 on all tokens.
- **Evidence from prices** (2026-10-20, per M tokens): Opus $4 in / $20 out / $0.20 cache read; Sonnet $2 / $10 /
  $0.20; Haiku $0.10 / $0.50 / $0.01. Agent spend is about 55% cache reads, 26% cache writes and 19% output. With the
  same token profile, an estimate:
  - **Sonnet ≈ 0.78 × Opus** (0.55 + 0.26·0.5 + 0.19·0.5). A Sonnet step pays only if the team's overhead (plan, step
    briefs, re-reading, lead review) adds under ~29% more tokens. The A/B shows +25% to +98%.
  - **Haiku ≈ 0.04 × Opus.** A Haiku step pays even when it is redone twice and reviewed.
  - **Codex** costs nothing in the Claude window: it pays wherever the Claude subscription is the scarce one.
- **Fix** (the target shape; the pricing code is being changed in another session).
  1. Add a provider and model profile as data beside Link.exe, the same idea as the harness texts: prices (in, out,
     cache write, cache read), billing (`subscription` with its window, or `api`), context window, cache TTL, vision,
     the effort vocabulary, structured-output support. A custom model gets a row, not code.
  2. Cost per run = tokens × price, keeping cache writes apart. Prefer the CLI's own `cost_usd` where it reports one.
     Subscription runs also keep the window points they used (`AgentLimits.Spent`, already recorded).
  3. `WorkerEvidence` gains the median cost and tokens per accepted step by kind × worker.
  4. `WorkerRouting` picks the cheapest worker whose evidence for the kind is proven (or not yet blocked). It counts a
     redo at its measured rate and a review at its measured cost. The reason in words names the numbers, as it does
     for evidence today.
  5. `TeamRunner.Suggests` stops suggesting a team for cost alone when the cheaper tier would be Sonnet on the same
     subscription. A team still fits multi-tool work, Haiku-class or Codex steps, and the user's choice.
- **Effect.** Cost: the team and cheap workers are used exactly where they pay, and the expected saving is visible
  per step. Quality: unchanged by construction, because a worker must be proven first.
- **Effort.** M.

### F3. Worker evidence drops the signals that matter most

- **Problem.**
  - `WorkerEvidence.Verdict` (`WorkerEvidence.cs:52-57`) is the run's outcome: "Done" is an exit code 0 with the check
    passed, or with no check at all.
  - `TeamRunner.After` records a success before a `Check` step waits for the user's OK (`TeamRunner.cs:150`). The
    user's OK (`MainWindow.Team.cs:546`) and their "run it again" (`TeamRunner.RunAgain` from
    `MainWindow.Team.cs:583, 641`) record nothing.
  - `LastAt` is written and never read: evidence never ages.
  - The key is the configured model (`WorkerEvidence.cs:110`), often an alias such as `sonnet` or null. Evidence then
    carries over a model release, which is exactly when harness assumptions go stale. Anthropic reports a context-reset
    workaround needed by Sonnet 4.5 that became dead weight on Opus 4.5
    ([Anthropic engineering](https://anthropic.com/engineering/managed-agents)).
- **Fix.**
  1. Record a failure when the user sends a step back. For `Check` steps, record success at the user's OK, not before.
  2. A "done" with no check and no review counts as unverified: half weight, or not counted.
  3. Key by the resolved model the CLI reported (`AgentResult.Model`).
  4. Count only the last 10 records per kind × worker, or the last 60 days.
- **Effect.** Quality: blocking and proving follow the user's judgement. Cost: proven cheap workers are found sooner
  and are not over-trusted after a model change.
- **Effort.** S (with tests: `WorkerEvidence` and `TeamRunner` are pure).

### F4. Verification rests on a model's judgement; numbers are optional

- **Problem.**
  - Link's own check is the user's `CheckCommand` (`AgentCoordinator.BuildTask`, `AgentCheck.RunAsync`). Most projects
    have none, so "done" means the run ended well.
  - Art is accepted or sent back by the critic from up to 6 images (`ArtCritic.cs:22`, `art-review.md`).
  - The lead's review reads a diff.
  - A weaker critic misjudges images, and every redo it causes costs an artist session on Deep. Each attached frame
    stays in the critic's context for every request.
  - The Blender pack is gaining a numeric review (`blender-review/check.py`, "numbers before frames": floating islands,
    non-manifold edges, inverted islands, scale, size and triangle budget). Nothing in Link runs it or gates on it yet.
- **Evidence.** Code above. OpenAI's write-up of its Codex harness checks design and architecture rules mechanically,
  by linters, CI and structural tests, rather than by prose
  ([InfoQ on OpenAI's harness engineering](https://www.infoq.com/jp/news/2026/03/openai-harness-engineering-codex/)).
- **Fix.** Add a pack check contract.
  1. A pack may ship `checks/<kind>` commands. Blender: `check.py` against the asset's targets from the step or the art
     profile. Unity: `unity-verify` compile plus console errors. Godot: a headless import and script check.
  2. Link runs them itself after an art or code step, like `CheckCommand`, and gives the worker the 5–10 line summary.
  3. The critic and the lead get the summary first. A failing number blocks "accept" without a model's say.
  4. The critic then judges only what numbers cannot (silhouette, style), from one sheet.
- **Effect.** Quality: a Haiku-class or custom critic or lead can work, because the hard part is deterministic. Cost:
  fewer redos and fewer image tokens.
- **Effort.** M (Blender: S once `check.py` lands; the contract and the runner are shared).

### F5. The rules assume a strong model; Link's blocks are fragile

- **Problem.**
  - `agent-rules.md:1-6` writes only what the model "does not know by itself" and leaves out looking around, planning
    and checking. That is right for Opus and GPT-5.x and wrong for a Haiku-class or small model.
  - Six output contracts (`link-ask`, `link-next`, `link-plan`, `link-review`, `link-exam`, `link-lessons`) are JSON
    in fenced blocks read by regex (`TeamPlan.cs:273-276`, `AgentNext.cs:40`).
  - A missing or invalid block on a review goes to the user as "ended without a verdict" (`MainWindow.Team.cs:305`),
    with no repair attempt.
  - "Get stronger with each task" (`agent-rules.md:71-84`) invites every run to write the map and skills. A weak model
    writes noise there that every later session pays for.
- **How a weaker model fails, and what catches it today:**

  | Failure | Caught now by | Gap |
  |---|---|---|
  | Skips `docs/map.md` and the "Read first" skills | nothing | F7 telemetry; inline a short map for Light |
  | Edits without a plan, wrong approach | `AutoEffort` raises the effort after a failure | no forced plan for weak tiers |
  | Says "done" without checking | `CheckCommand` if set; else exit 0 counts as done | F3, F4 |
  | Repeats the same failing command | the loop label | no steering (F8) |
  | Malformed or missing `link-*` JSON | nothing: the user is asked | structured output plus repair |
  | Writes wrong "lessons" into skills or the map | the lead's diff review, in a team only | gate writes for weak tiers |
  | `cat` of whole big files, huge logs | a rule line | F8 telemetry, hints |
  | Weak vision | another agent as critic | F4 |
- **Fix.**
  1. Constrain the final output of judge runs (plan, review, critic, exam, lessons). Claude Code has `--json-schema`
     ([CLI reference](https://code.claude.com/docs/en/cli-reference)) and Codex has `codex exec --output-schema`
     ([Codex non-interactive](https://developers.openai.com/codex/noninteractive)). Third-party write-ups say it does
     not yet combine with `exec resume` (Codex issue #14343; verify on the installed version), so a resumed Codex judge
     falls back to the block.
  2. Everywhere else: when a block is missing or invalid, resume once with "Answer with only the ```link-… block" at low
     effort, before asking the user.
  3. Add `rules-light.md` (≤ 25 lines), appended for Light-tier and custom-model runs: read the map, a three-line plan,
     the smallest change, run the check, then a fixed report. Its prefix stays stable per tier, so it caches.
  4. Light runs do not write the map or skills. They name what they would keep in the answer, and the lead or Reflect
     writes it.
- **Effect.** Quality: fewer dead ends with weaker models and less memory noise. Cost: a repair turn costs a few
  hundred tokens against a user round-trip.
- **Effort.** S (repair turn, light rules), M (schemas for both CLIs).

### F6. The fixed start of a session: whole skills read up front, pack skills unchecked

- **Problem.**
  - "Read first" makes the agent read up to 4 whole SKILL.md files at every new session (`ProjectRoles.cs:41, 218`).
  - Pack skills are exempt from the size note (`HarnessView.cs:208`, `skill.Pack is null`): `ue-mcp-animation` is 182
    lines and 11.6k chars, past the 150-line budget.
  - 11 skill names that pack roles declare do not exist in the repo's packs: `unreal-verify`, `unreal-blueprint`,
    `unreal-mcp`, `unreal-animbp`, `unreal-import`, `unreal-umg`, `unreal-level`, `unreal-package`,
    `unreal-profiling`, `cascadeur-anim`, `figma-ui`. `OwnSkills` skips them silently, so an Unreal gameplay
    developer's "Done when" may lean on a skill it never gets.
- **Fix.**
  1. Write each skill as a core of about 60 lines that says when, how and the pitfalls, plus `reference/*.md` files
     opened on demand. This is the progressive disclosure the Agent Skills format intends.
  2. "Read first" names only the role's primary skill. The rest stay in the listing by description.
  3. The size check covers pack skills too.
  4. The inspector flags missing declared skills.
- **Effect.** Cost: about −2–4k tokens per specialist session (estimate), more in Unreal. Quality: weak models follow a
  short core better than a long one.
- **Effort.** S (code), M (condensing the existing skills).

### F7. Memory grows without usage-based pruning

- **Problem.**
  - Every task may append to `docs/map.md` and skills (F5).
  - The budgets are notes in the inspector: 120 map lines, 150 skill lines (`HarnessView.cs:70-71`).
  - Nobody records which skill, map section or memory note a run actually read.
  - Claude runs also get the workspace's auto memory, whose `MEMORY.md` index loads on every Claude request. Codex
    has no counterpart, so the same task carries different memory by agent.
  - `workers.json` never ages (F3).
  - Training lessons are the exception: an exam proves them first.
- **hx comparison.**
  - The reflect skill archives memory notes that were not opened in two weeks, from a digest of the transcripts
    (`~/.claude/skills/reflect/SKILL.md` §5a, `bin/mem_archive.pl`).
  - Its lesson: measure before promising. On 16.10 it promised ~0.5% from the index size and got ~0.05%, because 84
    of 112 notes had been opened.
  - Every reflection starts by checking whether earlier changes worked (§1).
- **Fix.**
  1. Usage telemetry from events Link already parses: which `SKILL.md`, `reference/*`, map and memory files each run
     read (Claude `Read file_path`; Codex `cat` / `Get-Content` / `rg` commands). Per-skill counters in the project's
     data: reads, tasks, last read, and outcome of the tasks that read it.
  2. A Link **Reflect**, the local one the parent session plans, run after N tasks or weekly. A deterministic digest
     script does the numbers, one Strong run does the judgement, and it proposes:
     - move a skill not read in 30 days or 20 tasks to `reference/`, out of the listing;
     - condense a skill or map over budget;
     - drop or age stale evidence;
     - change routing thresholds.
     Each proposal carries the numbers and the expected saving. The user approves, as with Share.
  3. The next Reflect checks each applied change's metric first.
- **Effect.** Cost: the session start stops growing with the project's age. Quality: skills stay short and current.
- **Effort.** M.

### F8. Loops are marked, not steered; the signature has no result

- **Problem.**
  - `AgentLoopDetector` "only marks the run" (`AgentLoopDetector.cs:6-9`). The 2026-09-30 measurement had one
    33-minute loop in five tasks: a quarter of the time, before a person saw it.
  - The call key is the tool plus its arguments (line 44), without the result. A poll of a changing status counts as a
    repeat; hx compares call and result hashes, and so does not.
  - Reads count as searches (`ClaudeCodeProvider` `SearchTools` include `Read`). The map plus 4 "Read first" skills
    plus 7 files at a normal start can reach the 12-search label (`SearchesWithoutChange`, line 20).
  - Bookkeeping calls are already skipped (`TaskCreate`, `TaskUpdate`, `TaskList`, `TodoWrite`), as hx learned the
    hard way.
- **Fix.**
  1. Add a normalized result hash: digits and spaces stripped, as `Comparable` does for errors.
  2. Do not count reads of harness files (the map, skills, `docs/art/*`) as searches.
  3. Steer once per run, model-agnostically, at "repeated call" or "repeated error". Link already stops a run and
     resumes the same session with words (`AgentCoordinator.Interrupted`). Resume with a fixed nudge: "You ran X 3
     times with the same result. In 3 lines say what you know, then change approach or end with a link-ask block."
  4. Later, Claude can get the same as a Link-owned `PostToolUse` hook through `--settings` (stage 6). The cost per
     call on Windows still has to be measured.
- **Effect.** Quality and time: loops end in minutes, not half an hour. Cost: the resume reuses a warm cache.
- **Effort.** S–M.

### F9. No per-run budget except No-stop's turns

- **Problem.** `--max-turns` is passed only when a task sets `MaxTurns`, which No-stop does
  (`ClaudeCodeProvider.cs:52`). A Light worker, a judge run or a study can spend without limit until the user looks.
- **hx comparison.** hx counts calls since the user's last message. At 100 calls it says "check that you converge". At
  150 it stops only when nothing was edited in the last 50 calls; with edits it asks for a one-line status and goes
  on. Its calls exclude app bookkeeping. That gives no false stops on real work.
- **Fix.**
  1. Per tier and per kind of run, a soft budget in calls and $ from the F2 profile. Link writes the status into the
     thread and the evidence.
  2. A hard cap: Claude `--max-budget-usd`, a client-side estimate
     ([CLI reference](https://code.claude.com/docs/en/cli-reference)). Codex has no such flag, so Link stops at N tool
     calls with no change, a count it already has.
  3. The hx rule carries over as is: stop only without progress.
- **Effect.** Cost: no runaway cheap worker or judge. Quality: unchanged, since the stop needs no progress.
- **Effort.** S.

### F10. The rules live in the first user message and fade at compaction

- **Problem.** Rules, role and "Read first" go in the first user message (`CliAgentProvider.cs:217-223`). When the CLI
  compacts inside a run (`CLAUDE_CODE_AUTO_COMPACT_WINDOW`, Codex `model_auto_compact_token_limit`, or the inherited
  40% of F1), that message becomes part of a summary. A long run's later turns hold the rules only as the summary
  keeps them, and weaker models drift first. `CompactRule` helps only between runs.
- **Fix.**
  1. Claude: send the composed rules as `--append-system-prompt-file`. The system prompt is recorded per session and
     stays across resume until compaction ([CLI reference](https://code.claude.com/docs/en/cli-reference)).
  2. Codex: check whether the installed version takes developer instructions through config. If not, keep the rules
     short and the first-message form.
  3. Either way, after the CLI's compaction event, the next turn Link sends carries a ≤ 1k-character rules digest.
- **Effect.** Quality on long runs. Cost: neutral, since the cache is the same.
- **Effort.** S (Claude), S to check (Codex).

### F11. Model and CLI asymmetries in small places

- **Pack skills are never listed for Codex.** `FindsSkills => true` (`CodexProvider.cs:128`) skips the listing, but
  Codex finds only `.agents/skills` in the workspace. The pack skills in Link's folder reach Codex only through "Read
  first". Fix: list pack skills for Codex. XS.
- **A Codex study cannot read its links.** `training-learn.md` says `WebFetch`, `Read`, `curl`, `uv`. Training adds
  `WebFetch`/`WebSearch` as allowed tools, which are Claude names (`Training.cs:23`). Codex's sandbox has network only
  for Perforce or No-stop (`CodexProvider.cs:299-302`). Fix: say capabilities, not tool names ("fetch the page", "read
  the PDF"), and give Codex study runs `network_access=true`. S.
- **Auto memory exists for Claude only** (F7): the same task carries different memory by agent. Fix: treat it as a
  workspace file that Link lists for both, or turn it off for Link runs and keep facts in the map. S.
- **Vision is a property of the CLI, not of the model.** Routing and the critic choice ask
  `Capabilities.Images` of the provider (`MainWindow.Team.cs:384, 751`), so any model behind Claude Code or Codex
  counts as seeing images. Fix: take it from the F2 model profile, so that a custom model without vision never gets
  art or the critic's chair. XS.

### F12. The cache TTL is hardcoded to a Claude subscription

- **Problem.** `CompactRule.CacheLasts` = 60 minutes (`CompactRule.cs:15`). That matches Claude Code on a
  subscription, which hx's sessions show using a one-hour cache. Anthropic's API default is 5 minutes. OpenAI keeps in-memory caches for 5–10 minutes on older models, and for
  at least 30 minutes on GPT-5.6 and later
  ([OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching),
  [Azure OpenAI prompt caching](https://learn.microsoft.com/en-my/Azure/foundry/openai/how-to/prompt-caching)).
- **Impact.** A Codex session idle for 20 minutes is resumed as warm when it is cold.
- **Fix.** Take the TTL from the F2 profile, keyed by provider and auth.
- **Effort.** XS.

## 3. What hx has that LINK should adopt

hx runs inside one CLI through hooks. LINK drives two CLIs from outside, so each adoption below works from the event
stream and from stopping and resuming, which work for any agent. Hooks are an optional extra for Claude.

| hx mechanism | LINK now | Adopt? How, model-agnostically |
|---|---|---|
| Budget without false stops: calls since the user's message, stop only without edits in the last 50, app calls excluded | none outside No-stop | **Yes**: F9. Same rule from the events, a hard cap per tier. |
| Big-output hint: a tool output over 40k chars gets a note on what it costs per later call | a rule line ("read the part you need") | **Yes, as telemetry first.** Link sees each tool result's size in the stream. It records the largest per run, Reflect reports them, and a pattern ("cat of a log") becomes a rule or a skill line. Steering mid-run only through F8's stop-and-resume, or later a `--settings` hook for Claude. |
| Note refreshed before auto-compaction, returned after | `TaskState` built by code, fresh sessions by `CompactRule` | **Already better** for agnosticism: the state comes from code, not from the model. Add F10's digest after the CLI's own compaction, and fold the agent's `link-next` and hand-over lines into TaskState (partly done). |
| Skill hint: a skill's scripts run without its SKILL.md loaded → "load the skill" once | none | **Yes, cheap.** In the events, a command that runs `…/skills/<name>/…py` with no earlier read of that SKILL.md goes into the run's record. The next new session's TaskState says "read `<skill>` before using its scripts", and Reflect counts it. |
| Loop detection on call + result, app and bookkeeping tools ignored | call only; bookkeeping ignored | **Yes**: F8 (result hash, steer once). |
| Telemetry: sessions, events, metrics JSONL; version tagged for A/B | `link.log` `[Agent]` lines, run records, Export for analysis | **Yes.** One JSONL line per run: model resolved, tier, kind, tokens split (fresh, cache write, cache read, output), $, window points, outcome, check, loops, harness files read, largest outputs, Link version. Reflect and the A/B read it. |
| Weekly reflection: digest → findings → changes → report; usage-based memory pruning; past changes checked first | none (Export for analysis, the dev skill `agent-token-review`) | **Yes**: F7 Reflect. Deterministic digest, one Strong judgement, proposals the user approves, effect checked next time. |
| Gates on Stop over the session's own edited files | `CheckCommand` after Do | Already equivalent; extend with F4's pack checks. |
| Guard on generated files, EOL and BOM kept | rules lines (Unity `.meta`, PowerShell UTF-8) | **Later.** Link can diff line endings and BOM of touched files after a run and revert noise, as the Unity rules ask, deterministically instead of by rule. |

## 4. Research notes that change a recommendation

- **Both CLIs now constrain the final answer by a schema.** Claude Code has `--json-schema` and Codex has `exec
  --output-schema`. That moves Link's six blocks from "hope the model formats it" to a contract (F5).
  Sources: [Claude Code CLI reference](https://code.claude.com/docs/en/cli-reference),
  [Codex non-interactive mode](https://developers.openai.com/codex/noninteractive).
- **Claude Code has the isolation and budget switches Link needs:** `--setting-sources`, `--bare`, `--settings`,
  `--max-budget-usd`, `--append-system-prompt-file`, `--fallback-model` (F1, F9, F10).
  Source: [CLI reference](https://code.claude.com/docs/en/cli-reference).
- **Cache lifetimes differ by provider and model** (F12).
  Source: [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching).
- **Model-specific workarounds go stale.** Anthropic saw a context-reset workaround needed by one model become dead
  weight on the next. So per-model behaviour belongs in data with a "measured on" date, and evidence is keyed by the
  resolved model (F2, F3).
  Source: [Anthropic engineering](https://anthropic.com/engineering/managed-agents).
- **Enforce mechanically, not in prose.** The OpenAI Codex team's harness checks its rules with linters, CI and
  structural tests (F4). Its primary post returned HTTP 403 here, so this rests on the
  [InfoQ summary](https://www.infoq.com/jp/news/2026/03/openai-harness-engineering-codex/). The general industry
  write-ups agree on verification loops that feed failures back with diagnostics, but add nothing beyond this audit
  ([Faros](https://www.faros.ai/blog/harness-engineering)).

## 5. Proposed order of work

| Order | Work | Effort | Why now |
|---|---|---|---|
| 1 | F1 isolation (`--setting-sources`, init check, one live run), and update `PROJECT_CONTEXT.md` §3 | S | Every Claude run is affected today; it is a precondition for comparing anything |
| 2 | F3 evidence signals (user send-back, OK, unverified, resolved model, aging) | S | Pure code with tests; F2 builds on it |
| 3 | F11, F12 small asymmetries (Codex pack skills, Codex study network, vision and TTL defaults) | XS–S | Cheap; removes agent-dependent behaviour |
| 4 | F5 repair turn for missing or invalid blocks | S | Ends the commonest weak-model dead end |
| 5 | F2 price profile → cost per accepted step → routing by expected cost; `Suggests` by price (with the pricing work under way) | M | The core of "cheap with any model" |
| 6 | F8 result-hash loops and one steer; F9 budgets | S–M | Bounds a bad run's cost for any model |
| 7 | F4 pack check contract, Blender `check.py` first, then the Unity compile and console check | M | Lets weaker leads and critics work |
| 8 | F6 skill core and reference split, missing-skill flags, pack size check | S + M | Shrinks every specialist session |
| 9 | F7 usage telemetry → Link Reflect with pruning proposals | M | Keeps 8 true over time |
| 10 | F5 light rules and schemas; F10 rules in the system prompt | S–M | Finishes the weak-model path |

## 6. Measuring

Without a fixed benchmark, none of the above can be shown to work. hx tags every change with its version in telemetry,
and every reflection first checks the previous changes. LINK should do the same.

- **Benchmark.** Three real tasks: a code change with a check, a Blender asset with references, a mechanical batch
  edit. Each runs under Opus solo, a team with Codex Standard, a team with Haiku Light and, when present, one custom
  model. Two runs each, as in the A/B.
- **Metrics per task.** $ at the profile prices and window points; tokens split four ways; the user's 1–5 rating;
  check pass; steers; redos and escalations; wall time; loops; harness files read.
- **Rule.** A change ships when the quality holds and the cost falls, or the quality rises at the same cost.
  Otherwise it goes back, with the reason recorded in Reflect's backlog.
