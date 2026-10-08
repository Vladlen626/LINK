---
name: unity-editor
description: Drive the open Unity editor through the official Unity CLI bridge (unity mcp, package com.unity.pipeline) - run C# (eval), read the console and hierarchy, recompile, capture the Game View, click uGUI. Use when a check needs the live editor, or to set the bridge up.
---

# The live Unity editor

Two parts, both needed: the **Unity CLI** outside the editor (`unity`, Windows: `winget install Unity.CLI`) and the
package **`com.unity.pipeline`** inside it (a local server with `eval`, `console`, `recompile`, `capture_game_view`
and ~150 more tools). `unity mcp` is the CLI's MCP server (stdio); without `--project-path` it finds the editor of the
project by the current folder.

## Set up (each change only after the user's yes)

1. Look, read only: `unity --version`, `unity status` (port, project, state of each open editor), `unity pipeline list`
   (package, version, Server Reachable, Safe Mode), the `com.unity.pipeline` line of `Packages/manifest.json`. No CLI:
   offer the install, do not run it.
2. No package: `unity pipeline install --project-path <project>` changes `Packages/manifest.json` and
   `packages-lock.json`, the team's tracked files: only with a yes, and the user decides whether it is committed. An old
   package ("too old to parse command lines") still works over MCP, which passes arguments as JSON.
3. Link gives agents MCP servers only from the project's `.mcp.json`. The entry, shown to the user before it is written:
   ```json
   { "mcpServers": { "unity-editor": { "command": "unity", "args": ["mcp"] } } }
   ```
   No `--project-path`: with several clones or editors each session reaches the editor of its own folder.
4. Check in the next task: `eval` of `return UnityEngine.Application.dataPath;` returns this project's path; five calls
   in a row, note the mean time. Write versions, the way of connection, the time and the limits into `docs/map.md`.

Fallback without MCP: `unity command eval "<code>"` (about 1 s a call), or a script that starts `unity mcp`, sends
`initialize` → `notifications/initialized` → `tools/call`, waits for the answer by its `id` and saves images to files.

## Use it cheaply

- A model's turn costs ~10 s, a call 0.2-1 s: fewer turns is the gain. One `eval` (or `eval_file`) runs a whole
  scenario - click, wait, read the state - and returns a one-line result.
- `eval`: the body of a method, full type names (`UnityEngine.Object`), the result as a string by `return`, all in
  `try/catch`; `timeout` in ms (5000 by default). The first call is slow (compiler warm-up); a main-thread timeout after
  a pause or without focus gets one retry, not a loop.
- Wait for a recompile in the background on `recompile_status`, not with short sleeps. Do not edit files while a
  check runs in Play Mode.
- Images to files; look at one only to check a layout. Read the console with a cursor and a level filter.
- Several editors open: confirm by `dataPath` or `unity status` that you talk to the right one.
- uGUI click: `EventSystem.RaycastAll` at normalised coordinates → `ExecuteEvents` pointerDown / pointerUp / click; find
  an element by its TMP text or its hierarchy path.
- Third-party Unity MCP packages only after the project compiles: their DLLs can clash with the project's types
  (CS0433, CS0121) and break the build for the whole team.

## Editor settings that speed checks (suggest, do not change)

Enter Play Mode Options without domain reload (`ProjectSettings/EditorSettings.asset`; static state must then be
reset in code with `[RuntimeInitializeOnLoadMethod]`, a team decision); Interaction Mode and background behaviour in
Preferences if eval slows while Unity is not focused; Auto Refresh off for a batch edit of many files, then one
`AssetDatabase.Refresh()` by eval.
