# One loop instead of Team

Source: Anthropic's article [Building agents: when to fold subagents into your main agent loop](https://claude.com/resources/articles/building-agents-when-to-fold-subagents-into-your-main-agent-loop)
(22.10.2026), and the user's own observation (29.10): Team does not run in parallel. In practice it is a plan plus a
hand-over of the session at every step, where it should drop the skills a step does not need.

## Why

Team runs each step as a new session. Every step pays again for the rules, the map, the skills and the files, without
a cache. The worker sees only the task's state, its step and a retelling of the step before it. Then the lead reviews
the step in a session of its own, which grows with each review. The plan is fixed up front, so the worker cannot change
it when the work shows it wrong. The one thing a new session per step buys, leaving earlier context behind, one
session gets from progressive disclosure (skills are only described until read) and from Compact at the end of a
phase.

What Team did well stays:
- cheap models for mechanical work;
- the second subscription (Codex);
- a separate verifier;
- the user's OK on taste.

## What is built (29.10)

| # | What | Where |
|---|---|---|
| L.1 | The plan is the agent's own task list, written in the first turn and kept current. It finishes and checks one part before the next. A result only the user can judge ends the turn with a `link-ask`. Team is off by default: Settings → Agents → **Offer the team (old way, to compare)**. The old `Enabled` key is not read, so it is off for everyone. | `docs/harness/agent-rules.md` → "A big task"; `TeamSettings.Offered` |
| L.2 | Claude Code runs get Link's subagent `mechanic`, on the Light model, for mechanical edits described exactly; Claude Code's own Explore does searches. Its steps show in the thread; its words and its own task list do not. Not in a plan first, a judge's run or Link's own answers. | `ClaudeCodeProvider.HelperAgents` (`--agents <file>`), `docs/harness/helpers.md`, Settings → **Helpers for Claude** |
| L.3 | Codex as Claude's tool: `codex mcp-server` (workspace-write sandbox, asks nothing, no desktop add-ons) joins the run's MCP config, and its two tools are allowed. Large, clear parts of the plan go to the ChatGPT subscription. | `ClaudeCodeProvider.CodexTool`, `docs/harness/codex-helper.md`, Settings → **Codex as Claude's tool** |
| L.4 | Compact at a phase's end. A reply after a run that finished part of its plan, with no step in progress and steps still to do, starts from the task's state once the session holds half its limit or more. Before this, a reply to a run that had just ended was never compacted. | `CompactRule.PhaseEnd` |
| L.5 | Final check. When the task's own plan of 3 or more steps is done with changes, Link offers **Check the work**: a fresh, read-only session on the Strong level reads the diff against the plan and ends with `Verdict: clean` or `Verdict: fix`. **Fix them** sends the findings to the session that did the work. It is offered once per plan. | `FinalCheck`, `docs/harness/final-check.md`, `MainWindow.Changes` |

The Harness inspector has a **Helpers** section that names what the session can hand work to.

## Not verified in the cloud (check at home)

1. **`--agents` with a file:** the file form needs Claude Code 2.1.281 or later; Link's comments name 2.1.286. With an
   older CLI the run exits at start, so turn Helpers off.
2. **`codex mcp-server`:** the subcommand's name and its tools `codex` / `codex-reply` come from secondary sources.
   Check `codex mcp-server --help` and the tool's names in the run's Console. If the server fails, Claude Code goes on
   without it.
3. **Whether Claude actually delegates:** the Console and the thread show `Agent: mechanic · …` and `codex: codex`.
4. **The final check:** its card appears after a plan of 3 or more steps, and Fix them goes on in the work's session,
   not the checker's.

## How to measure (L.6, at home)

As the article says: a small end-to-end eval, one change at a time, and keep only what holds up.

1. **Pick 10 golden tasks** from the test game, the kinds Team was used for: 3 code, 2 bug, 2 art (asset), 1
   integration, 1 refactor, 1 docs/mechanical. Write each brief once; both sides get the same text.
2. **Run each task twice** in each setup: one loop (the defaults now), and Team (**Offer the team** on, Team chosen on
   the card). The article's own sample broke only in one run of two, so a single run misses failures.
   **Measure team vs solo** can alternate the two for you.
3. **For every run, write one row:**

   | Task | Setup | Run | Done? (your 1–5) | Broke a rule? | ≈ tokens | Claude 5 h % | Codex % | Time | Your stops |
   |---|---|---|---|---|---|---|---|---|---|

   ≈ tokens and the windows come from the Spend column (`Ctrl+Shift+U`); the time from the task header. Export for
   analysis gives the rest.
4. **Compare the tasks that flipped,** not only the averages. A task one setup passes and the other fails says more
   than a score that moved.
5. **Decide:**
   - One loop is as good or better on quality and cheaper: delete Team (`TeamRunner`, `MainWindow.Team`,
     `team-task.md`, `team-review.md`, the card's Team box).
   - Team wins on some kind (art, say): keep the subagent only for that kind, as a tool of the main session, not as a
     hand-over.
6. **Change one thing at a time** (the mechanic's model, Codex as a tool on or off, the phase share) and rerun the same
   10.

## Next, by what the measurement shows

- The art critic as a verifier of one-loop art checkpoints, not only of Team's steps.
- The share at which a phase ends in a Compact: 50% of the limit now (`CompactRule.PhaseShare`).
- "Read first": the role's skills are still listed to read first. The article prefers the descriptions alone.
