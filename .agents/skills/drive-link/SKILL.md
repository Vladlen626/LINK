---
name: drive-link
description: Give the user's own Link a command when the user asks for it - hand the latest task of a project to Claude Code with words added to its brief, through UI Automation. Only on the user's explicit request; never start agents unasked.
---

# Drive the user's Link

The user sometimes asks to give a task to an agent *through Link* ("дай через Link команду claude"), not from this chat.
Link is driven through UI Automation, as a user would click it. Only on such a request: an agent Claude starts
unasked is refused, and Link's agents spend the user's limits.

## Hand the latest task over to Claude Code

1. Link runs and shows the project wanted (`rebuild-link` starts it; a hidden Link is not in the UIA tree).
2. Write the user's words for the agent into a UTF-8 file (they are appended to the brief Link builds from the task).
3. First without Run, to see the card:
   ```powershell
   & .agents\skills\drive-link\handoff.ps1 -Project Bigsteps -Extra <words.txt> -NoRun
   ```
   It waits until nobody has typed for 15 s (the card takes the keyboard), opens Tasks, the newest task, Hand off ▾ →
   Claude Code · new session, and appends the words to the brief.
4. Read the card through UIA (ProviderCombo, ModelCombo, EffortCombo, AccessCombo, the end of GoalBox), then invoke
   `RunButton`. The pill (`AgentPillText`) shows "Claude · 0:10" once it runs; its `[Agent]` line comes to link.log at
   the end.

## Pitfalls

- PowerShell 5.1 reads the script as ANSI: it stays ASCII, the words come from the file.
- The newest task is the list's first row only when no Link-own run (a map) is shown above it.
- Pictures of the screen show whatever is on top; read texts through UIA instead.
