---
name: blender-procedural
description: Asset generators in Blender - one Python generator per class of game asset (rocks, crates, barrels, planks, docks, fences, pipes) with parameters, ranges, a seed and a triangle budget; build an instance, sweep seeds into a lineup, test the contract, keep each asset's record. Use when a class of asset repeats, the task asks for many variants, or the project has a generator for it.
---

# Generators: the hundredth asset is a seed

`gen.py` beside this file builds assets from a generator through the bridge of `blender-live`; `example_crate.py` is
a generator to copy into the project, not to use. The project's generators live in `tools/blender/generators/`.

## Instance, generator or unique
- **Instance**: the project has a generator for the class: seeds and a preset, no modelling.
- **Generator**: the class repeats (two or three approved assets of it, or the task asks for many) and has none: write
  one, from the approved assets of the class.
- **Unique**: a hero (the player's boat, a landmark, a machine with moving parts): the stages of `blender-model`; a
  generator would cost more than it saves.

## The contract
A generator is a file with `PARAMS`, `BUDGET`, `build(p, rng)` and, if it has invariants, `check(objects, p)` (the
format is in `gen.py`'s header). The contract matters, not how the geometry is made.
- Parameters are what an art director would change, each with its unit, range and what it changes: the size, the
  number of boards, the lean, the wear. Segments and subdivisions are not parameters: they follow from the budget.
- `BUDGET` is the class's triangle budget from the project's `docs/art/STYLE.md`. Spend triangles where they change the
  silhouette or a large patch of light or colour; one that adds only unseen smoothness is wasted.
- Every random choice comes from `rng`: nothing from `random.random()`, the time, `hash()` or the order of a set. The
  test builds seed 1 twice and compares.
- `check` holds what the class must keep at every setting: it stands on the ground at the origin, its footprint
  matches its parameter, a door fits the character.
- Build at the origin with the pivot where the engine places the asset (the base centre for a prop), by bmesh and the
  data API, not `bpy.ops` (they need a context and are slow). Faces take materials named by the palette's colours
  (`wood`, `rust`): `palette=` turns them into the atlas (skill `blender-model`, Palette).
- Compose from small blocks - a board, a lathe profile, a jittered hull, a ring of posts - kept as functions in a
  helper module beside the generators (`blocks.py`); `gen.py` imports it anew on each run and counts it in the version.
- Geometry Nodes are fine for scatter and instancing; realize the instances in the tree: an FBX carries none, and
  `gen.py` counts only realized geometry. Export applies the modifiers.

## Work with it
1. After each change of a generator: `gen.py <generator> test`. Read the rows (problems, triangles, size, open edges)
   and `deterministic`, then run the `sheet` command of the answer and read the one sheet: the default, both ends of
   every range and three seeds. Fix the generator, never one instance of it.
2. Sweep from the game camera with the colours of the game:
   `gen.py <generator> 1-12 <preset> "views=game,thumb;cam=<camera of STYLE.md>;palette=docs/art/STYLE.md"`. Put
   the gamecam frames of approved assets of the class (`Art/Approved/`) into the sheet command too: the lineup shows
   whether the new ones belong.
3. Pick the seeds that belong by the lineup, not each alone (a critic judges your picks), then build each:
   `gen.py <generator> <seed> <preset> "out=<asset>.gen.json;palette=docs/art/STYLE.md;atlas=<engine folder>/<atlas>.png"`,
   and export it as `blender-model` says.
4. Settings that proved good become a preset in `PRESETS`. An approved asset keeps its record (`.gen.json`: generator,
   version, seed, parameters, hash): it can be built again. A changed generator is another version: rebuild approved
   assets with it only on purpose, and compare their frames and hashes.
