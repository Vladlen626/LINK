---
name: blender-model
description: Model game assets in Blender by script - real scale from references, the form in stages (blockout, primary, secondary, detail), the project's finish and palette colours, separate moving parts with pivots on their axes, retopology to a game-ready mesh, UVs and FBX export for Unity or Unreal with an engine-side check; a vehicle's body, doors and glass in vehicle.md. Use for any modeling, mesh or export work in Blender.
---

# Modeling for a game

Work through the bridge of `blender-live`; build by scripts kept in the project, so a change is a rerun, not clicks.
How the game looks - finish, palette, camera, budgets - is the project's art profile (skill `art-style`); a class of
asset that repeats is made by a generator (skill `blender-procedural`, beside this one); a vehicle adds `vehicle.md`
beside this file. Every step is published and judged by its numbers, and the user reviews by marks on the model in a
window, not by screenshots: skill `blender-review`, beside this one - read it before the first build.

## Scale and references
- Metres, unit scale 1, transforms applied (`bpy.ops.object.transform_apply` or matrix math), no negative scale,
  normals outward. Mirror a part by mirroring its mesh data and reversing its faces, not by scale -1.
- Set the real size first, from the kind of object and its construction references, before any detail; write the
  numbers you chose in the answer.
- References drawn by a model (AI) are inconsistent between views and break proportions: take the silhouette and the
  character from them, take sizes, symmetry and mechanics from the real thing. Model one half and mirror it where the
  thing is symmetric, and break the symmetry only once the construction is right. Name in the answer what you
  corrected against the pictures.
- Blender's front is -Y, so an object's left is +X.

## Stages: the form before the detail
A model that exports cleanly can still look built of primitives. Work in stages; each ends with frames from the same
cameras, compared with the references, and a stage that fails is fixed there, not covered with detail.
1. References: what each one is taken for (silhouette, construction, detail density, materials), real sizes, where a
   drawn reference breaks the mechanics, and what not to copy.
2. Blockout: big masses at real size, nothing else: the silhouette and proportions read from front, side and 3/4, and
   from the gameplay camera and as a thumbnail where the project has a profile.
3. Primary forms: the main surfaces with their curvature and the transitions between them (a beltline, tumblehome, a
   bonnet that runs into the wings): it reads as the object from every side, not as boxes put together.
4. Secondary forms: panels, cuts, openings, lips and recesses that follow how the thing is made and where its parts
   meet; the surfaces stay continuous across them.
5. Detail: handles, lamps, seams, bolts - only on a form that passed, denser where the eye goes, sparse on flat areas.
6. Game-ready: topology, UVs, normals, materials, pivots, export and the engine check (below).

Each stage is its own run of the build script (a stage switch), rendered and compared before the next is written: a
whole model written in one go and seen only at the end was the primitive-looking result. Inside a stage, each step is
judged by `publish.py`'s numbers (skill `blender-review`), not by frames. The blockout is checked over
the references themselves: each reference as a camera background (an Image Empty in the view's ortho camera, 50%
opacity) scaled by one known size (wheelbase, height), rendered with the mesh over it; then 4–6 key ratios (length to
height, wheel to body, cabin to bonnet) measured on the picture and on the mesh, the gaps listed in the answer.

## Building forms, not boxes
- Main masses come from cross-sections and profile curves (a side profile and a few sections lofted or bridged), or a
  low cage under Subdivision with creases: surfaces that curve and run into each other. Booleans of boxes give flat
  slabs with seams; keep them for cuts (panel lines, openings, vents).
- Bevels follow the part: wide soft radii on the big masses, tight ones on panel edges and lips, none where two parts
  only touch (faceted: wide and narrow chamfers instead of radii). One chamfer on every edge reads as aggressive and
  machine-made, whatever the finish.
- The same for interiors and small parts: a seat is a cushion with a crown and a roll, not two boxes at an angle.

## Finish and colour
- The profile's `finish` decides the surface. `faceted`: hard edges and flat faces; light, shadow and colour zones are
  carved as faces - a cut, a chamfer, a step, each zone with its palette colour - not left to smoothing or a texture;
  a triangle earns its place when it changes the silhouette or a large patch of light or colour. `smooth`: bevels,
  weighted normals, smooth shading. With no profile, as the references show.
- Palette: faces take materials named after the profile's colours (`hull`, `rust`), then as the build script's last
  step `palette.py <objects|*> docs/art/STYLE.md <atlas.png> [flat|height]` (beside this file) turns them into one
  material and a small atlas, each face on its colour; `height` shades a face by its height in the object, lighter
  higher. The atlas goes where the engine takes it, imported without filtering (Point) and without mipmaps.

## Review sheet and critique
- With an art profile the style check of `art-style` comes first (gameplay camera, thumbnail, lineup). The full sheet
  (front, side, rear, both 3/4 and top, each lit, matcap, wireframe and UV grid: `shots.py` of skill `blender-live`,
  one call) judges the construction and closes the game-ready stage.
- Critique the form, not the presence of parts, worst first: silhouette, proportions, the quality of transitions, a
  primitive look (boxes and cylinders butted together, one bevel everywhere, no secondary forms), detail hierarchy,
  fidelity to the references. Fix the worst in the source, render the same frames again, and say what is left.

## Parts that move
- Each moving part is its own object: wheels, doors, hood, lids, hatches. Its origin sits on its axis of motion
  (wheel centre, hinge line), and its local axes line up with that motion (a wheel spins around its local X), so the
  engine turns it by one rotation with no offset.
- Every moving part is a direct child of the root body: the FBX exporter with Apply Transform writes wrong transforms
  for objects two levels deep. Weld what rides on a part (a handle, a lamp) into its mesh.
- Prove it before going on: a script turns each part through its range in steps and reports `mathutils.bvhtree`
  overlaps with the body and interior, also at rest (the gaps), then puts them back; then one frame in the open pose.
  Write the limits into the answer, in the engine's axes too.

## Retopology and the game mesh
- After the parts work: a game mesh within the budget from the profile, the task or `docs/map.md`. Keep loops on
  silhouettes and panel gaps, flat areas sparse; no n-gons left at export (`use_triangles` in the FBX export), no
  hidden inner faces.
- A scripted asset: one build script with a detail switch builds the high source and the game mesh (fewer bevel and
  radial segments), then the game mesh is cleaned: merge by distance, dissolve degenerate, limited dissolve at 0.5°
  with material delimit (merges the boolean triangle fans on flat panels).
- Bake detail from the high mesh into a normal map when it carries the look; keep the high mesh in the .blend.
- UVs: no overlaps except deliberate mirrors, even texel density, seams along hard edges and gaps; power-of-two
  textures. Smart-project and pack all the asset's objects in one multi-object edit pass: packing each object alone
  gave small parts 70x the body's density (and took 30 s instead of 3). Measure the density per object after. A
  palette asset needs none of this: its UVs are the atlas's.
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
