# Team control: change, resume, skip, go back, re-plan

Status: stages 1–3 built 2026-10-15 (not yet tried in the user's Link); 4–5 open.

## Why

The character contest (Drive Slop, 2026-10-15) showed the team flow has no way out of a wrong turn except a new task:

1. The lead wrote "Contestant A, run by Codex", but a plan step could name only a tier, and Strong is Claude: Codex's
   steps went to Opus. Fixed: a step may carry `"agent": "codex" | "claude"` (team-task.md), kept on a tier up.
2. The user saw the wrong worker before the run, but nothing on the plan card changes a step's worker.
3. The user's words during the lead's review went into the review (a verdict), not into a new plan: no re-plan.
4. A failed step is a dead end: `OfferTeamGoOn` skips it, so there is no retry, skip or other worker.
5. Routing is frozen when the plan comes: a step routed while an agent was at its limit stays "nobody" or keeps its
   stand-in after the limit resets.
6. Codex's step 1 ran in its sandbox, where `uv` through the WinGet shim was denied (`denials: workspace-write`); the
   lead had planned with its own tools. The lead does not know the workers' sandboxes.

Re-queuing the task restarted the lead's plan from zero (7 min, ~$1.2) and repeated step 1. The aim: any interruption
continues the same task.

## Practice

Agent tools converge on the same few controls: the plan is an editable draft until it runs (Copilot Workspace,
Devin, Kiro's task lists: run any task, mark one done), a step can be run again or skipped, and work goes back to a
checkpoint with or without its file changes (Claude Code's rewind, Cursor's checkpoints, LangGraph's time travel:
replay or fork from a checkpoint). The rule they share: the plan's state and the files are two things, and going back
asks about the files separately.

## Design

All per-step control lives in one place: a menu on the plan card's step row (a click on its worker chip, or right
click on the row). No new buttons (see ui-minimal). The decision line under the thread names what the menu can do
when the team is stuck ("Step 3 failed: change its worker or skip it in Plan").

| Action | On | What it does |
|---|---|---|
| Worker ▸ | any step not done | Claude Light/Standard/Deep, Codex Light/Standard/Deep, the custom model, or "Link picks". The user's choice is kept on the step (reason "you chose …") and on a tier up. |
| Retry | a failed or stopped step | Failures and redos reset, a new session of its worker (or the one just chosen). |
| Skip | a step not done | Status Skipped; it hands over "Skipped by the user: nothing was done", so the next worker knows. Not counted for any worker. |
| Back to here | a done step | This step and all after it wait again; their hand-overs, notes and sessions are cleared. The files stay as they are (see stage 4), and the step's brief lists what an earlier attempt changed. |
| Re-plan… | the plan title | Words to the lead's own session (not a review): the done steps and their hand-overs stay, the lead rewrites the rest, and the new steps replace only those not done. The plan card shows them and "Run team". |

Validity rules:
- While a run is in flight only the steps after the one it works on change (worker, skip); that step and the ones
  before say "stop the run first". After a change at rest the team goes on only by the user's "Run team" / "Go on".
- A change is a thin note in the thread (`AgentEntryKind.Note`), not the user's words: agents and metrics do not
  count it. Mode toggles (No-stop) leave no line at all: their box shows them.
- Done steps never change except by Back to here; Re-plan never touches them.
- A changed worker of a step with a session starts a new one (a session of another agent cannot be resumed; the
  existing `OwnSession` check).
- Skip warns when a later step names the same files (`TeamStep.Files`) or its text names this step's hand-over.
- Every new field has a default; older task records load. Evidence counts only real runs: skipped, re-planned and
  rolled-back steps add nothing.
- When a step starts, steps still waiting whose worker cannot work now (or that have none) are routed again by the
  plan's tier, so a limit that has reset takes them back; a worker the user chose stays. Settings changed mid-team do
  not re-route a plan: the menu does that per step.

## Stages

1. **Runner** (`TeamRunner`, no UI): `SetWorker`, `Retry`, `Skip`, `BackTo`, `Reroute` waiting steps on go-on; a
   Skipped status; the failed step offered again. Tests for each rule above.
2. **Plan card menu** (`AgentPanel` plan rows → `MainWindow.Team`): the step menu and the decision line. The chip
   shows the level too ("Codex · Deep").
3. **Re-plan**: the plan title's "Re-plan…" puts the reply box in re-plan mode (a chip like Plan mode ✕); the lead's
   prompt carries done steps, hand-overs and the rest; `TeamPlan.Split` → merge into the not-done steps.
4. **Back with files**: a workspace capture before each step's first run (`AgentChanges.CaptureAsync`), so Back to
   here can offer, with the user's yes and a file list, to undo what the steps from there changed (only files clean
   before them; git/Perforce or the copy).
5. **Small**: team-task.md tells the lead that each worker runs in its own CLI and sandbox (Codex: workspace-write,
   tools outside the workspace may be denied), so steps for another agent name full tool paths; for a contest the
   critic judges blind, by the files.

Stages 1–2 cover the user's asks (swap a model, retry, skip, go back without a new task); 3 is the "rethink the plan"
button; 4 is the risky part and can wait.

Built with them (the user's asks on the same day): the user's own words are kept whole and fold in the thread past
8 lines or 600 characters (Show all ▾); run rows drop a `cd` into the workspace, show its files relative and temp
files as %TEMP%, unwrap PowerShell nested in Codex's own, and the bare "Model …" / "Session started" rows go.
