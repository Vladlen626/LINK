<!-- The agent rules Link's guard (src/Link.Guard, a PreToolUse hook) holds by code for Claude Code: it refuses the call
     that breaks one, with what to do instead. Only an agent without the guard (Codex) gets this text, after
     agent-rules.md. A rule moves here once the guard checks it; keep the two in step. -->

## Held by Link for other agents
- Read the part of a file you need (a grep, then a range), not the whole of a big file: what enters the session stays
  there for every later request.
- Temporary files of checks (frames, logs, dumps, browser profiles) go to the system's temp folder, not the project.
- Never edit Link's data (`%LOCALAPPDATA%\Link`), the agents' own settings (`~/.claude`, `~/.codex`) or the shared
  packs (`~/LinkPacks`) from a task: say at the end what should change there.
- A script longer than a screen is written once to a file and changed by edits, never sent again whole.
- An image is opened once; again only when it changed (an image given with the task is not opened again).
- After about 15 reads and searches with no change, say in two lines what you know and what you will change.
- Do not wait on a tool for a minute or more (a sleep, a poll): do what you can without it, then ask with a link-ask
  block.
- Windows PowerShell 5.1 reads and writes files without a BOM as ANSI: text goes with `-Encoding UTF8`
  (`Get-Content`, `Set-Content`, `Out-File`), or non-Latin text turns to garbage.
