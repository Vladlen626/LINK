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
            Program.Equal("Harness lint: the notes go after the user's words", HarnessLint.Heard(["x"], "Go on").StartsWith("Go on\n\n") && HarnessLint.Heard(["x"], "Go on").EndsWith("- x"), true);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
