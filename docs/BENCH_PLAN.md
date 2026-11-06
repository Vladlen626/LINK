# Link Bench: a benchmark of harnesses, not only of models

What it answers: **how well does a harness let a model build a game**, and how fast and at what cost. A harness here
is what wraps the model: the Claude Code app or CLI, the Codex app or CLI, Link (its rules, guard, check after a run,
final check, compaction). The same task is run in any harness with any model, so three questions get numbers:
- the same model in different harnesses (Opus in Claude Code against Opus in Link): what a harness is worth;
- different models in one harness (Opus, Sonnet, GLM, GPT in Link): how far plan B is (`PORTABLE_HARNESS_PLAN.md`);
- a change to Link's harness before and after: kept only if the bench holds.

## Principles
- **A general game, nothing from the user's projects.** One small third-person action-adventure in the browser, in the
  spirit of the classic island adventures: a hero with a sword on a small island, pots and crates to break, grass,
  slimes, gems and hearts, a chest, a key and a door. The genre's basic mechanics, our own names and art (no names,
  characters or assets of any existing game).
- **Simple and small.** three.js for 3D (vendored as one file, never read), plain ES modules, under ~1.5k lines. The
  logic is apart from drawing, with a fixed step (`step(state, input, dt)`), so a hidden test drives it in Node and in
  the page alike. A run pays for the task's work, not for reading an engine.
- **The task is just a folder and a prompt.** Nothing in it knows who works on it, so any harness takes it as a user
  would give it: open the folder, paste the prompt.
- **Scored after the fact, the same for every harness.** The hidden checks are copied in only after the work and give
  a score 0-100 (the share of cases passed). The numbers come from the session logs every harness already writes:
  Claude Code (the app and the CLI, and so Link's Claude runs) in `~/.claude/projects`, Codex in `~/.codex/sessions`,
  each with the folder, the model, the tokens and the times. Link's runs are told apart by their session ids in
  `link.log`.
- **Nothing to install.** Node 24 runs the hidden tests (`node:test`) and drives a headless Edge or Chrome over the
  DevTools protocol with its own WebSocket: the real page loaded, keys played, the DOM and frames read, screenshots
  taken.
- **Out of the agent's sight.** Runs live in `%USERPROFILE%\LinkBench\runs\`, far from the bench's checks in this
  repository.
- **Few tokens.** A smoke set of three tasks for every harness change, the full set at milestones. One run per task by
  default; a second only for tasks that flipped.

## How a run goes
```
node bench/bench.mjs new pot-break        # a fresh copy in %USERPROFILE%\LinkBench\runs\pot-break-<n>\ (git, first commit),
                                          # the prompt printed and on the clipboard
   ... the user opens that folder in Claude Code, Codex or Link and pastes the prompt;
       a task with a follow-up prints it too, to paste after the first answer ...
node bench/bench.mjs score <folder>       # hidden checks + the sessions found for that folder -> one row in results
node bench/bench.mjs report               # the table: by task, harness, model
```
Automatic runs, for repeats without the user: `node bench/bench.mjs run pot-break --harness claude-cli --model opus`
(`claude -p`), `--harness codex-cli` (`codex exec`), `--harness link` (Link's own task through its UI Automation, the
`drive-link` skill), with the follow-up sent the same way. Another model in a harness is that harness's own choice (the
model picker, or an endpoint profile for GLM in Claude Code and in Link).

## The game the tasks share
"Island": a hero with a third-person camera that follows and avoids walls; walk, run, roll, a sword swing; pots,
crates and grass; a slime; gems and hearts; a chest, a key, a door; a HUD. It brings what a real project would to check
work: tests under `node --test`, `tools/serve.mjs` (a static server), `tools/shot.mjs` (a page's screenshot through
headless Edge), and a short `AGENTS.md` (with `CLAUDE.md` pointing to it): how to run, test and look. Whether an agent
uses them is part of what the bench sees. Each task starts from its own state of the game.

## The tasks

| # | Id | Kind | What the agent does | What it shows of the harness | Set |
|---|---|---|---|---|---|
| 1 | `pot-break` | feature | The sword breaks pots (one hit) and crates (two): a hit arc in front, pieces that fly and settle, a seeded drop of a gem or a heart. **Follow-up:** "the pieces fall through the floor, and crates should take three hits". | following the project's ways; a correction to earlier work in the same session | smoke |
| 2 | `lift-throw` | 3D feel | Lift a pot in front of you, carry it overhead (slower walk), throw it in an arc where you face; it breaks where it lands and hurts a slime. | 3D math; looking at the result, not only testing it | smoke |
| 3 | `roll-wall` | bug | A player's report: "at low frame rate the roll goes through thin fences, and sometimes sticks to walls". | diagnosis: searches before the first edit, a cause found, not a patch | smoke |
| 4 | `lock-on` | 3D camera | Target lock: the nearest slime in a cone in front, the camera frames hero and target, the hero strafes round it, switch and release. | 3D reasoning; checking its own numbers | full |
| 5 | `slime-spec` | mechanic | A slime from a one-page design: hops, notices you in sight within 6 m, chases, lunges after a 0.5 s tell, knockback and blink on a hit, two hits to defeat, a drop. 12 rules, a hidden test each. | following a spec to the letter | full |
| 6 | `grass-perf` | performance | 20,000 grass blades: each swing tests them all and the field is rebuilt every frame; the budget is 60 fps on the bench page. | measuring before optimising | full |
| 7 | `dungeon-room` | multi-part | A room: push a block onto a switch to open a gate, a chest with a small key, a locked door, the room's state saved and loaded. Four parts, each checked in the page. | a plan with a check per part; finishing; the final check | full |
| 8 | `new-item` | skills | "Add a heart piece: four make a new heart." The project's skill says items go through `tools/gen-items.mjs`, which regenerates `items.gen.js`; the check knows a hand edit. | reading and following skills | full |
| 9 | `hud-taste` | judgment | "Make the HUD feel cosier", with nothing that says what that is. Passes when the agent asks or offers options before a large change. | asking instead of guessing | full |

Tasks 1, 2, 4 and 7 also leave a frame sheet from fixed angles for the user's 1-5 mark; a judge model can give it
instead, at its cost.

## What each run records
- **Result:** the hidden score; the user's 1-5 mark where a sheet exists.
- **Speed:** wall time; time to the first edit; time the user spent answering.
- **Cost:** input, cached and output tokens; dollars where the log says.
- **The user's part:** messages the user had to write beyond the prompt and the follow-up; questions asked.
- **Process:** turns, tool calls, searches before the first edit, tool errors, files and lines changed, whether it
  ran the tests, served the page or took a screenshot, loops, the guard's refusals, helper calls.
- **Setup:** harness (Claude Code app or CLI, Codex app or CLI, Link), model, effort, the bench's and Link's commit.

## Layout
```
bench/
  bench.mjs      new, score, report, run (Node, no packages)
  game/          Island, the shared game
  tasks/<id>/    task.json (set, time limit, what it measures), prompt.md, followup.md (optional),
                 start.patch (the task's state of the game), check/ (*.test.mjs, page.mjs), sheet.mjs (optional)
  lib/           the page driver (DevTools protocol), the log readers (Claude Code, Codex, link.log)
results/ in %USERPROFILE%\LinkBench\: one CSV row per run, kept out of git
```

## Stages
- **B.1** `bench.mjs` (new, score, report), the log readers, the page driver, Island, tasks 1-3. Baseline by hand:
  Opus in the Claude Code app and in Link; GPT in the Codex app.
- **B.2** Tasks 4-9; `run` for the CLIs and for Link.
- **B.3** GLM in Claude Code and in Link, once the user has a Z.ai key.
- Later, if needed: a task with a broken tool (a server that does not answer); a long task that needs a compaction.

## Status (2026-10-30)
B.1 and B.2 are built in `bench/`; no agent has run on them yet.
- **Island** (`bench/game/`): ~1,040 lines of our code, three.js r170 vendored (`vendor/three.module.min.js`), 9 project
  tests, `tools/serve.mjs`, `tools/shot.mjs` (headless Edge), `tools/gen-items.mjs`, `AGENTS.md` with `CLAUDE.md`
  pointing to it. `step(state, input, dt)` takes the page's frame time (at most 0.1 s); the page exposes
  `window.island` (`state`, `step`, `view`, `frameTimes`, `ready`).
- **lib**: `driver.mjs` (DevTools protocol over Node's WebSocket; Edge, Chrome if Edge is missing or fails),
  `sessions.mjs` (Claude Code with Link's account folders, Codex, `link.log`), `kit.mjs` and `reporter.mjs` (what the
  hidden checks share, node:test to `{ score, passed, total, notes }`), `sheet.mjs` (frame sheets), `serve.mjs`.
- **Tasks**: all 9, each with `task.json`, `prompt.md`, `followup.md` (pot-break), a `start/` overlay on the game (and
  files to remove in `task.json`) instead of `start.patch`, a hidden `check/` and an oracle `solution/`; sheets for
  pot-break, lift-throw, lock-on and dungeon-room; hud-taste scores the session log, its oracle a recorded session.
- **bench.mjs**: `new`, `score`, `report`, `run`, `selftest`. `selftest` passes: every start scores below 50
  (pot-break 0, lift-throw 0, roll-wall 0, lock-on 0, slime-spec 8, grass-perf 25, dungeon-room 0, new-item 0,
  hud-taste 0), every start + solution 100, the game's own tests green in both.
- Tried by hand: `new` + `score` on pot-break with the solution copied in (100, a sheet, a CSV row; no session found,
  so empty stats); the log readers on this machine's sessions (Claude Code app, CLI and Link runs, Codex CLI): the
  numbers match Link's own `[Agent]` lines.

Untested:
- `run` was never executed (no agent was started): `claude -p` / `codex exec` with the follow-up as a resumed turn, and
  Link through `drive-link` (`--folder` of a run made by `new` and `--project`, the Link project that shows it).
- Scoring a real agent's run end to end; Codex app sessions (none on this machine); Codex logs carry no cost.
- In Link's runs, Link's own follow-up turns (the check after a run) count as user messages.
- grass-perf's 60 fps budget was measured with the GPU of this machine; on a machine without one (software WebGL)
  the reference solution may miss it.
