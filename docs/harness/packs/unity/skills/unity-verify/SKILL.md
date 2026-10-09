---
name: unity-verify
description: Check a Unity change without the editor - compile the changed assemblies with dotnet from the generated csproj, read the Editor.log, tell code errors from environment errors. Use after editing C# or asmdef in a Unity project.
---

# Check a Unity change

The ladder, lowest step the change needs (`docs/map.md` may say more): `.cs` → compile; a serialized field or a prefab
→ the editor's console; behaviour → Play Mode. A step you could not take is named "not checked" in the answer.

## Compile without the editor

1. Unity writes one `*.csproj` per assembly into the project root. Find the assembly of a file by its line
   `Include="Assets\...\File.cs"` (backslashes): `git grep -l "Assets\\\\Path\\\\File.cs" -- "*.csproj"`.
2. A new file is in no csproj yet. The nearest `.asmdef` up the tree names its assembly; without one it is
   `Assembly-CSharp`. Write a temporary targets file outside the project:
   ```xml
   <Project><ItemGroup Condition="'$(MSBuildProjectName)' == 'ASSEMBLY'"><Compile Include="FULL\PATH\File.cs" /></ItemGroup></Project>
   ```
   and build `dotnet build ASSEMBLY.csproj -nologo -v q -p:CustomAfterMicrosoftCommonTargets=<targets>`.
   In Codex's sandbox add `-p:TargetPlatformSdkPath=none -p:TargetPlatformDisplayName=Windows` to every build:
   otherwise MSBuild looks for the Windows SDK in `%LOCALAPPDATA%\Microsoft SDKs`, the sandbox denies it, and every
   build ends in MSB4184 before compiling anything.
3. A changed `.asmdef` with new `references`: add them to the same targets as `ProjectReference`.
4. Take the lines with `: error `; thousands of warnings are noise. The summary line depends on the locale
   (`Ошибок:` / `Error(s)`).
5. **Environment errors are not code errors:** CS2001, any error inside `Library/PackageCache`, CS0246/CS0234 on a type
   that exists on disk but not in the csproj (the csproj is stale). Then the build is "not checked": say so and ask the
   user to let the editor regenerate the project files; do not "fix" the code.
6. Do not build `*.Player.csproj` right after the main one: they share `obj/Debug` and the next incremental build
   takes a DLL without `UNITY_EDITOR`. If you had to, build again with `--no-incremental`.
7. No `*.csproj` at all (never opened in an IDE): ask the user to open the project in Unity once, or use the editor
   bridge (`unity-editor` skill) to recompile and read the console.

## The editor's log

`%LOCALAPPDATA%\Unity\Editor\Editor.log` (Windows). Read its tail, not the whole file:
`Get-Content "$env:LOCALAPPDATA\Unity\Editor\Editor.log" -Tail 200 -Encoding UTF8`, then filter for `error CS` and
`Exception`.

## Tests

EditMode tests of one assembly only when asked or when the task says so; batch mode does not start while the project
is open in an editor (`Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode -testResults <file>`).
