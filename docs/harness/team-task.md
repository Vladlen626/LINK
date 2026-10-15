Make a team plan for this task: read what you need, change nothing. Link hands each step to a worker and shows the
user who does what. A worker sees only the task's state and its step, not this conversation: write what it needs.

End your answer with one block, nothing after it:
```link-plan
[{"step": "Model the dash prop in Blender and import it into Content/Props", "kind": "asset", "worker": "strong", "role": "3d-artist", "check": true, "why": "taste and the DCC"},
 {"step": "Rename the prop's files to the SM_ scheme", "kind": "mechanical", "worker": "light", "why": "a list of renames", "files": ["Content/Props"]}]
```
- `worker`: `strong` design, an unclear bug, taste, editor or DCC work, anything risky; `standard` usual code and tests
  by a clear description; `light` mechanical edits, renames, imports, docs, comments, descriptions.
- `kind`: design, investigate, code, test, review, asset, instance, mechanical, docs. Art: `instance` when the project
  has a generator for the class (`tools/blender/generators/`): seeds and a preset, no modelling; `asset` for a unique
  model, or a new generator once the class repeats (two or three approved of it, or many are asked for). A unique
  model is two `asset` steps: the blockout (silhouette and proportions over the references, nothing else), then the
  forms, detail and game-ready on the approved blockout: a wrong blockout fixed later means a rebuild. An art
  critic judges each asset and instance step from the frames its artist names, before the next step or the user's
  OK: it is Link's own, so plan no step for it.
- `agent`: `claude` or `codex`, only where the user named who does a step (one agent against another, "Codex
  first"); Link then runs that agent at the tier's level. Otherwise leave it out: the tier picks the agent.
- `role`: the specialist who does the step, from the list below; none fits: leave it out.
- `check`: true where only the user can judge (a model's look, an animation's feel): the team waits for their OK.
- 2–8 steps, each self-contained: what to do, where, how to know it is done. Order them as the work flows (model, then
  rig and animate, then integrate, then QA). Your own exploration is not a step.
- You stay the lead: after each step a cheaper worker does, Link shows you its answer and diff and you accept it or send
  it back. So give clear code to `standard` and clear edits to `light` rather than doing them yourself, and add no
  review steps of your own.
