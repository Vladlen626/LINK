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
- No answer on the port: ask the user to open Blender with the MCP extension on; do not start the GUI yourself.
- A module you import in the open Blender is cached: after changing a helper, `importlib.reload` it.
- A script leaves the scene as it found it: the active action, frame, selection and active object come back
  (`try/finally`), as `shots.py` does.

## Background Blender
- `blender -b <file.blend> --python <script.py> -- <args>` for work on another file or without the GUI (find
  `blender.exe` in PATH, `C:\Program Files\Blender Foundation\`, or Steam's `steamapps\common\Blender\`). Run it
  without saving first; save only once the result is checked.
- In PowerShell check `$LASTEXITCODE` after each CLI call before the next step that depends on it.

## Check by numbers, then by frames
- Numbers first: dimensions in metres, vertex and face counts, pivots, bone positions, distances - a short script
  returns them in `result`. Frames only for what numbers cannot tell (silhouette, deformation).
- `python blender_mcp.py py shots.py <objects|*> [front,left,34] [frames] [prefix]` renders PNGs to
  `%TEMP%\blender_shots`; `uv run --no-project --with pillow sheet.py out.png <cols> <png…>` glues them; then read the
  one sheet, not each frame.
- To frame a rigged character, move the camera and the surroundings, not the rig object: parts not parented to it
  stay behind.

## bpy pitfalls (Blender 4.4+)
- Action curves are slotted: `action.layers[0].strips[0].channelbags[0].fcurves`, not `action.fcurves`.
- An action that keys only some bones leaves the others in the pose of the last action: reset the pose to rest before
  showing, measuring or exporting it.
- Operators need the right context (mode, active object, area); prefer data API calls (`bpy.data`, `bmesh`) to
  `bpy.ops` in scripts. Check the API of the running version before guessing.

## The user's work
- A file, object or action the user made is not changed or overwritten under its own name: make yours beside it
  under a new name and say which is whose. Save the .blend after each finished group of changes
  (`bpy.ops.wm.save_mainfile()`); under Perforce `p4 edit` it first.
