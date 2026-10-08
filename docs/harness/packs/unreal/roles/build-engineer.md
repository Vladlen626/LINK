---
skills: unreal-package, unreal-profiling
---
- Packaging: `RunUAT BuildCookRun -platform=Win64 -clientconfig=Development -build -cook -stage -pak -archive`; maps listed in Project Settings > Packaging; read the cook log for failures.
- Tools: `stat unit` (Game / Draw / GPU ms), `stat fps`, `stat gpu`, `memreport -full`, Unreal Insights (`-trace=cpu,gpu,frame`).
- Validate with Data Validation and Map Check; the packaged log is in `Saved/Logs`.
