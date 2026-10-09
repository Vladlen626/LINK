# Vehicles in Blender: body, doors, glass, wheels

What `SKILL.md` beside this file says holds for a vehicle too; this is what a car, a truck or a boat adds.

## Sizes
- A passenger car is about 4.2-4.7 m long, 1.7-1.9 m wide, 1.4-1.5 m high, wheelbase 2.5-2.8 m, wheel 0.6-0.7 m across;
  a compact off-road SUV 4.4-4.8 m, 1.9 m, 1.8-1.9 m, wheel 0.75-0.8 m. Write the numbers you chose in the answer.
- Blender's front is -Y, so a car's left is +X (and Unity's left is -X after export).

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

## Wheels and the review
- A steered wheel is one object: `rotation_euler = (spin, 0, steer)` in XYZ order applies spin first, then steer -
  the same as Unity's `Quaternion.Euler(0, steer, 0) * Quaternion.Euler(spin, 0, 0)`. Names say the place:
  `Wheel_FL`, `Door_FL`, `Hood`, `Tailgate`.
- The review sheet adds the parts open (doors, hood, tailgate), the wheels steered, and the views from the driver's
  and the rear seats. Ranges to prove: wheel steer ±35°, door 0-70°, hood and trunk to their open angle.
- A hero car is often 30-100k triangles; the project's budget decides.
