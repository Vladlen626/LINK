<!-- The agent rules Link's guard (src/Link.Guard, a PreToolUse hook) holds by code for Claude Code: it refuses the call
     that breaks one, with what to do instead. Only an agent without the guard (Codex) gets this text, after
     agent-rules.md. A rule moves here once the guard checks it; keep the two in step. -->

## Held by Link for other agents
- Read the part of a file you need (a grep, then a range), not the whole of a big file: what enters the session stays
  there for every later request.
- Temporary files of checks (frames, logs, dumps, browser profiles) go to the system's temp folder, not the project.
- Do not wait on a tool for a minute or more (a sleep, a poll): do what you can without it, then ask with a link-ask
  block.
- Windows PowerShell 5.1 reads and writes files without a BOM as ANSI: text goes with `-Encoding UTF8`
  (`Get-Content`, `Set-Content`, `Out-File`), or non-Latin text turns to garbage.
