---
name: blender-live
description: Work in Blender from the agent - run bpy scripts in the open Blender through the Blender Lab MCP bridge or in a background Blender, render check frames of objects, glue them into one sheet, bpy pitfalls. Use before any modeling, rigging or animation work in Blender.
---

# Blender from the agent

The scripts are beside this file (`blender_mcp.py`, `shots.py`, `sheet.py`); call them by full path, keep your own
scripts in the project (`tools/blender/` unless `docs/map.md` says otherwise), never in the temp folder.

## The open Blender
- The Blender Lab extension "MCP" in the open Blender listens on `127.0.0.1:9876` (not `localhost`: IPv6 first costs
  ~2 s a call). `python blender_mcp.py ping` - version, file, scene; `python blender_mcp.py py <script.py> [args]` -
  the script runs in the open Blender: `bpy` is there, the arguments are the list `ARGS`, the answer is the dict
  `result`, `print()` goes to stdout. Keep the code in a script file, not in an argument.
- In Git Bash on Windows `python` can be the Microsoft Store stub (prints `Python`, exit 49): run the client as
  `uv run --no-project blender_mcp.py ...`. The script must set `result` to a dict.
- No answer on the port: ask the user to open Blender with the MCP extension on; do not start the GUI yourself.
- A module you import in the open Blender is cached: after changing a helper, `importlib.reload` it.
- A script leaves the scene as it found it: the active action, frame, selection and active object come back
  (`try/finally`), as `shots.py` does.

## Background Blender
- `blender -b <file.blend> --python <script.py> -- <args>` for work on another file or without the GUI (find
  `blender.exe` in PATH, `C:\Program Files\Blender Foundation\`, or Steam's `steamapps\common\Blender\`; the surest
  is the open one: PowerShell `(Get-Process blender).Path`). Run it without saving first; save only once the result
  is checked.
- In PowerShell check `$LASTEXITCODE` after each CLI call before the next step that depends on it.

## Check by numbers, then by frames
- Numbers first: dimensions in metres, vertex and face counts, pivots, bone positions, distances - a short script
  returns them in `result`. Frames only for what numbers cannot tell (silhouette, deformation).
- `python blender_mcp.py py shots.py <objects|*> [front,left,34] [frames] [prefix] [looks] [options]` renders PNGs to
  `%TEMP%\blender_shots`; `uv run --no-project --with pillow sheet.py out.png <cols> <png…>` glues them; then read the
  one sheet, not each frame. Views also `back`, `right`, `top`, `34r` (rear 3/4); looks `lit`, `matcap` (the form
  alone), `wire`, `uv` (a grid: stretching and texel density). A model's review sheet in one call:
  `shots.py <objects> front,left,back,34,34r,top 1 sheet lit,matcap,wire,uv`, glued 6 to a row.
- As the player sees it: view `game` (the gameplay camera, in perspective), view `thumb` (the object as small as on
  screen far away), look `flat` (the colours unshaded: whether the colour zones alone carry the form). The camera,
  background and thumbnail size come from the project's art profile with the option `style=docs/art/STYLE.md`, or as
  `cam=PITCH,FOV,DISTANCE[,YAW];bg=#RRGGBB;thumb=64`; colours render as they are (Standard view transform). A style
  check: `shots.py <objects> game,thumb 1 style lit,flat "style=docs/art/STYLE.md"`; `sheet.py` fits frames of other
  sizes into the first frame's cell, a thumbnail enlarged by whole pixels, so the lineup - your `game` frame beside
  approved assets' `gamecam.png` - is one sheet.
- A camera render draws no overlays: that is why `wire` adds a temporary Wireframe modifier and `uv` an active Image
  Texture node with a generated grid; the script takes them off after, as any check of yours must.
- To frame a rigged character, move the camera and the surroundings, not the rig object: parts not parented to it
  stay behind.

## bpy pitfalls (Blender 4.4+)
- Action curves are slotted: `action.layers[0].strips[0].channelbags[0].fcurves`, not `action.fcurves`.
- An action that keys only some bones leaves the others in the pose of the last action: reset the pose to rest before
  showing, measuring or exporting it.
- `mesh.materials.clear()` resets every face's `material_index` to 0: keep the indices, re-add the slots, then
  `polygons.foreach_set("material_index", ...)`.
- Operators need the right context (mode, active object, area); prefer data API calls (`bpy.data`, `bmesh`) to
  `bpy.ops` in scripts. Check the API of the running version before guessing.

## The user's work
- A file, object or action the user made is not changed or overwritten under its own name: make yours beside it
  under a new name and say which is whose. Save the .blend after each finished group of changes
  (`bpy.ops.wm.save_mainfile()`); under Perforce `p4 edit` it first.
