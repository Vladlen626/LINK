---
name: blender-review
description: Review a Blender model without screenshots - publish the asset after every build step (check.py's numbers - size against the brief, the triangle budget, floating parts, holes, inverted normals), one review window where the user marks points, painted areas and measurements on the 3D model itself, and the agent reads the marks as text lines and answers each; a generated model (Tripo and the like) or a blockout as a ghost at real size. Use with blender-model for every asset the user looks at.
---

# Review by marks, not screenshots

The scripts are beside this file: `check.py` and `publish.py` run in Blender (the bridge of `blender-live`:
`python blender_mcp.py py <here>/publish.py <args>`, or headless `blender -b <file.blend> --python <here>/publish.py --
<args>`); `review.py` runs in plain Python 3 and serves `viewer.html`. Call them by full path.

Why: a frame read by the agent costs 1-2K tokens every time and says nothing exact; a mark is one line with a point in
metres, and the numbers catch a scripted model's commonest defects before anyone looks.

## The brief
- A first version needs a picture (or a few) and 3-5 real sizes: overall width, length and height, and one or two that
  carry the character (wheel diameter, wheelbase, seat height). Ask once for what is missing. They become check options
  in Blender's axes (X across, Y front to back, Z up, `?` for no target), with the triangle budget:
  `size=1.9,4.4,?;tris=30000`.
- The review folder: `%TEMP%\blender_review\<asset>`, unless `docs/map.md` names another. It is working state, never
  committed.
- Copy the picture there as `ref.png`: the user lays it over the view (I) to compare.
- Optional: a generated model the user brings (Tripo and the like; nothing here calls such a service) or an old
  blockout becomes the ghost (R): `publish.py <dir> <file.glb> "size=1.9,4.4,?" --ref` scales the file whole to the sizes given, stands it on the ground at the origin and leaves the
  scene as it was. The line it prints (`scaled x4.575 to 1.830 x 4.575 x 1.601 m`) against the brief shows where the
  generated proportions are off. Objects of the scene instead: `publish.py <dir> <objects> "" --ref`. A generated model
  gives the silhouette and the character, never the sizes (skill `blender-model`).

## The loop
1. After every build step: `publish.py <dir> <objects|*> "size=...;tris=..."`. It prints check.py's lines and the new
   revision. Read the numbers first: size against the brief in %, the budget, then the issues worst first, each with a
   point. A defect (floating, inverted, non-manifold, degenerate, loose, unapplied or negative scale) is fixed before
   the next step; `open` is fine on a surface meant to be one-sided - say so.
2. `review.py open <dir>` once, at the first revision worth showing: it starts the window's server (127.0.0.1, ports
   7330-7339) and the browser. The window reloads itself on every publish; the server stops after 20 minutes nobody
   looks, and `open` again brings it back on the same folder. Tell the user in one line what to look at in this
   revision.
3. The user marks the model: Point (P) - click; Area (A) - paint with the brush, `[` `]` size it; Measure (M) - two
   clicks, then the size it should be; Enter saves a mark. B holds the previous revision, R the ghost, I the picture.
4. When the user says so, or at the next step boundary: `review.py notes <dir>` - each open mark in one line with its
   thread; `--all` adds the closed ones. Read nothing else of the folder; `notes.json` only for an area's stroke
   points.
5. Each mark: change the build script, publish, then `review.py reply <dir> <id> done "what changed, with numbers"`.
   Not doing it: `question`, `later` or `declined` with the reason. Only the user accepts or reopens; a reopened mark
   comes back open with what is still wrong.
6. A question about a place: `review.py pin <dir> x,y,z "question" [object]`. The user's answer turns it open with the
   reply.

## Reading a mark
- Points are Blender's world axes in metres, on the surface of the object named. `facing -Y (Front view)` is the side
  the surface faces, so the view that sees it. The faces meant: `mathutils.bvhtree` `find_nearest` from the point on
  the object's evaluated mesh in world space.
- `area`: brush strokes over the surface; the line gives the point nearest their middle, their extent and the brush
  radius. The faces meant are those within the radius of the strokes' segments (`area.strokes`, lists of points, in
  `notes.json`).
- `measure`: two surface points, the length and its X, Y, Z parts in mm, and the size the user wants with the
  difference. Change the form by that difference, then measure the same two points by script before replying.
- A mark's `rev` is the revision it was made on: a point on a part moved since may sit off the surface now.

## Frames
- None for the user's feedback, and never a screenshot of the window: the marks are the feedback.
- The agent's own frames (`shots.py` of `blender-live`) only at a stage boundary of `blender-model`, for what numbers
  and marks cannot tell: silhouette, the quality of the form, the style check.
