using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>A skill of the workspace: its name, when to use it (the description) and its SKILL.md; <see cref="Pack"/> names the tool pack it comes from.</summary>
public sealed record ProjectSkill(string Name, string Description, string FilePath, string? Pack = null);

/// <summary>
/// The workspace's skills: <c>.agents/skills/&lt;name&gt;/SKILL.md</c> (the open Agent Skills format), then those of the
/// tool packs the workspace uses (<see cref="ToolPacks"/>) that it has no skill of the same name for. Codex finds the
/// workspace's own there by itself; Claude Code looks only in <c>.claude/skills</c>, so Link lists them with its rules.
/// One copy serves both agents, and the model picks a skill by its description, not Link.
/// </summary>
public static partial class ProjectSkills
{
    /// <summary>Where skills live in a workspace, as the agent's rules name it.</summary>
    public const string RelativeFolder = ".agents/skills";

    public static string FolderIn(string workspace) => Path.Combine(workspace, ".agents", "skills");

    /// <summary>The workspace's own skills by name, then its packs' that it does not override.</summary>
    public static IReadOnlyList<ProjectSkill> In(string? workspace)
    {
        var skills = new List<ProjectSkill>(Own(workspace));
        foreach (var pack in ToolPacks.For(workspace))
        {
            foreach (var skill in Of(pack))
            {
                if (!skills.Any(each => string.Equals(each.Name, skill.Name, StringComparison.OrdinalIgnoreCase))) skills.Add(skill);
            }
        }

        return skills;
    }

    /// <summary>A tool pack's skills.</summary>
    public static IReadOnlyList<ProjectSkill> Of(ToolPack pack) => Read(Path.Combine(pack.Folder, "skills"), pack.Name);

    /// <summary>The skills in the workspace's own folder.</summary>
    public static IReadOnlyList<ProjectSkill> Own(string? workspace) => workspace is null ? [] : Read(FolderIn(workspace), null);

    /// <summary>How the agent is told where a skill is: inside the workspace from its root, a pack's in full.</summary>
    public static string PathFor(string workspace, ProjectSkill skill) =>
        (skill.Pack is null ? Path.GetRelativePath(workspace, skill.FilePath) : skill.FilePath).Replace('\\', '/');

    /// <summary>The skills of a folder by name; a folder without a readable SKILL.md is not a skill.</summary>
    private static List<ProjectSkill> Read(string folder, string? pack)
    {
        var skills = new List<ProjectSkill>();
        if (!Directory.Exists(folder)) return skills;
        try
        {
            foreach (var directory in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(file)) continue;
                var (name, description) = Header(File.ReadAllText(file));
                skills.Add(new ProjectSkill(name ?? Path.GetFileName(directory), description ?? string.Empty, file, pack));
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

    [GeneratedRegex(@"^([A-Za-z][\w-]*):(.*)$")]
    private static partial Regex Field();
}
