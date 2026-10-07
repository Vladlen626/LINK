using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>A skill of the workspace: its name, when to use it (the description) and its SKILL.md.</summary>
public sealed record ProjectSkill(string Name, string Description, string FilePath);

/// <summary>
/// The workspace's skills: <c>.agents/skills/&lt;name&gt;/SKILL.md</c> (the open Agent Skills format). Codex finds them
/// there by itself; Claude Code looks only in <c>.claude/skills</c>, so Link lists them with its rules. One copy
/// serves both agents, and the model picks a skill by its description, not Link.
/// </summary>
public static partial class ProjectSkills
{
    /// <summary>Where skills live in a workspace, as the agent's rules name it.</summary>
    public const string RelativeFolder = ".agents/skills";

    /// <summary>The agent's task for Save as skill: a file beside Link.exe, to be changed without a new build.</summary>
    public static string TaskFile => Path.Combine(AppContext.BaseDirectory, "harness", "skill-task.md");

    public static string FolderIn(string workspace) => Path.Combine(workspace, ".agents", "skills");

    /// <summary>A skill's file relative to the workspace, as Link writes it.</summary>
    public static string RelativePath(string name) => $"{RelativeFolder}/{name}/SKILL.md";

    /// <summary>The workspace's skills by name; a folder without a readable SKILL.md is not a skill.</summary>
    public static IReadOnlyList<ProjectSkill> In(string? workspace)
    {
        var folder = workspace is null ? null : FolderIn(workspace);
        if (folder is null || !Directory.Exists(folder)) return [];
        var skills = new List<ProjectSkill>();
        try
        {
            foreach (var directory in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(file)) continue;
                var (name, description) = Header(File.ReadAllText(file));
                skills.Add(new ProjectSkill(name ?? Path.GetFileName(directory), description ?? string.Empty, file));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What was read so far is still true.
        }

        return skills;
    }

    /// <summary>The name and description of a SKILL.md's front matter; null when it has none.</summary>
    public static (string? Name, string? Description) Header(string text)
    {
        var (fields, _) = FrontMatter(text);
        return (fields.GetValueOrDefault("name"), fields.GetValueOrDefault("description"));
    }

    /// <summary>
    /// A file's front matter (<c>---</c> lines around <c>key: value</c> lines) and the text after it, as skills and roles
    /// are written. A folded value (<c>&gt;</c> or <c>|</c>) is joined into one line, a blank one is left out. A file
    /// without front matter has no fields and is all body.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Fields, string Body) FrontMatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Trim().Split('\n');
        var fields = new Dictionary<string, string>();
        if (lines[0].Trim() != "---") return (fields, string.Join('\n', lines));
        var index = 1;
        for (; index < lines.Length && lines[index].Trim() != "---"; index++)
        {
            var match = Field().Match(lines[index]);
            if (!match.Success) continue;
            var value = match.Groups[2].Value.Trim();
            if (value is ">" or "|" or ">-" or "|-" or "")
            {
                var folded = new List<string>();
                while (index + 1 < lines.Length && lines[index + 1].StartsWith(' ')) folded.Add(lines[++index].Trim());
                value = string.Join(' ', folded);
            }

            value = value.Trim('"', '\'');
            if (value.Length > 0) fields[match.Groups[1].Value] = value;
        }

        return (fields, string.Join('\n', lines.Skip(index + 1)).Trim());
    }

    /// <summary>A skill's name as both agents accept it: lowercase Latin letters, digits and single hyphens, up to 64.</summary>
    public static bool IsName(string name) => name.Length <= 64 && Name().IsMatch(name);

    [GeneratedRegex(@"^([A-Za-z][\w-]*):(.*)$")]
    private static partial Regex Field();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Name();
}
