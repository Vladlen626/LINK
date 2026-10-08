using System.Text;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// A specialist a task can run as: duties, how to work, what done means and what to hand over, as a markdown file.
/// <see cref="Keywords"/> are word stems of a brief it suits, English or Russian; <see cref="Skills"/> are names of
/// workspace skills it works by; <see cref="Effort"/> is its usual effort, null when the file names none.
/// <see cref="Color"/> is the colour of its jellyfish, "#RRGGBB", null when the file names none (see <see cref="ProjectRoles.ColorOf"/>).
/// </summary>
public sealed record AgentRole(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Skills,
    string? Effort,
    string Body,
    string FilePath,
    string? Color = null);

/// <summary>
/// The specialists: eight ship beside Link.exe and are edited without a rebuild; a workspace adds its own or replaces a
/// built-in one in <c>.agents/roles</c> by the file's name. Link suggests one from the brief, picks the skills that go
/// with it, and tells them to the agent in the new session, where they follow its rules.
/// </summary>
public static partial class ProjectRoles
{
    /// <summary>Link's own orange: the jellyfish of General, the work with no specialist.</summary>
    public const string LinkColor = "#F5843F";

    /// <summary>
    /// The colours a specialist's jellyfish is given when its file names none, tuned for dark glass and none of them
    /// near <see cref="LinkColor"/>, so that General stays the only orange one.
    /// </summary>
    public static readonly IReadOnlyList<string> Colors =
        ["#4ACFDF", "#6C9EF8", "#A78BFA", "#F472B6", "#F87171", "#FBBF24", "#A3E635", "#34D399", "#38BDF8", "#C084FC"];

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

    /// <summary>Built-in roles that had another file name: older tasks name them by it.</summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase) { ["unreal-developer"] = "gameplay-developer" };

    /// <summary>
    /// The built-in roles with what the workspace's tool packs add to them (<see cref="WithPack"/>), then the workspace's;
    /// a workspace file of the same name as a built-in one replaces it. By name. A file that cannot be read, or has no
    /// name, is not a role.
    /// </summary>
    public static IReadOnlyList<AgentRole> In(string? workspace)
    {
        var roles = new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in ReadFolder(BuiltInFolder)) roles[role.Id] = role;
        foreach (var pack in ToolPacks.For(workspace))
        {
            foreach (var role in roles.Values.ToArray()) roles[role.Id] = WithPack(role, pack);
        }

        foreach (var role in workspace is null ? [] : ReadFolder(FolderIn(workspace))) roles[role.Id] = role;
        return [.. roles.Values.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase).ThenBy(role => role.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The role of that file name, or of the name it had; null for General and for a role whose file is gone.</summary>
    public static AgentRole? Find(string? id, string? workspace)
    {
        if (id is null) return null;
        var roles = In(workspace);
        return roles.FirstOrDefault(role => string.Equals(role.Id, id, StringComparison.OrdinalIgnoreCase)) ??
               (Renamed.TryGetValue(id, out var now) ? roles.FirstOrDefault(role => string.Equals(role.Id, now, StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>
    /// A built-in role as a tool pack has it: the pack's <c>roles/&lt;id&gt;.md</c> adds its text under "In &lt;pack&gt;"
    /// and its <c>skills:</c> and <c>keywords:</c> to the role's. A pack with no file for the role leaves it as it is.
    /// </summary>
    private static AgentRole WithPack(AgentRole role, ToolPack pack)
    {
        var file = Path.Combine(pack.Folder, "roles", role.Id + ".md");
        if (!File.Exists(file)) return role;
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return role;
        }

        var (fields, body) = ProjectSkills.FrontMatter(AgentRules.WithoutNotes(text));
        return role with
        {
            Keywords = [.. role.Keywords.Concat(List(fields.GetValueOrDefault("keywords"))).Distinct(StringComparer.OrdinalIgnoreCase)],
            Skills = [.. role.Skills.Concat(List(fields.GetValueOrDefault("skills"))).Distinct(StringComparer.OrdinalIgnoreCase)],
            Body = body.Length == 0 ? role.Body : $"{role.Body}\n## In {pack.Name}\n{body}"
        };
    }

    /// <summary>
    /// The colour of a specialist's jellyfish: Link's orange for General (<c>null</c>), the file's own colour when it names
    /// one, else one of <see cref="Colors"/> that no role of <paramref name="roles"/> names itself, handed out to the roles
    /// with no colour in the order of their file names, and again from the first when they are more than the colours left.
    /// The same roles give the same colours.
    /// </summary>
    public static string ColorOf(AgentRole? role, IReadOnlyList<AgentRole> roles)
    {
        if (role is null) return LinkColor;
        if (Hex(role.Color) is { } own) return own;
        var free = Colors.Where(color => !roles.Any(each => Hex(each.Color) == color)).ToArray();
        if (free.Length == 0) free = [.. Colors];
        var plain = roles.Where(each => Hex(each.Color) is null).Select(each => each.Id).Order(StringComparer.Ordinal).ToList();
        var index = plain.IndexOf(role.Id);
        return free[(index < 0 ? plain.Count : index) % free.Length];
    }

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
            .Select(skill => ProjectSkills.PathFor(workspace, skill))
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
            file,
            Hex(fields.GetValueOrDefault("color")));
    }

    /// <summary>A colour as "#RRGGBB" in capitals; null when the text is not one.</summary>
    private static string? Hex(string? value) => value is not null && ColorText().IsMatch(value) ? value.ToUpperInvariant() : null;

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

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex ColorText();

    // The words of a description that tell something: five letters or more.
    [GeneratedRegex(@"\p{L}{5,}")]
    private static partial Regex Word();
}
