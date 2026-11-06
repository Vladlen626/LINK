# A harness that outlives the model (plan B)

The user's aim (30.10): if Claude goes away, plug in another model (GLM, say) and work as well as before. That means the
harness's quality must live in code and in the repository, not in text tuned to one model. And "as well" must be
measured on general tasks, not felt.

## What the research says

- **Agent = model + harness.** Controls are of two kinds: *computational* (tests, linters, hooks: fast, certain, any
  model) and *inferential* (rules text, an LLM judge: slow, model-dependent). Guides without sensors are wallpaper: a
  rule a script can hold belongs in a hook, a linter or a check that refuses the wrong result, not in a sentence.
  ([Addy Osmani](https://addyosmani.com/blog/agent-harness-engineering/), [Faros](https://www.faros.ai/blog/harness-engineering),
  [CodeMySpec](https://codemyspec.com/blog/the-harness-layer), [HumanLayer](https://humanlayer.dev/blog/skill-issue-harness-engineering-for-coding-agents))
- **Codex now has Claude Code's hooks.** Stable since v0.124 (here 0.154, `hooks stable true`): PreToolUse, PostToolUse,
  Stop, PreCompact and more; it blocks with `hookSpecificOutput.permissionDecision: "deny"` or exit 2, covers `Bash`,
  `apply_patch` (as `Edit`/`Write`) and MCP tools, has `commandWindows`, and runs a new hook only once it is trusted
  (`--dangerously-bypass-hook-trust` for automation). ([Codex hooks](https://learn.chatgpt.com/docs/hooks))
- **GLM runs inside Claude Code.** Z.ai's endpoint speaks Anthropic's API: `ANTHROPIC_BASE_URL=https://api.z.ai/api/anthropic`,
  `ANTHROPIC_AUTH_TOKEN`, and the Opus/Sonnet/Haiku slots mapped to `glm-5.3[1m]` / `glm-5.3-flash[1m]`. Claude Code's
  whole harness (hooks, skills, subagents) stays. ([Z.ai docs](https://docs.z.ai/devpack/tool/claude)) GLM-5.3 is near
  Opus on terminal benchmarks by Z.ai's own numbers, behind it in independent ones
  ([Vals](https://www.vals.ai/comparisons/anthropic_claude-opus-4-8-vs-zai_glm-5.3)): only our own eval tells what it
  does with our harness.
- **Evals are a task folder, a goal and a hidden test.** Harbor (Terminal-Bench's framework) runs Claude Code, Codex,
  Aider and others on such tasks, in Docker. ([Harbor](https://harborframework.com/llms-full.txt)) Linux containers
  do not fit Windows-only work (Unity, PowerShell); Link's own runner over its providers does.

## Where Link stands

| Already code (any agent) | Code for Claude only | Still text |
|---|---|---|
| Access, backup, undo, Stop; the project's check after a run with resume; the final check; HarnessLint; Compact by phase; the loop flag in the log | Link.Guard (PreToolUse): big file read whole, temp files in the project, long waits, PowerShell encoding. Codex gets `guarded-rules.md` | `agent-rules.md`: plan with `check:`, link-ask / link-next blocks, scripts re-sent whole, images opened twice, hand-made assets, stuck twice, map and skills upkeep, how to answer |

## Stages

Each stage is kept only if the eval holds or improves. Runs spend the user's limits: the eval runs only when the user
starts it.

### P.1 Link Bench
Our own benchmark of harnesses: a small action-adventure built in the browser, the same task run in the Claude Code
app, the Codex app or Link, with any model, scored by hidden checks and the harnesses' own session logs:
`docs/BENCH_PLAN.md`. Done when the smoke set has its baseline (Opus in Claude Code and in Link, GPT in Codex).

### P.2 Model profiles (plan B in a day)
- An agent setup gets an endpoint profile: base URL, the key from Windows Credential Manager (never settings.json or
  the log), the model for each slot. Link passes them as the run's environment. First profile: Z.ai GLM in Claude
  Code. Settings → Agents shows which profile a task ran on; the run's record keeps it.
- Check what does not carry over: Anthropic's server tools (web search), images, `--effort`.
- Done when: the eval runs on Claude Code + GLM, with its table beside the baseline.

### P.3 The guard for Codex
- Link writes the same `Link.Guard` hook into the Codex run's config (inline `[hooks]` via `-c`, or a run-scoped
  `hooks.json`), with trust bypassed for Link's own hook only. Check Codex's hook input against the guard's
  (`tool_name`, `tool_input`; `apply_patch` arrives as `Edit`/`Write`).
- `guarded-rules.md` stops being sent to Codex.
- Done when: the guard tests pass for both input shapes and eval task 10 passes on Codex.

### P.4 Protocols checked, not trusted
- The `link-ask` and `link-next` blocks are already plain text any model can write, and GLM inside Claude Code keeps
  Claude Code's task list. An MCP server for them would add a process to every run for little gain, so not now.
- Instead Link checks them: a block that does not parse, a plan step without `check:`, an answer that ends in a
  question without a block — Link sends the session back what is wrong, once, without the user.
- Done when: the bench's `menu-taste` and `menu-flow` pass on every setup.

### P.5 Sensors during the run
Hooks that every CLI with hooks runs (Claude Code, Codex), each one leaving its sentence in `agent-rules.md`:
- **Search loop:** PostToolUse counts reads and searches since the last change; past a limit it adds context ("15
  searches without a change: say what you know and act"). Today the loop is only flagged in the log after the run
  (the 30.10 hands run: 249 turns, 126 searches).
- **Re-sent script:** PreToolUse refuses a shell command with a heredoc longer than a screen: write it to a file.
- **Image twice:** refuses a second Read of the same image in a session.
- **Hand-made asset:** refuses Write or Edit of a tracked binary asset (`.fbx`, `.anim`, `.png`, `.unity`, ...) the
  user committed: make yours beside it.
- **Plan without checks:** when the plan is set, a step without `check:` is sent back.
- Done when: each sensor has its test and eval task, and `agent-rules.md` is shorter by those rules.

### P.6 What stays text
Only judgment: when to ask, what done means for taste, how to answer. Every other line has moved to code or a skill.
The eval is rerun on all setups; the table says how close plan B is.

## Order
1. **P.1** the bench's smoke set and its baseline: without numbers nothing after it can be judged.
2. **P.5 + P.3** sensors and the guard for Codex: one exe (`Link.Guard`, PreToolUse and PostToolUse), state kept per
   session in `%TEMP%` by the hook's `session_id`. The search-loop sensor first: it is the costliest waste seen.
3. **P.2** GLM: in the bench first (an environment profile of the runner), in Link's settings only if it holds up.
   Needs a Z.ai key, which the user gets.
4. **P.4**, then **P.6**: the rules text shrinks by what moved into code; the full bench on every setup.

## Not now
- An agent loop of our own: the CLIs' inner loops (tools, edits, context, caching) are better than one we would write.
- OpenCode as a third CLI: only if Claude Code and Codex both stop working with another model. Its plugins can hold the
  same guard (`tool.execute.before`), so P.3–P.5 carry over.
