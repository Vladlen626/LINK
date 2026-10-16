---
name: drive-link
description: Give the user's own Link a command when the user asks for it - a new task, the latest task handed to Claude Code, or words to a task's session, through UI Automation. Only on the user's explicit request; never start agents unasked.
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
   & .agents\skills\drive-link\handoff.ps1 -Project <name> -Extra <words.txt> -NoRun
   ```
   It waits until nobody has typed for 15 s (the card takes the keyboard), opens the Studio (the side panel's Studio button), the newest task, Hand off ▾ →
   Claude Code · new session, and appends the words to the brief.
4. Read the card through UIA (ProviderCombo, ModelCombo, EffortCombo, AccessCombo, the end of GoalBox), then invoke
   `RunButton`. The pill (`AgentPillText`) shows "Claude · 0:10" once it runs; its `[Agent]` line comes to link.log at
   the end.

## Give a new task

```powershell
& .agents\skills\drive-link\newtask.ps1 -Project <name> -Goal <goal.txt> [-Agent 'Claude Code'|Codex] [-NoRun]
```
It waits for 15 s without typing, opens the Studio (the side panel's Studio button), New task, picks the agent if
asked, puts the goal from the UTF-8 file into `GoalBox`, prints the card (agent, model, effort, access, plan first,
what goes along) and presses Run. After `-NoRun` the card stays open: invoke `RunButton` once it is right.

## Send words to a task's session

```powershell
& .agents\skills\drive-link\reply.ps1 -Project <name> -Words <words.txt> [-NoSend]
```
It waits for 15 s without typing, opens the Studio (the side panel's Studio button) — the task Link shows there, or else the newest of the list — puts the
words into `ReplyBox` and invokes `ContinueButton` (Send). It prints the task's goal and whether the `Plan mode ✕`
chip shows: while it does, the session only plans, so a reply that should do the work comes after a click on it.

## Pitfalls

- PowerShell 5.1 reads the script as ANSI: it stays ASCII, the words come from the file.
- Link in the tray is not in the UIA tree: its show/hide hotkey (`Hotkeys.ShowHide` in settings.json, Ctrl+` here,
  sent with `keybd_event`) brings it back; a second Link.exe only shows a message box.
- Watch the run in the agent's own session log (`agent-token-review`), not in the editor or the workspace: the agent
  owns them while it works.
- The newest task is the list's first row only when no Link-own run (a map) is shown above it.
- Pictures of the screen show whatever is on top; read texts through UIA instead.
