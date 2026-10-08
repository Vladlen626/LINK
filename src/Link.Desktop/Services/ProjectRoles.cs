using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// A specialist a task can run as: duties, how to work, what done means and what to hand over, as a markdown file.
/// <see cref="Keywords"/> are word stems of a brief it suits, English or Russian; <see cref="Skills"/> are names of
/// workspace skills it works by; <see cref="Effort"/> is its usual effort, null when the file names none.
/// </summary>
public sealed record AgentRole(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Skills,
    string? Effort,
    string Body,
    string FilePath);

/// <summary>
/// The specialists: eight ship beside Link.exe and are edited without a rebuild; a workspace adds its own or replaces a
/// built-in one in <c>.agents/roles</c> by the file's name. Link suggests one from the brief, picks the skills that go
/// with it, and tells them to the agent in the new session, where they follow its rules.
/// </summary>
public static partial class ProjectRoles
{
    // So many skills go to one task: more is a reading list, not a pick.
    private const int MostSkills = 4;

    // A word of a skill stands for the letters it starts with, as the roles' keywords are stems.
    private const int StemLength = 5;

    // A part of a skill's name shorter than this ("ui", "a") says too little to match a brief by.
    private const int ShortestName = 3;

    // Stems of words that any description and any brief may have: they would tie every skill to every task.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "alway", "befor", "betwe", "could", "every", "files", "never", "other", "proje", "shoul",
        "their", "there", "these", "thing", "those", "under", "using", "where", "which", "while", "would", "write",
        "всегд", "друго", "задач", "испол", "когда", "котор", "можно", "нужно", "перед", "после", "проек", "работ",
        "через", "чтобы", "этого"
    };

    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "harness", "roles");

    public static string FolderIn(string workspace) => Path.Combine(workspace, ".agents", "roles");

    /// <summary>
    /// The built-in roles, then the workspace's; a workspace file of the same name as a built-in one replaces it. By name.
    /// A file that cannot be read, or has no name, is not a role.
    /// </summary>
    public static IReadOnlyList<AgentRole> In(string? workspace)
    {
        var roles = new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in ReadFolder(BuiltInFolder).Concat(workspace is null ? [] : ReadFolder(FolderIn(workspace)))) roles[role.Id] = role;
        return [.. roles.Values.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase).ThenBy(role => role.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The role of that file name; null for General and for a role whose file is gone.</summary>
    public static AgentRole? Find(string? id, string? workspace) =>
        id is null ? null : In(workspace).FirstOrDefault(role => string.Equals(role.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The role whose keywords the brief has the most of (a keyword counts once, as a word that begins with it); the
    /// earlier one on a tie. Null when none is found: General, no role.
    /// </summary>
    public static AgentRole? Suggest(string brief, IReadOnlyList<AgentRole> roles)
    {
        AgentRole? best = null;
        var bestScore = 0;
        foreach (var role in roles)
        {
            var score = role.Keywords.Count(keyword => Stems.Found(brief, keyword));
            if (score > bestScore) (best, bestScore) = (role, score);
        }

        return best;
    }

    /// <summary>
    /// Every role whose keywords the brief has, in the order they first occur in it: a task that goes from a model to its
    /// animation to the game names its specialists in the order the work flows. A role with no keyword in the brief is
    /// not among them; roles whose first words are at the same place keep their order.
    /// </summary>
    public static IReadOnlyList<AgentRole> SuggestAll(string brief, IReadOnlyList<AgentRole> roles) =>
        [.. roles
            .Select(role => (Role: role, First: role.Keywords.Select(keyword => Stems.IndexOf(brief, keyword)).Where(index => index >= 0).DefaultIfEmpty(-1).Min()))
            .Where(each => each.First >= 0)
            .OrderBy(each => each.First)
            .Select(each => each.Role)];

    /// <summary>
    /// The skills for a task, at most four: the role's own that the workspace has, then those that go with the brief or
    /// the role. A skill goes with the brief when a word of its name or description (the first letters of it) begins a
    /// word of the brief, and with the role when one of the role's keywords is in its description; the more such words,
    /// the earlier. Names as the workspace has them.
    /// </summary>
    public static IReadOnlyList<string> SkillsFor(string brief, AgentRole? role, IReadOnlyList<ProjectSkill> skills)
    {
        var picked = new List<string>();
        foreach (var name in role?.Skills ?? [])
        {
            if (skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)) is { } own && !picked.Contains(own.Name)) picked.Add(own.Name);
        }

        var related = skills
            .Where(skill => !picked.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))
            .Select(skill => (skill.Name, Score: Related(skill, brief, role)))
            .Where(each => each.Score > 0)
            .OrderByDescending(each => each.Score)
            .Select(each => each.Name);
        return [.. picked.Concat(related).Take(MostSkills)];
    }

    /// <summary>
    /// What a new session is told after the rules: the role's text under "Your role", and the skills (their names) that
    /// the workspace has as a line of files to read first. Empty when there is neither.
    /// </summary>
    public static string SessionText(AgentRole? role, IReadOnlyList<string> skills, string workspace)
    {
        var text = new StringBuilder();
        if (role is not null) text.Append("## Your role: ").Append(role.Name).Append('\n').Append(role.Body);
        var all = skills.Count == 0 ? [] : ProjectSkills.In(workspace);
        var files = skills
            .Select(name => all.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<ProjectSkill>()
            .Select(skill => Path.GetRelativePath(workspace, skill.FilePath).Replace('\\', '/'))
            .ToArray();
        if (files.Length > 0) text.Append(text.Length > 0 ? "\n\n" : string.Empty).Append("Read first: ").AppendJoin(", ", files);
        return text.ToString();
    }

    /// <summary>
    /// What the session that goes on is told when the specialist changes: from now on it works as the other one, who has
    /// its text and skills; null is General. It does not read the project again.
    /// </summary>
    public static string SwitchText(AgentRole? role, IReadOnlyList<string> skills, string workspace)
    {
        var text = new StringBuilder(role is null
            ? "From now on you work on this task without a specialist role: the one before no longer applies."
            : $"From now on you work on this task as {role.Name}.");
        if (SessionText(role, skills, workspace) is { Length: > 0 } session) text.Append("\n\n").Append(session);
        return text.ToString();
    }

    private static List<AgentRole> ReadFolder(string folder)
    {
        var roles = new List<AgentRole>();
        if (!Directory.Exists(folder)) return roles;
        string[] files;
        try
        {
            files = [.. Directory.GetFiles(folder, "*.md").Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return roles;
        }

        foreach (var file in files)
        {
            if (Read(file) is { } role) roles.Add(role);
        }

        return roles;
    }

    private static AgentRole? Read(string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (fields, body) = ProjectSkills.FrontMatter(AgentRules.WithoutNotes(text));
        if (!fields.TryGetValue("name", out var name)) return null;
        return new AgentRole(
            Path.GetFileNameWithoutExtension(file),
            name,
            fields.GetValueOrDefault("description") ?? string.Empty,
            List(fields.GetValueOrDefault("keywords")),
            List(fields.GetValueOrDefault("skills")),
            fields.GetValueOrDefault("effort"),
            body,
            file);
    }

    private static string[] List(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    /// <summary>How many words of the skill's name and description are in the brief, plus the role's keywords in its description.</summary>
    private static int Related(ProjectSkill skill, string brief, AgentRole? role)
    {
        var stems = skill.Name.Split('-').Where(part => part.Length >= ShortestName)
            .Concat(Word().Matches(skill.Description).Select(match => match.Value))
            .Select(word => word[..Math.Min(word.Length, StemLength)])
            .Where(stem => !Common.Contains(stem))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return stems.Count(stem => Stems.Found(brief, stem)) +
               (role?.Keywords.Count(keyword => Stems.Found(skill.Description, keyword)) ?? 0);
    }

    // The words of a description that tell something: five letters or more.
    [GeneratedRegex(@"\p{L}{5,}")]
    private static partial Regex Word();
}
