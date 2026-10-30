using System;
using System.IO;
using System.Linq;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>The harness rules Link checks by script after a run: the map's and skills' size, front matter, English, leftovers.</summary>
internal static class HarnessLintTests
{
    public static void Run()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "link-lint-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "docs"));
            Directory.CreateDirectory(Path.Combine(workspace, ".agents", "skills", "jump"));
            Directory.CreateDirectory(Path.Combine(workspace, ".agents", "skills", "dash"));
            File.WriteAllLines(Path.Combine(workspace, "docs", "map.md"), Enumerable.Repeat("- a line", 120));
            File.WriteAllText(Path.Combine(workspace, ".agents", "skills", "jump", "SKILL.md"),
                "---\nname: jump\n---\n" + string.Concat(Enumerable.Repeat("Прыжок делается так\n", 4)));
            File.WriteAllText(Path.Combine(workspace, ".agents", "skills", "dash", "SKILL.md"),
                "---\nname: dash\ndescription: Make a dash\n---\n> Пользователь: сделай рывок\nThe dash is one impulse.\n");

            var notes = HarnessLint.Check(workspace, [
                new AgentChange("docs/map.md", "M", false), new AgentChange(".agents/skills/jump/SKILL.md", "??", true),
                new AgentChange(".agents/skills/dash/SKILL.md", "M", false), new AgentChange("frame.png", "??", true),
                new AgentChange("Assets/Art/hero.png", "??", true), new AgentChange("build.log", "M", false), new AgentChange("gone.md", "D", false)
            ]);
            Program.Equal("Harness lint: a long map, a skill without a description in Russian, a new picture at the top; not a quote, an asset or an old log",
                string.Join(" | ", notes.Select(note => note.Split(':')[0])),
                "`docs/map.md` is 120 lines | `.agents/skills/jump/SKILL.md` needs front matter with `name` and a one-line `description`. | " +
                "`.agents/skills/jump/SKILL.md` has text in another language | `frame.png` looks like a check's leftover");
            File.WriteAllText(Path.Combine(workspace, ".agents", "skills", "dash", "SKILL.md"),
                $"---\nname: dash\ndescription: {new string('d', HarnessLint.DescriptionLength + 1)}\n---\nThe dash is one impulse.\n");
            Program.Equal("Harness lint: a description every session would read at length",
                HarnessLint.Check(workspace, [new AgentChange(".agents/skills/dash/SKILL.md", "M", false)]).Single().Contains($"description is {HarnessLint.DescriptionLength + 1} characters"), true);
            Program.Equal("Harness lint: the notes go after the user's words", HarnessLint.Heard(["x"], "Go on").StartsWith("Go on\n\n") && HarnessLint.Heard(["x"], "Go on").EndsWith("- x"), true);

            var big = Path.Combine(workspace, "Big.cs");
            File.WriteAllLines(big, Enumerable.Repeat("var line = 1; // a line of code that is long enough", 800));
            string? Guard(string tool, object args) => Link.Guard.HarnessGuard.Check(
                System.Text.Json.JsonSerializer.SerializeToElement(new { tool_name = tool, cwd = workspace, tool_input = args }));
            Program.Equal("Guard: a big file read whole, a leftover written into the project, a long wait, a PowerShell write without encoding are refused",
                (Guard("Read", new { file_path = big }) is not null, Guard("Write", new { file_path = Path.Combine(workspace, "run.log") }) is not null,
                    Guard("Bash", new { command = "sleep 120 && curl x" }) is not null, Guard("PowerShell", new { command = "Start-Sleep -Seconds 90" }) is not null,
                    Guard("PowerShell", new { command = "Set-Content a.txt $text" }) is not null),
                (true, true, true, true, true));
            Program.Equal("Guard: a range of it, a picture in Assets, a file outside, a short wait, timeout as a limit, an encoded write run",
                (Guard("Read", new { file_path = big, offset = 1, limit = 100 }), Guard("Write", new { file_path = Path.Combine(workspace, "Assets", "hero.png") }),
                    Guard("Write", new { file_path = Path.Combine(Path.GetTempPath(), "run.log") }), Guard("Bash", new { command = "sleep 5" }),
                    Guard("Bash", new { command = "timeout 300 dotnet test" }), Guard("PowerShell", new { command = "Set-Content a.txt $text -Encoding UTF8" }),
                    Guard("Edit", new { file_path = big })),
                ((string?)null, (string?)null, (string?)null, (string?)null, (string?)null, (string?)null, (string?)null));
            Program.Equal("Guard: Claude's rules leave out what the guard holds; Codex's keep it",
                (AgentRules.Before("Go", workspace, false, guarded: true).Contains("Held by Link"), AgentRules.Before("Go", workspace, false).Contains("Held by Link")),
                (false, true));

            var script = string.Join('\n', Enumerable.Repeat("echo step", Link.Guard.HarnessGuard.LongestCommand + 5));
            File.WriteAllText(Path.Combine(workspace, "tool.sh"), "echo old");
            Program.Equal("Guard: a long inline script and a heredoc over a file that is there are refused; a heredoc that makes a new file runs",
                (Guard("Bash", new { command = "python - <<'EOF'\n" + script + "\nEOF" }) is not null,
                    Guard("Bash", new { command = "cat > tool.sh <<'EOF'\n" + script + "\nEOF" }) is not null,
                    Guard("Bash", new { command = "cat > new.sh <<'EOF'\n" + script + "\nEOF" })),
                (true, true, (string?)null));

            var (linkData, claude, packs) = (Link.Guard.HarnessGuard.ProtectedFolders[0].Folder, Link.Guard.HarnessGuard.ProtectedFolders[1].Folder,
                Link.Guard.HarnessGuard.ProtectedFolders[3].Folder);
            string? EditIn(string file, string cwd) => Link.Guard.HarnessGuard.Check(
                System.Text.Json.JsonSerializer.SerializeToElement(new { tool_name = "Edit", cwd, tool_input = new { file_path = file } }));
            Program.Equal("Guard: Link's data, Claude's settings and the shared packs are never edited from a task; a pack's own task may, and temp is free",
                (EditIn(Path.Combine(linkData, "settings.json"), workspace) is not null,
                    Guard("Write", new { file_path = Path.Combine(claude, "settings.json") }) is not null,
                    EditIn(Path.Combine(packs, "unity", "skills", "x", "SKILL.md"), workspace) is not null,
                    EditIn(Path.Combine(packs, "unity", "skills", "x", "SKILL.md"), Path.Combine(packs, "unity")),
                    EditIn(Path.Combine(Path.GetTempPath(), "frame.txt"), workspace)),
                (true, true, true, (string?)null, (string?)null));

            var session = Guid.NewGuid().ToString();
            string? Remembered(string tool, object args) => Link.Guard.HarnessGuard.Check(
                System.Text.Json.JsonSerializer.SerializeToElement(new { tool_name = tool, cwd = workspace, session_id = session, tool_input = args }));
            var sheet = Path.Combine(workspace, "sheet.png");
            File.WriteAllBytes(sheet, [1, 2, 3]);
            var first = Remembered("Read", new { file_path = sheet });
            var again = Remembered("Read", new { file_path = sheet });
            var insisted = Remembered("Read", new { file_path = sheet });
            File.SetLastWriteTimeUtc(sheet, DateTime.UtcNow.AddMinutes(1));
            Program.Equal("Guard: an image opened again unchanged is refused once; asked again, or changed, it opens",
                (first, again is not null, insisted, Remembered("Read", new { file_path = sheet })), ((string?)null, true, (string?)null, (string?)null));

            var searches = Enumerable.Range(1, Link.Guard.HarnessGuard.SearchesWithoutWork)
                .Select(n => Remembered(n % 2 == 0 ? "Grep" : "Bash", n % 2 == 0 ? new { pattern = "x" } : new { command = "cd src && grep -n x a.cs" }))
                .ToArray();
            Remembered("Edit", new { file_path = big });
            var afterEdit = Enumerable.Range(1, Link.Guard.HarnessGuard.SearchesWithoutWork - 1).Select(_ => Remembered("Glob", new { pattern = "*.cs" })).ToArray();
            Program.Equal("Guard: searching on without a change is stopped once at the limit; an edit starts the count over",
                (searches.Take(searches.Length - 1).All(why => why is null), searches[^1] is not null, afterEdit.All(why => why is null)), (true, true, true));

            string? Stop(string id, bool active = false) => Link.Guard.HarnessGuard.Stop(
                System.Text.Json.JsonSerializer.SerializeToElement(new { hook_event_name = "Stop", session_id = id, stop_hook_active = active }));
            var shortWork = Stop(session);
            for (var call = 0; call < Link.Guard.HarnessGuard.LongWork; call++) Remembered("Bash", new { command = "dotnet test" });
            var (continuing, ended) = (Stop(session, active: true), Stop(session));
            Program.Equal("Guard: a long session that changed files and kept nothing is asked once, before it ends, what to keep",
                (shortWork is null, continuing, ended is not null, Stop(session)), (true, (string?)null, true, (string?)null));
            var learning = Guid.NewGuid().ToString();
            for (var call = 0; call < Link.Guard.HarnessGuard.LongWork; call++)
            {
                Link.Guard.HarnessGuard.Check(System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    tool_name = "Edit", cwd = workspace, session_id = learning,
                    tool_input = new { file_path = call == 0 ? Path.Combine(workspace, ".agents", "skills", "jump", "SKILL.md") : big }
                }));
            }

            Program.Equal("Guard: a session that wrote a skill is not asked", Stop(learning), (string?)null);
            foreach (var id in new[] { session, learning }) File.Delete(Path.Combine(Path.GetTempPath(), "link-guard", id + ".txt"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
