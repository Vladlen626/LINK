---
name: rebuild-link
description: Put a code change into the user's running Link - close Link gracefully, build Release, run the tests, start it again and check link.log. Also how to build and test while Link stays open.
---

# Rebuild the user's Link

The user runs `src\Link.Desktop\bin\Release\net10.0-windows\Link.exe`; a running Link locks it. Rebuild it only when
the user has not said they will do it themselves.

## Check without closing

```powershell
& .agents\skills\rebuild-link\close-link.ps1 -Check   # not running | running, idle | agent running (exit 2)
```

`agent running` — an agent task works in Link: never close it, the task would die. Commit, and tell the user to
rebuild when the task is over.

## While Link stays open

Build and test into the scratchpad, not `bin\Release`:

```bash
dotnet build tests/Link.Desktop.Tests -c Release -o "<scratchpad>/testbin" 2>&1 | grep -E "error|warning CS|Ошибок|Предупреждений" | sort -u
"<scratchpad>/testbin/Link.Desktop.Tests.exe" | tail -3
```

Run the tests only after the build printed `Ошибок: 0` (dotnet speaks Russian here): a failed build leaves the old
test exe, and it passes.

## Rebuild

1. `& .agents\skills\rebuild-link\close-link.ps1` → `closed`. `still running` → ask the user for tray → Exit.
2. If you change `%LocalAppData%\Link\settings.json` or `projects.json`, copy them to the scratchpad first; edit only
   while Link is closed (it writes them itself).
3. `dotnet build Link.sln -c Release` → `Предупреждений: 0`, `Ошибок: 0`.
4. `dotnet run --project tests/Link.Desktop.Tests -c Release --no-build` → `All N checks passed.`
5. `explorer.exe "$PWD\src\Link.Desktop\bin\Release\net10.0-windows\Link.exe"`, wait ~5 s, then
   `Get-Content $env:LOCALAPPDATA\Link\link.log -Tail 5`: no new errors. Through Explorer, not `Start-Process`: a
   process started from this session belongs to the Claude app, and Windows ends it with the app's update (2026-10-09
   Link and its team's agent died at 08:58 when Claude 2.31226 installed, with nothing in link.log).

## The user's own button

`rebuild-link.ps1` does steps 1, 3, 4 and 5 in one console; it refuses while an agent works in Link. The user starts
it from the Start menu entry "Rebuild Link", which `install-shortcuts.ps1` makes together with the "Link" entry (both
can be pinned to the taskbar from Start). Run that installer again after the repo moves.

## Done when

The new Link runs from `bin\Release`, the build has 0 warnings and 0 errors, all checks pass.
