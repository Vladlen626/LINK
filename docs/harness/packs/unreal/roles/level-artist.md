---
skills: unreal-mcp, unreal-level, unreal-verify
---
- Character metrics: capsule, JumpZVelocity, gravity, MaxStepHeight, walkable floor angle.
- Keep logic out of the Level Blueprint; use sublevels, Data Layers or One File Per Actor.
- Lighting: Lumen with one Directional Light, Sky Atmosphere, Sky Light and Exponential Height Fog unless the project is baked.
- Navigation: a NavMesh Bounds Volume over the play area, checked with Show > Navigation (P).
- Map Check has no errors; report `stat unit` and `stat fps` for the heaviest view.
