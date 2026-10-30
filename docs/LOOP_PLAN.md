# One loop instead of Team

Source: Anthropic's article [Building agents: when to fold subagents into your main agent loop](https://claude.com/resources/articles/building-agents-when-to-fold-subagents-into-your-main-agent-loop)
(22.10.2026), and the user's own observation (29.10): Team does not run in parallel. In practice it is a plan plus a
hand-over of the session at every step, where it should drop the skills a step does not need.

## Why

Team runs each step as a new session. Every step pays again for the rules, the map, the skills and the files, without
a cache. The worker sees only the task's state, its step and a retelling of the step before it. Then the lead reviews
the step in a session of its own, which grows with each review. The plan is fixed up front, so the worker cannot change
it when the work shows it wrong. The one thing a new session per step buys, leaving earlier context behind, one
session gets from progressive disclosure (skills are only described until read) and from Compact at the end of a
phase.

What Team did well stays:
- cheap models for mechanical work;
- the second subscription (Codex);
- a separate verifier;
- the user's OK on taste.

## What is built (29.10)

| # | What | Where |
|---|---|---|
| L.1 | The plan is the agent's own task list, written in the first turn and kept current. It finishes and checks one part before the next. A result only the user can judge ends the turn with a `link-ask`. Team is off by default: Settings → Agents → **Offer the team (old way, to compare)**. The old `Enabled` key is not read, so it is off for everyone. | `docs/harness/agent-rules.md` → "A big task"; `TeamSettings.Offered` |
| L.2 | Claude Code runs get Link's subagent `mechanic`, on the Light model, for mechanical edits described exactly; Claude Code's own Explore does searches. Its steps show in the thread; its words and its own task list do not. Not in a plan first, a judge's run or Link's own answers. | `ClaudeCodeProvider.HelperAgents` (`--agents <file>`), `docs/harness/helpers.md`, Settings → **Helpers for Claude** |
| L.3 | Codex as Claude's helper. Codex 0.154 has no `mcp-server`, so since 30.10 the run calls `link-codex` from Bash instead: a script in `%TEMP%\link-bin`, first on the run's PATH and allowed as `Bash(link-codex:*)`. It runs `codex exec` (workspace-write sandbox, asks nothing, no desktop add-ons, git's safe folder) with the brief on stdin, and prints Codex's last message and its session. `link-codex resume <session>` sends the work back. Large, clear parts of the plan go to the ChatGPT subscription. | `ClaudeCodeProvider.CodexHelper`, `docs/harness/codex-helper.md`, Settings → **Codex as Claude's tool** |
| L.4 | Compact at a phase's end. A reply after a run that finished part of its plan, with no step in progress and steps still to do, starts from the task's state once the session holds half its limit or more. Before this, a reply to a run that had just ended was never compacted. | `CompactRule.PhaseEnd` |
| L.5 | Final check. When the task's own plan of 3 or more steps is done with changes, Link offers **Check the work**: a fresh, read-only session on the Strong level reads the diff against the plan and ends with `Verdict: clean` or `Verdict: fix`. **Fix them** sends the findings to the session that did the work. It is offered once per plan. | `FinalCheck`, `docs/harness/final-check.md`, `MainWindow.Changes` |

The Harness inspector has a **Helpers** section that names what the session can hand work to.

## Built at home (30.10), from the first real runs

The first runs on the new build (DriverTheSlop, 30.10) were 120 and 78 turns with no hand-over at all: 0 mechanic,
0 Explore, 0 Codex, no task list. Searches stayed in the main session: one run made 17 before its first change and
was flagged as a loop.

| # | What | Where |
|---|---|---|
| L.7 | Every run counts its hand-overs (`helpers N` in the `[Agent]` line, `HelperCalls` in the run's record and export): the article's "was the right tool used", which the eval reads. | `AgentStats.HelperCalls`, `CliAgentProvider.LogStats` |
| L.8 | A workspace above its repository (`DriverTheSlopMain` holds `DriverTheSlop/.git`) tells the agent where git lives. Before this the first git command failed with exit 128, and about 35 commands began with a `cd`. | `AgentChanges.RepositoriesInside`, `AgentRules.Before` |
| L.9 | The final check follows the Claude Code team's review (15.10): every finding is tagged `Important`, `Nit` or `Pre-existing` and gives its file and line. An Important finding names its kind, so every place of it gets fixed. Only Important means `Verdict: fix`. The check reads the project's `REVIEW.md` if there is one. | `docs/harness/final-check.md`, `FinalCheck.Fix` |
| L.10 | A shorter start: the rules say the same in about a third fewer words ("Project and environment", "Get stronger", "How to answer"). HarnessLint flags a skill description over 300 characters, since every session reads all of them; in DriverTheSlop they ran 300–870 characters, 6.5k of the 31k-character prompt. | `docs/harness/agent-rules.md`, `HarnessLint.DescriptionLength` |

| L.11 | One map. If a repository in the workspace keeps its own map (`docs/map.md` or `docs/README.md`, in git for the whole team), that is the map: the rules name it, and skills that say `docs/map.md` mean it. Link's own map is then neither read nor offered (Project tab, Spend tips, Harness). DriverTheSlop: `DriverTheSlop/Docs/README.md`, the team's router. | `ProjectMap.Find`, `ProjectMap.Shared`, `AgentRules.Before` |
| L.12 | The plan's checks: each part names how it is checked ("check:"). The final check grades every step `done`, `failed` or `unverified`, with evidence. Where a result shows only in the running game, it runs the game through the project's skills and looks. A problem it found stays a finding (Anthropic's harness design, 24.03: a checker that only reads the code, or talks itself out of a finding, passes broken work). | `docs/harness/agent-rules.md`, `docs/harness/final-check.md` |
| L.13 | Telegram and the other devices: when a plan is done, the alert says "The plan is done: check the work?", and the card carries the button. | `MainWindow.Agent` (alert) |

No Stop hook: Link already runs the project's check after a run and continues the same session with its output
(`AgentCoordinator.RunAsync`, two retries). That is the same loop, but every project's check command is empty. For
DriverTheSlop the check is `"C:\Program Files\Git\bin\bash.exe" tools/unity/refresh.sh`: it refreshes the open
editor and exits 1 on a failed compile.

Checked and left as is: the art critic resumes its Codex session after a redo. The log's 236k → 459k → 546k are the
session's running totals; the runs themselves cost 236k, 222k and 87k, less than fresh sessions would.

## Still to verify in real runs

1. **`--agents` with a file:** Claude Code here is 2.1.286, so the file form is supported.
2. **`link-codex`:** checked here with a stand-in for Codex, not with real Codex. In a run, the Console shows
   `Bash: link-codex …`, and the `[Agent]` line counts it in `helpers`.
3. **Whether Claude actually delegates:** watch `helpers` in link.log. If it stays 0 on big tasks, the next lever is
   the helper's description, one change at a time, not more rules.
4. **The final check:** its card appears after a plan of 3 or more steps, and Fix them goes on in the work's session,
   not the checker's.

## How to measure (L.6, at home)

As the article says: a small end-to-end eval, one change at a time, and keep only what holds up.

1. **Pick 10 golden tasks** from the test game, the kinds Team was used for: 3 code, 2 bug, 2 art (asset), 1
   integration, 1 refactor, 1 docs/mechanical. Write each brief once; both sides get the same text.
2. **Run each task twice** in each setup: one loop (the defaults now), and Team (**Offer the team** on, Team chosen on
   the card). The article's own sample broke only in one run of two, so a single run misses failures.
   **Measure team vs solo** can alternate the two for you.
3. **For every run, write one row:**

   | Task | Setup | Run | Done? (your 1–5) | Broke a rule? | ≈ tokens | Claude 5 h % | Codex % | Time | Your stops |
   |---|---|---|---|---|---|---|---|---|---|

   ≈ tokens and the windows come from the Spend column (`Ctrl+Shift+U`); the time from the task header. Export for
   analysis gives the rest.
4. **Compare the tasks that flipped,** not only the averages. A task one setup passes and the other fails says more
   than a score that moved.
5. **Decide:**
   - One loop is as good or better on quality and cheaper: delete Team (`TeamRunner`, `MainWindow.Team`,
     `team-task.md`, `team-review.md`, the card's Team box).
   - Team wins on some kind (art, say): keep the subagent only for that kind, as a tool of the main session, not as a
     hand-over.
6. **Change one thing at a time** (the mechanic's model, Codex as a tool on or off, the phase share) and rerun the same
   10.

## Next, by what the measurement shows

- The art critic as a verifier of one-loop art checkpoints, not only of Team's steps.
- The share at which a phase ends in a Compact: 50% of the limit now (`CompactRule.PhaseShare`).
- "Read first": the role's skills are still listed to read first. The article prefers the descriptions alone.
