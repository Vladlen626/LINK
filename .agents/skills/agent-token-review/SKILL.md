---
name: agent-token-review
description: Find where an agent's tokens, turns and time go (Claude Code or Codex, through Link or this session's own) from the CLIs' session logs, and decide what Link's harness should cut or fix.
---

# Agent token review

## Measure

```powershell
& .agents\skills\agent-token-review\session-stats.ps1 -Agent codex -Like <workspace folder> -Days 3
& .agents\skills\agent-token-review\session-stats.ps1 -Agent claude -Like <workspace path> -Days 3
```

What sessions cost at API prices, Claude and Codex together (the version Link pins in `UsageReport.Package`; a
Codex session is named by its rollout file, which ends with the thread id):

```bash
npx -y ccusage@20.0.34 session --json --offline --since 20261014
```

One line per session: requests, the first request's input (what rides along on every request), the last one's,
totals (cached apart), output, tool calls, failed calls, and the troubles met in Link's runs: garbled Russian, git
"dubious ownership", sandbox refusals, reads of plugin guides. Link's own `[Agent]` lines in
`%LocalAppData%\Link\link.log` give the same totals per task.

Where the time went in one session (the user says the agent was slow):

```powershell
& .agents\skills\agent-token-review\session-time.ps1 -Agent claude -Session <id part> [-Since <ISO time>] [-Top 12]
```

Span, time inside tool calls and the rest (the model, waits between runs), time by script or tool, the slowest calls.
An `Edit` or `Write` of 10-25 s is the model writing the edit (the call's text streams before it runs), not the tool.
In BigSteps (2026-10-06) most of a 3.8-hour Claude run was checks and waits for them; in Codex runs, 70-80% is the
model itself. A slow tool is fixed in the project's tooling and skill (the editor's background throttle, `localhost`
on Windows costing 2 s a connection), not by telling the model to hurry.

Logs: Codex `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (`session_meta` names the folder, `token_count` the usage
of each request); Claude Code `~/.claude/projects/<folder with - for \ and :>/<session>.jsonl` (`assistant` lines
carry `usage`, one response over several lines with the same message id); Link's runs under its other accounts log in
`%LocalAppData%\Link\ClaudeAccounts\<account>\projects\`, which the scripts search too. A team task's step goes to a
session: the newest log files of the workspace folder, one per step (a step that went on in another's shares it).

## Read what rides along

- What Link gave a run, by section and size (the user asks whether a worker gets too much):
  `prompt-sections.ps1 -Agent claude -Session <id part> [-Show "Current state"]`. A team step's prompt is the rules
  (~8k characters), the role, the skills list, the task state, the hand-overs and its step; on 2026-10-09 the state
  held every step of the plan in full (12.7k of 31k) and the step itself three times, fixed by headlines.
- A team task: `[Agent]` lines of `link.log` per step, the task's runs in `Projects\<id>\agent-tasks.json` (effort and
  `EffortReason`, files, words). Check that each step got the effort its kind needs, that a step which changed files
  shows them (the lead reviews only those), and what a worker could not verify and why (Codex's sandbox).
- Codex: the rollout's first lines are the request's start — `base_instructions`, the skills list, plugin
  suggestions, `AGENTS.md` in `world_state`. Tool schemas are not there: first input minus those is the tools.
- Check a CLI's flags without running a model: `codex -c <key>=<value> features list` (bad values fail to load),
  `codex mcp list`, `claude --help`; a hidden option shows in the binary: `grep -a -o -- "--max-turns <" claude.exe`.

## Team or one agent

`team_vs_solo.py` models a team task against one agent on the 2026-09-30 numbers: all tokens, the Claude window and
the chance of a right result without the user, by task size, with the break-even and what moves it. Its ranges are
assumptions at the top of `trial`: put measured ones there (requests of a run, a request's start and growth, review
turns) once `session-stats.ps1` or a task's Export for analysis gives them. First results: `docs/TOKENS_PLAN.md`.

## Decide

Rank by share of the task's tokens × how much can go ÷ the risk to quality. In Link's runs the cost was never Link's
rules (~0.7k a request) but what the agent did: plugin guides read for 10k tokens, a 33-minute loop, files read
garbled and read again, a refused `git status`. Fix those in Link (flags, environment, one rule line), not by asking
the model to save tokens.

## Pitfalls

- No live claude or codex runs without the user's word: measure what already ran.
- Read sizes and patterns, never copy what the sessions hold.
- A Codex resume reports the session's tokens, not the run's (Link marks them "for the session").
