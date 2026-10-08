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

One line per session: requests, the first request's input (what rides along on every request), the last one's,
totals (cached apart), output, tool calls, failed calls, and the troubles met in Link's runs: garbled Russian, git
"dubious ownership", sandbox refusals, reads of plugin guides. Link's own `[Agent]` lines in
`%LocalAppData%\Link\link.log` give the same totals per task.

Where the time went in one session (the user says the agent was slow):

```powershell
& .agents\skills\agent-token-review\session-time.ps1 -Agent claude -Session <id part> [-Since <ISO time>] [-Top 12]
```

Span, time inside tool calls and the rest (the model, waits between runs), time by script or tool, the slowest calls.
In BigSteps (2026-10-06) most of a 3.8-hour Claude run was checks and waits for them; in Codex runs, 70-80% is the
model itself. A slow tool is fixed in the project's tooling and skill (the editor's background throttle, `localhost`
on Windows costing 2 s a connection), not by telling the model to hurry.

Logs: Codex `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (`session_meta` names the folder, `token_count` the usage
of each request); Claude Code `~/.claude/projects/<folder with - for \ and :>/<session>.jsonl` (`assistant` lines
carry `usage`, one response over several lines with the same message id).

## Read what rides along

- Codex: the rollout's first lines are the request's start — `base_instructions`, the skills list, plugin
  suggestions, `AGENTS.md` in `world_state`. Tool schemas are not there: first input minus those is the tools.
- Check a CLI's flags without running a model: `codex -c <key>=<value> features list` (bad values fail to load),
  `codex mcp list`, `claude --help`; a hidden option shows in the binary: `grep -a -o -- "--max-turns <" claude.exe`.

## Decide

Rank by share of the task's tokens × how much can go ÷ the risk to quality. In Link's runs the cost was never Link's
rules (~0.7k a request) but what the agent did: plugin guides read for 10k tokens, a 33-minute loop, files read
garbled and read again, a refused `git status`. Fix those in Link (flags, environment, one rule line), not by asking
the model to save tokens.

## Pitfalls

- No live claude or codex runs without the user's word: measure what already ran.
- Read sizes and patterns, never copy what the sessions hold.
- A Codex resume reports the session's tokens, not the run's (Link marks them "for the session").
