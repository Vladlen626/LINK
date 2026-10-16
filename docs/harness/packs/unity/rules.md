<!-- Unity's rules: Link adds them to the agent's rules in every Unity project, whatever the specialist. Link strips HTML comments before the agent sees it. -->
## In Unity
- The Unity project is the folder with `ProjectSettings/ProjectVersion.txt`: the workspace itself or a folder in it,
  beside art sources and docs. Paths below are inside it.
- Never create or edit `.meta`, `*.csproj`, `*.sln`, `Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/` by hand. A new
  file in `Assets/` gets its `.meta` when the editor imports it; the two are committed together.
- YAML assets (`.prefab .unity .asset .mat .anim .controller`): do not read them whole or edit them as text beyond a
  trivial value. Change them with an editor script: `PrefabUtility.LoadPrefabContents` → change →
  `SaveAsPrefabAsset` → `UnloadPrefabContents`, then `AssetDatabase.SaveAssetIfDirty`.
- Renaming a `[SerializeField]` field needs `[FormerlySerializedAs("old")]`; removing one breaks the wiring in prefabs
  and scenes: say so in the answer, never silently. A new serialized field gets the initializer of the literal it replaces.
- Test Unity objects with `if (!obj)` / `if (obj)`: `?.` and `??` skip Unity's fake null.
- A new `using` from another assembly usually needs a reference in the `.asmdef`.
- Search with `git grep` / `git ls-files` (or `rg` with a path), never a recursive scan of `Assets/` or `Library/`; do
  not open binaries (png, fbx, wav, dll) or files over ~1 MB unless asked.
- One editor per project folder. A file changed during Play Mode recompiles and breaks the check: do not edit while a
  check runs in the editor. Batch mode does not start while the project is open in an editor.
- Without being asked: no package installs, no player builds, no batch-mode test runs, no writes to assets beyond the
  task.
- In a team's project: fetch and compare with the team's main branch before you start and work on its latest (a
  fast-forward keeps uncommitted work; save a patch for a file both changed). What others own - generators, scene
  builders, their scenes, prefabs and renderers - is not changed: integrate through the data they produce (a terrain,
  its layers). Revert editor noise before handing over: assets rewritten with other line endings only
  (`git diff --ignore-cr-at-eol`), dynamic font atlases.
- Renderer features go into the default renderer, idle until a volume turns them on; never assume a renderer index:
  other features switch cameras to their own renderers and back to the default.
- GPU-written data: bounds-check every write in a compute shader and clamp every count the GPU is told to use
  (indirect arguments). D3D12, the editor's default on Windows, can lose the device on a write past a buffer's end
  that D3D11 silently drops - the editor crashes.
- A mesh drawn instanced by a custom shader (positions from a buffer): give it zero positions and pass its own
  parameters in UVs. Debug views (Overdraw) and override materials draw it with their shader: real positions put
  every instance at the world's origin, and a hundred thousand of them hang the GPU.
- Terrain on the GPU: `terrainData.heightmapTexture` is R16_UNorm holding 0..0.5 for 0..the terrain's height; splat
  maps by `GetAlphamapTexture(i)`. Read `terrain.terrainData` every frame: gameplay may swap in a deforming copy.
- Unity 6.4 deprecates `FindObjectsByType(FindObjectsSortMode)` and `FindFirstObjectByType`: use
  `FindObjectsByType<T>()` and `FindAnyObjectByType<T>()`.
