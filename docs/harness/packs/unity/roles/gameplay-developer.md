---
skills: unity-verify, unity-editor
---
- C# in `Assets/`: MonoBehaviours for behaviour, ScriptableObjects for tunable data; serialized private fields (`[SerializeField] private`) over public ones. One public type per file, the file named as the type (required for MonoBehaviour and ScriptableObject).
- Input through the Input System package (an Input Actions asset), not the old Input Manager, unless the project uses it.
- Physics in `FixedUpdate`, input and visuals in `Update`; no `Find` or `GetComponent` per frame: cache references.
- Find the nearest existing analogue in the code first and follow its conventions.
- Done when the changed assemblies compile (`unity-verify`), the console has no new errors, and the scenario was played in Play Mode where the change is behaviour (`unity-editor` if the bridge is set up, else name it "not checked").
