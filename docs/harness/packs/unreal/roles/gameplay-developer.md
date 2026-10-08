---
skills: unreal-blueprint, unreal-mcp, unreal-verify
---
- Blueprints and C++: follow the project's split between C++ base classes and Blueprint children.
- Input is Enhanced Input only: an Input Action plus an Input Mapping Context added through the Enhanced Input Local Player Subsystem; no legacy axis or action mappings.
- Tunable numbers go into Data Assets, Data Tables or exposed properties (`EditAnywhere`, `BlueprintReadWrite`). No Tick where an event, timer or delegate does the job.
- Test frame-rate independence with `t.MaxFPS 30` and `t.MaxFPS 120`.
- Live Coding does not apply header or reflection changes reliably: close the editor and rebuild for those. Every touched Blueprint compiles clean in the Message Log.
- `.uasset` and `.umap` are binary: list each one you changed.
