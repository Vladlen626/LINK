---
name: blender-model
description: Model game assets in Blender by script - scale and proportions from (maybe AI-drawn) references, separate moving parts with pivots on their axes (wheels, doors, hoods, lids), hollow bodies by booleans, retopology to a game-ready mesh, UVs and FBX export for Unity or Unreal with an engine-side check. Use for any modeling, mesh or export work in Blender.
---

# Modeling for a game

Work through the bridge of `blender-live`; build by scripts kept in the project, so a change is a rerun, not clicks.

## Scale and references
- Metres, unit scale 1, transforms applied (`bpy.ops.object.transform_apply` or matrix math), no negative scale,
  normals outward. Mirror a part by mirroring its mesh data and reversing its faces, not by scale -1.
- Set the real size first, from the kind of object, before any detail: a passenger car is about 4.2-4.7 m long,
  1.7-1.9 m wide, 1.4-1.5 m high, wheelbase 2.5-2.8 m, wheel 0.6-0.7 m across; a compact off-road SUV 4.4-4.8 m,
  1.9 m, 1.8-1.9 m, wheel 0.75-0.8 m. Write the numbers you chose in the answer.
- References drawn by a model (AI) are inconsistent between views and break proportions: take the silhouette and the
  character from them, take sizes, symmetry and mechanics from the real thing. Model one half and mirror it. Name in
  the answer what you corrected against the pictures.
- Blender's front is -Y, so a car's left is +X (and Unity's left is -X after export).

## Stages: the form before the detail
A model that exports cleanly can still look built of primitives. Work in stages; each ends with frames from the same
cameras, compared with the references, and a stage that fails is fixed there, not covered with detail.
1. References: what each one is taken for (silhouette, construction, detail density, materials), real sizes, where a
   drawn reference breaks the mechanics, and what not to copy.
2. Blockout: big masses at real size, nothing else: the silhouette and proportions read from front, side and 3/4.
3. Primary forms: the main surfaces with their curvature and the transitions between them (a beltline, tumblehome, a
   bonnet that runs into the wings): it reads as the object from every side, not as boxes put together.
4. Secondary forms: panels, cuts, openings, lips and recesses that follow how the thing is made and where its parts
   meet; the surfaces stay continuous across them.
5. Detail: handles, lamps, seams, bolts - only on a form that passed, denser where the eye goes, sparse on flat areas.
6. Game-ready: topology, UVs, normals, materials, pivots, export and the engine check (below).

## Review sheet and critique
- The sheet is front, side, rear, both 3/4 and top, each lit, matcap, wireframe and UV grid (`shots.py`, skill
  `blender-live`, one call); a vehicle adds its parts open (doors, hood, tailgate), the wheels steered, and the
  views from the driver's and the rear seats.
- Critique the form, not the presence of parts, worst first: silhouette, proportions, the quality of transitions, a
  primitive look (boxes and cylinders butted together, one bevel everywhere, no secondary forms), detail hierarchy,
  fidelity to the references. Fix, render the sheet again, and say what is left.

## Hollow bodies and cut panels by booleans
- A boxy body: the side profile (y, z) extruded across X, bisected at the beltline and tapered above it
  (tumblehome), Bevel modifier by angle; then EXACT booleans through a temporary object and
  `bpy.data.meshes.new_from_object(obj.evaluated_get(depsgraph))` - no operator context needed.
- Convex cutters are quickest as `bmesh.ops.convex_hull` of their corner points; non-convex ones as an extruded
  polygon. Give every mesh and cutter the same material slot list and use `material_mode = "INDEX"`: cut faces take
  the cutter's material (glass in window recesses, dark in gaps and wells).
- Shell: subtract a cabin void (the outline offset by the wall). Things that stick into the cabin (wheel tubs) are
  subtracted from the void first, not unioned to the shell: a union there leaves inner faces and non-manifold edges.
- Doors, tailgate, hood: piece = shell ∩ (cutter shrunk by half the gap), body = shell − (cutter grown by it); a
  cutter face coplanar with a shell face makes non-manifold edges, so keep cutters off existing planes.
- Windows: cut them through the whole wall into the cabin void (cutter depth = wall + ~20 mm, seal faces get the trim
  material) and close them with glass panes: closed ~5 mm slabs ~10 mm under the skin, grown ~10 mm into the seal,
  welded into the part that carries them (door glass into the door). A cutter only a few mm deep leaves a blind
  recess: from the seat you see the wall behind it. Prove it from the eye point: renders from the driver's head to the
  front, sides and back with coloured posts outside, each post seen through its glass.
- Merge-by-distance welds coincident faces of separate pieces (a flare on its arch cut) into non-manifold edges: keep
  bolt-on parts a few mm off cut faces; EXACT booleans can also leave back-to-back sliver pairs: drop them in a cleanup
  pass. Count non-manifold edges and check normals after each change; each such edge has had a real cause.

## Parts that move
- Each moving part is its own object: wheels, doors, hood, trunk lid, steering wheel. Its origin sits on its axis of
  motion (wheel centre, door hinge line, hood hinge), and its local axes line up with that motion (the wheel spins
  around its local X), so the engine turns it by one rotation with no offset.
- Every moving part is a direct child of the root body: the FBX exporter with Apply Transform writes wrong transforms
  for objects two levels deep. Weld what rides on a part (mirror, handle, lamp) into its mesh.
- A steered wheel is one object: `rotation_euler = (spin, 0, steer)` in XYZ order applies spin first, then steer -
  the same as Unity's `Quaternion.Euler(0, steer, 0) * Quaternion.Euler(spin, 0, 0)`. Names say the place:
  `Wheel_FL`, `Door_FL`, `Hood`, `Tailgate`.
- Prove it before going on: a script turns each part through its range in steps (wheel steer ±35°, door 0-70°, hood
  and trunk to their open angle) and reports `mathutils.bvhtree` overlaps with the body and interior, also at rest
  (the panel gaps), then puts them back; then one frame in the open pose. Write the limits into the answer, in the
  engine's axes too.

## Retopology and the game mesh
- After the parts work: a low-poly game mesh within the budget from the task or `docs/map.md` (a hero car often
  30-100k triangles, a prop far less). Keep loops on silhouettes and panel gaps, flat areas sparse; no n-gons left at
  export (`use_triangles` in the FBX export), no hidden inner faces.
- A scripted asset: one build script with a detail switch builds the high source and the game mesh (fewer bevel and
  radial segments), then the game mesh is cleaned: merge by distance, dissolve degenerate, limited dissolve at 0.5°
  with material delimit (merges the boolean triangle fans on flat panels).
- Bake detail from the high mesh into a normal map when it carries the look; keep the high mesh in the .blend.
- UVs: no overlaps except deliberate mirrors, even texel density, seams along hard edges and gaps; power-of-two
  textures. Smart-project and pack all the asset's objects in one multi-object edit pass: packing each object alone
  gave small parts 70x the body's density (and took 30 s instead of 3). Measure the density per object after.
- LODs only when the task asks.

## Export
- FBX of the asset's objects only, modifiers applied, Apply Scalings `FBX All`, Add Leaf Bones off, smoothing `Face`.
- Unity: Forward `-Z`, Up `Y`, Apply Transform on; in Unity the root must show rotation (0,0,0) and scale (1,1,1),
  else import with Bake Axis Conversion. Unreal: Unreal is Z-up, 1 unit = 1 cm; see the Unreal pack.
- Keep the .blend and the export script in the project; the exported FBX goes where the engine project expects it.
- Check the file: import it into a factory-startup background Blender (`--factory-startup`) and print parents, local
  transforms and world pivots.
- Check in the engine: size against a known object, pivots where the parts turn, materials assigned, no import
  warnings. With the project's editor open (Unity: skill `unity-editor`), check in place: copy the FBX over the one in
  the project (same GUID, prefab links stay), reimport, read the transforms. Glass gets a transparent material by a
  remap in the importer (Unity `ModelImporter.AddRemap`), not by editing the FBX's material. With no editor of the
  project's version, any installed Unity checks the import in a throwaway project in the temp folder:
  `Unity.exe -batchmode -nographics -createProject <tmp> -quit` (~35 s), then the FBX and an editor script in it,
  `-projectPath <tmp> -executeMethod <Class.Method> -quit -logFile <log>` (~6 s).
