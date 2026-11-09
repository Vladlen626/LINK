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
    string? Color = null)
{
    /// <summary>
    /// Whether the packs' own rules (<c>rules.md</c>: an engine's assets, code and editor) go with the role; off
    /// (<c>pack-rules: no</c>) for one that works in a tool beside the engine, which would pay for them in every request.
    /// </summary>
    public bool PackRules { get; init; } = true;
}

/// <summary>
/// A jellyfish of what a task is given (<see cref="ProjectRoles.Kit"/>): a role in its colour, or a tool pack with no
/// colour of its own, with the skills it brings and whether the agent opened its files. <see cref="FilePath"/> is the
/// role's file or the pack's folder; null for General.
/// </summary>
public sealed record KitPart(string Name, string? Color, string Description, IReadOnlyList<string> Skills, string? FilePath, bool Lit);

/// <summary>
/// The specialists: nine ship beside Link.exe and are edited without a rebuild; a workspace adds its own or replaces a
/// built-in one in <c>.agents/roles</c> by the file's name. Link picks those the brief calls for (<see cref="Pick"/>),
/// the skills that go with them, and tells them to the agent in the new session, where they follow their rules; there
/// is no choice of role by hand. <see cref="Kit"/> is what the user sees of it.
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

    // So many specialists work on one task together: more would bury each one's text under the others'.
    private const int MostRoles = 3;

    // So many skills go to one task: more is a reading list, not a pick.
    private const int MostSkills = 4;

    // So many words tie a skill to a task beside the role's own: one word in common ("camera", "Blender") is chance.
    private const int RelatedWords = 2;

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
    public static AgentRole? Find(string? id, string? workspace) => id is null ? null : Resolve(id, In(workspace));

    /// <summary>The roles of those file names (or of the names they had) among <paramref name="roles"/>, in their order; a gone one is left out.</summary>
    public static IReadOnlyList<AgentRole> Of(IEnumerable<string> ids, IReadOnlyList<AgentRole> roles) =>
        [.. ids.Select(id => Resolve(id, roles)).OfType<AgentRole>().Distinct()];

    private static AgentRole? Resolve(string id, IReadOnlyList<AgentRole> roles) =>
        roles.FirstOrDefault(role => string.Equals(role.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        (Renamed.TryGetValue(id, out var now) ? roles.FirstOrDefault(role => string.Equals(role.Id, now, StringComparison.OrdinalIgnoreCase)) : null);

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
    /// The specialists Link gives a task, at most three of <see cref="SuggestAll"/>: the lead, whose words come first in
    /// the brief, first. None: General.
    /// </summary>
    public static IReadOnlyList<AgentRole> Pick(string brief, IReadOnlyList<AgentRole> roles) => [.. SuggestAll(brief, roles).Take(MostRoles)];

    /// <summary>
    /// The skills for a task, at most four: the roles' own (<see cref="OwnSkills"/>), the first of each role, then the
    /// second of each and so on, so that the lead does not take them all; then those that go with the brief or the roles
    /// by two words or more. A skill goes with the brief when a word of its name or description (the first letters of it)
    /// begins a word of the brief, and with the roles when one of their keywords is in its description; the more such
    /// words, the earlier. A pack's name is no such word for its skills: "Blender" in a brief calls for none of the
    /// Blender pack's skills more than another. Names as the workspace has them.
    /// </summary>
    public static IReadOnlyList<string> SkillsFor(string brief, IReadOnlyList<AgentRole> roles, IReadOnlyList<ProjectSkill> skills)
    {
        var picked = roles
            .SelectMany(role => OwnSkills(role, skills).Select((name, turn) => (Name: name, Turn: turn)))
            .OrderBy(each => each.Turn)
            .Select(each => each.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var related = skills
            .Where(skill => !picked.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))
            .Select(skill => (skill.Name, Score: Related(skill, brief, roles)))
            .Where(each => each.Score >= RelatedWords)
            .OrderByDescending(each => each.Score)
            .Select(each => each.Name);
        return [.. picked.Concat(related).Take(MostSkills)];
    }

    /// <summary>
    /// The role's own skills that the workspace has, then the game's add-on of their tool where it has one
    /// (<c>blender-project</c> beside <c>blender-model</c>): what was learned in this game; at most four. What the role
    /// reads first, whatever the task's words. Names as the workspace has them.
    /// </summary>
    public static IReadOnlyList<string> OwnSkills(AgentRole? role, IReadOnlyList<ProjectSkill> skills)
    {
        var declared = role?.Skills ?? [];
        var picked = new List<string>();
        foreach (var name in declared.Concat(declared.Select(name => name.Split('-')[0] + "-project")))
        {
            if (skills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)) is { } own && !picked.Contains(own.Name)) picked.Add(own.Name);
        }

        return [.. picked.Take(MostSkills)];
    }

    /// <summary>
    /// What a new session is told after the rules: the role's text under "Your role" (several roles each under its
    /// name, the lead first), and the skills (their names) that the workspace has as a line of files to read first.
    /// Empty when there is neither.
    /// </summary>
    public static string SessionText(IReadOnlyList<AgentRole> roles, IReadOnlyList<string> skills, string workspace)
    {
        var text = new StringBuilder();
        if (roles.Count == 1) text.Append("## Your role: ").Append(roles[0].Name).Append('\n').Append(roles[0].Body);
        if (roles.Count > 1)
        {
            text.Append("## Your roles\nYou work on this task as ").AppendJoin(", ", roles.Select(role => role.Name))
                .Append(": each part of the work by the role it belongs to; where they differ, ").Append(roles[0].Name).Append(" leads.");
            foreach (var role in roles) text.Append("\n\n### ").Append(role.Name).Append('\n').Append(role.Body.TrimEnd());
        }

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
    /// What a task is given, as the user sees it: a part for each of its roles, the lead first (General when none of
    /// them is found), then one for each tool pack of the workspace. A role is lit once the agent opened its file or one
    /// of its own skills (<paramref name="opened"/>, see <see cref="Opened"/>), a pack once it opened one of the pack's
    /// skills; General is always lit.
    /// </summary>
    public static IReadOnlyList<KitPart> Kit(IReadOnlyList<string> roleIds, IReadOnlyCollection<string> opened,
        IReadOnlyList<AgentRole> roles, IReadOnlyList<ProjectSkill> skills, IReadOnlyList<ToolPack> packs)
    {
        bool AnyOpen(IEnumerable<ProjectSkill> some) => some.Any(skill => opened.Contains(Tail(skill)));
        var parts = new List<KitPart>();
        foreach (var role in Of(roleIds, roles))
        {
            var own = OwnSkills(role, skills);
            var lit = opened.Contains("roles/" + role.Id.ToLowerInvariant()) || AnyOpen(skills.Where(skill => own.Contains(skill.Name)));
            parts.Add(new KitPart(role.Name, ColorOf(role, roles), role.Description, own, role.FilePath, lit));
        }

        if (parts.Count == 0) parts.Add(new KitPart("General", LinkColor, "No specialist: Link's rules and the project's own.", [], null, true));
        foreach (var pack in packs)
        {
            var its = skills.Where(skill => skill.Pack == pack.Name).ToArray();
            parts.Add(new KitPart(pack.Name, null, pack.Description, [.. its.Select(skill => skill.Name)], pack.Folder, AnyOpen(its)));
        }

        return parts;
    }

    /// <summary>
    /// The roles and skills a tool call opens, as "roles/&lt;id&gt;" and "skills/&lt;folder&gt;" in lower case: a
    /// <c>…/roles/&lt;id&gt;.md</c> or <c>…/skills/&lt;folder&gt;/SKILL.md</c> in its text or arguments, whatever the
    /// slashes. None for any other call, and for a pattern such as <c>skills/*/SKILL.md</c>.
    /// </summary>
    public static IReadOnlyList<string> Opened(string call)
    {
        var text = Slashes().Replace(call.ToLowerInvariant(), "/");
        if (!text.Contains("skill.md", StringComparison.Ordinal) && !text.Contains("roles/", StringComparison.Ordinal)) return [];
        return [.. OpenedFile().Matches(text).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Distinct()];
    }

    // How Opened names a skill: its folder.
    private static string Tail(ProjectSkill skill) => "skills/" + Path.GetFileName(Path.GetDirectoryName(skill.FilePath))?.ToLowerInvariant();

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
            Hex(fields.GetValueOrDefault("color")))
        {
            PackRules = !string.Equals(fields.GetValueOrDefault("pack-rules"), "no", StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>A colour as "#RRGGBB" in capitals; null when the text is not one.</summary>
    private static string? Hex(string? value) => value is not null && ColorText().IsMatch(value) ? value.ToUpperInvariant() : null;

    private static string[] List(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    /// <summary>
    /// How many words of the skill's name and description are in the brief, plus the roles' keywords in its description;
    /// the name of the skill's pack is none of them.
    /// </summary>
    private static int Related(ProjectSkill skill, string brief, IReadOnlyList<AgentRole> roles)
    {
        var pack = skill.Pack?[..Math.Min(skill.Pack.Length, StemLength)];
        var stems = skill.Name.Split('-').Where(part => part.Length >= ShortestName)
            .Concat(Word().Matches(skill.Description).Select(match => match.Value))
            .Select(word => word[..Math.Min(word.Length, StemLength)])
            .Where(stem => !Common.Contains(stem) && !string.Equals(stem, pack, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return stems.Count(stem => Stems.Found(brief, stem)) +
               roles.SelectMany(role => role.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).Count(keyword => Stems.Found(skill.Description, keyword));
    }

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex ColorText();

    // A run of slashes either way, as a path in JSON or a shell has it.
    [GeneratedRegex(@"[\\/]+")]
    private static partial Regex Slashes();

    // A skill's SKILL.md (group 1: "skills/<folder>") or a role's file (group 2: "roles/<id>"), in a lower-case text with "/".
    [GeneratedRegex(@"(skills/[^/\s""'`*?]+)/skill\.md|(roles/[^/\s""'`*?]+?)\.md")]
    private static partial Regex OpenedFile();

    // The words of a description that tell something: five letters or more.
    [GeneratedRegex(@"\p{L}{5,}")]
    private static partial Regex Word();
}
