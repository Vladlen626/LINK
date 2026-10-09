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
