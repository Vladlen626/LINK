---
name: blender-model
description: Model game assets in Blender by script - scale and proportions from (maybe AI-drawn) references, separate moving parts with pivots on their axes (wheels, doors, hoods, lids), retopology to a game-ready mesh, UVs and FBX export for Unity or Unreal. Use for any modeling, mesh or export work in Blender.
---

# Modeling for a game

Work through the bridge of `blender-live`; build by scripts kept in the project, so a change is a rerun, not clicks.

## Scale and references
- Metres, unit scale 1, transforms applied (`bpy.ops.object.transform_apply` or matrix math), no negative scale,
  normals outward.
- Set the real size first, from the kind of object, before any detail: a passenger car is about 4.2-4.7 m long,
  1.7-1.9 m wide, 1.4-1.5 m high, wheelbase 2.5-2.8 m, wheel 0.6-0.7 m across. Write the numbers you chose in the answer.
- References drawn by a model (AI) are inconsistent between views and break proportions: take the silhouette and the
  character from them, take sizes, symmetry and mechanics from the real thing. Model one half and mirror it. Name in
  the answer what you corrected against the pictures.

## Parts that move
- Each moving part is its own object: wheels, doors, hood, trunk lid, steering wheel. Its origin sits on its axis of
  motion (wheel centre, door hinge line, hood hinge), and its local axes line up with that motion (the wheel spins
  around its local X), so the engine turns it by one rotation with no offset.
- Steered wheels: an empty at the wheel centre steers (around up), the wheel mesh under it spins (around its axle),
  so the two rotations never fight. Names say the place: `Wheel_FL`, `Wheel_FL_Steer`, `Door_L`, `Hood`, `Trunk`.
- Hierarchy: the body is the root, parts under it; no part parented to another moving part unless it moves with it.
- Prove it before going on: a script turns each part to its limits (wheel steer ±35°, door 0-70°, hood and trunk to
  their open angle) and reports intersections with the body (`mathutils.bvhtree` overlap, or frames from `shots.py`),
  then puts them back. Write the limits into the answer; the engine side needs them.

## Retopology and the game mesh
- After the parts work: a low-poly game mesh within the budget from the task or `docs/map.md` (a hero car often
  30-80k triangles, a prop far less). Keep loops on silhouettes and panel gaps, flat areas sparse; no n-gons left at
  export, no hidden inner faces.
- Bake detail from the high mesh into a normal map when it carries the look; keep the high mesh in the .blend.
- UVs: no overlaps except deliberate mirrors, even texel density, seams along hard edges and gaps; power-of-two
  textures.
- LODs only when the task asks.

## Export
- FBX of the asset's objects only, modifiers applied, Apply Scalings `FBX All`, Add Leaf Bones off, smoothing `Face`.
- Unity: Forward `-Z`, Up `Y`, Apply Transform on; in Unity the root must show rotation (0,0,0) and scale (1,1,1),
  else import with Bake Axis Conversion. Unreal: Unreal is Z-up, 1 unit = 1 cm; see the Unreal pack.
- Keep the .blend and the export script in the project; the exported FBX goes where the engine project expects it.
- Check in the engine: size against a known object, pivots where the parts turn, materials assigned, no import warnings.
