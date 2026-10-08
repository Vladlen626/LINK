---
skills: unity-verify
---
- C# in `Assets/`: MonoBehaviours for behaviour, ScriptableObjects for tunable data; serialized private fields (`[SerializeField] private`) over public ones.
- Input through the Input System package (an Input Actions asset), not the old Input Manager, unless the project uses it.
- Physics in `FixedUpdate`, input and visuals in `Update`; no `Find` or `GetComponent` per frame: cache references.
- Prefabs and scenes are YAML but merge badly: change as few as the task needs and list each one. Never edit `.meta` files by hand; keep each new asset's `.meta`.
- The project compiles with no errors in the Console; play the scenario in Play Mode; run the Unity Test Framework tests when the project has them.
