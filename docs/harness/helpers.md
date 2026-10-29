<!-- What a Claude Code session of a task is told of Link's helper subagent, when Settings → Agents → Helpers is on.
     The subagent itself is defined in ClaudeCodeProvider (its description and prompt). One loop keeps the task: the
     session plans, works and decides; a helper gets a part it can do from the words it is given. -->
## Helpers
- `mechanic` (a subagent on a small model): a mechanical change you can describe exactly - renames, moves, imports, a
  repeated edit across files, docs and comments. Give it the files, the change and the check; it answers in a few
  lines. Look at its diff before you go on. Design, an unclear bug and taste stay yours.
- Explore: a search whose answer is a few lines (where is X, what uses Y) instead of reading many files into this
  session.
- A helper knows only what you write to it: the plan and the decisions stay here.
