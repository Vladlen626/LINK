# Link Bench: how to run it

Measures how well a harness (Claude Code, Codex, Link) and a model build a small browser game. The design and the
tasks are in `docs/BENCH_PLAN.md`. Needs only Node 24 and Edge.

## One run
1. `node bench/bench.mjs new pot-break`: a fresh folder `%USERPROFILE%\LinkBench\runs\pot-break-<n>`; the prompt is
   printed and on the clipboard.
2. Open that folder in the harness you test and paste the prompt:
   - Claude Code or Codex app: open the folder, pick the model, paste;
   - Link: add the folder as a project, start a new task with the prompt.
3. Do not help. If the agent asks, answer in a line. If `new` printed a follow-up, paste it after the first answer.
4. When it ends: `node bench/bench.mjs score <folder>`. It prints the score and the run's numbers, and adds a row to
   `%USERPROFILE%\LinkBench\results.csv`. If it prints a sheet (pictures from fixed angles), look at it and put your
   1-5 mark in that row's `mark` column.
5. `node bench/bench.mjs report`: the table by task, harness and model.

## Which tasks
- After a harness change: the smoke set, `pot-break`, `lift-throw`, `roll-wall`.
- At a milestone: all nine (`ls bench/tasks`).
- One run per task; run a task again only when its score flipped.
- Compare like with like: the same task and prompt, one setting changed (the harness or the model), a fresh folder.

## What to look at
- **score** (0-100, the hidden checks): is the work done. A task that passes in one setup and fails in the other says
  more than an average.
- **mark** (your 1-5): does it look right.
- **extra msgs**: how often you had to step in. Fewer is better.
- **wall s**, **first edit s**: speed.
- **tokens in / out**, **$**: cost.
- **turns**, **searches before edit** (in the CSV): effort wasted on wandering.

What it answers: the same model in Claude Code and in Link shows what Link's harness is worth; several models in Link
show how far a plan B (GLM, GPT) is.

`node bench/bench.mjs selftest` checks the bench itself (no agent): every task's start scores low, its reference
solution 100.
