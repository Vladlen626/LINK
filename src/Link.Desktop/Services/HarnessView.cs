using System.Globalization;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Something a section names that opens in Link's viewer: a file or a folder (<see cref="Path"/>), or text Link holds
/// rather than a file (<see cref="Text"/>, Markdown), such as the brief. Neither: a line that only says. A
/// <see cref="Color"/> ("#RRGGBB") draws a role's jellyfish before it.
/// </summary>
public sealed record HarnessLink(string Label)
{
    public string? Path { get; init; }
    public string? Text { get; init; }
    public string? Note { get; init; }
    public string? Color { get; init; }
}

/// <summary>
/// One part of what the agent really gets, for the inspector: a title, a line of what it is, short notes and what opens
/// in the viewer. <see cref="Meter"/> (0..1) draws a bar under the value.
/// </summary>
public sealed record HarnessSection(string Title, string Value)
{
    public string? Note { get; init; }
    public double? Meter { get; init; }
    public IReadOnlyList<HarnessLink> Links { get; init; } = [];

    // The same parts are the same section, so that a refresh leaves an unchanged card alone.
    public bool Equals(HarnessSection? other) =>
        other is not null && Title == other.Title && Value == other.Value && Note == other.Note && Meter == other.Meter &&
        Links.SequenceEqual(other.Links);

    public override int GetHashCode() => HashCode.Combine(Title, Value, Note, Meter, Links.Count);
}

/// <summary>
/// What the inspector is built from. <see cref="Task"/> is the open task, null before one starts: the sections then
/// say what the next task would get. The words (<see cref="Agent"/>, <see cref="Setup"/>, <see cref="EffortWhy"/>) are
/// those the window shows elsewhere, so that the inspector never names a model differently.
/// </summary>
public sealed record HarnessInput(
    string? Workspace, string Agent, string Setup, string EffortWhy, AgentCapabilities Capabilities, int ContextBudget)
{
    public AgentTaskRecord? Task { get; init; }

    /// <summary>A plan first only reads; every other run may change files.</summary>
    public bool PlanOnly { get; init; }

    public bool AutoApprove { get; init; }

    /// <summary>What the session holds and the model's window, as the CLI last told; null when it has not.</summary>
    public long? ContextUsed { get; init; }

    public long? ContextWindow { get; init; }
    public string? CheckCommand { get; init; }

    /// <summary>The tool rules the project allows for this access (<see cref="AgentCoordinator.Tools"/>).</summary>
    public IReadOnlyList<string> ToolRules { get; init; } = [];

    /// <summary>The project's stats, the busiest group first (<see cref="AgentStatsReport"/>).</summary>
    public IReadOnlyList<StatsRow> Stats { get; init; } = [];

    /// <summary>The project's last two weeks looked back on (<see cref="Services.Reflection"/>).</summary>
    public IReadOnlyList<string> Reflection { get; init; } = [];

    /// <summary>Names an agent as Settings does; null: the default names.</summary>
    public Func<AgentProviderKind, string>? AgentName { get; init; }

    /// <summary>Who the session can hand parts of the work to ("mechanic · Haiku", "Codex"); empty: it works alone.</summary>
    public IReadOnlyList<string> Helpers { get; init; } = [];
}

/// <summary>
/// The harness Link gives an agent, as the inspector lists it, in seven cards: the agent, its context, the instructions
/// it starts with, what it knows of the project, what it may do, the check and how the project's runs went. Every file
/// and text in them opens in Link's viewer, so that nothing needs the file system to be read. Built from files and
/// records, not from a model, so that it says what is, and refreshes cheaply.
/// </summary>
public sealed record HarnessView(IReadOnlyList<HarnessSection> Sections)
{
    // So many of the stats' rows show; the rest is in the data.
    private const int StatsShown = 5;

    // A skill's description cut for its line.
    private const int DescriptionShown = 110;

    // Past these the map and a skill are condensed, as the agent's rules ask; the inspector says when they are not.
    private const int MapLines = 120;
    private const int SkillLines = 150;

    private static int Lines(string path)
    {
        try
        {
            return File.ReadLines(path).Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static HarnessView Build(HarnessInput input)
    {
        var all = ProjectRoles.In(input.Workspace);
        var roles = ProjectRoles.Of(input.Task?.RoleIds ?? [], all);
        return new HarnessView(
        [
            Agent(input),
            Context(input),
            Instructions(input, roles, all),
            Knowledge(input),
            Access(input),
            Verification(input),
            History(input)
        ]);
    }

    /// <summary>
    /// Why the run has its effort: Link's pick with its <paramref name="reason"/>, Link's pick to come (the task is on
    /// <paramref name="auto"/>), the user's pin (an <paramref name="effort"/> without either), or the CLI's own. A model
    /// that does not <paramref name="takesEffort"/> has none.
    /// </summary>
    public static string EffortWhy(bool takesEffort, bool auto, string? reason, string? effort) =>
        !takesEffort ? "this model has no effort setting"
        : reason is not null ? $"Link picked it: {reason}"
        : auto ? "Link picks it for each run"
        : effort is not null ? "pinned on the card"
        : "the CLI's own";

    /// <summary>"Context budget 400k · 5 skills · MCP: unreal, blender": what else goes with a run, as the card's muted line says it.</summary>
    public static string CardLine(int contextBudget, int skills, IReadOnlyList<string> mcpServers) =>
        $"Context budget {AgentContext.Budget(contextBudget)} · {(skills == 0 ? "no skills" : Plural(skills, "skill"))} · " +
        (mcpServers.Count == 0 ? "no MCP servers" : "MCP: " + string.Join(", ", mcpServers));

    private static HarnessSection Agent(HarnessInput input)
    {
        var caps = input.Capabilities;
        var helpers = input.Helpers.Count == 0
            ? "Works alone: Claude Code gets helpers in Settings → Agents"
            : $"Hands parts to {string.Join(" · ", input.Helpers)}: their steps show in the thread";
        return new HarnessSection("The agent", $"{input.Agent} · {input.Setup}")
        {
            Note = $"Effort: {input.EffortWhy}\n{(caps.Subscription ? "Subscription" : "API, no subscription")} · code goes to {caps.SeesCode}\n{helpers}"
        };
    }

    private static HarnessSection Context(HarnessInput input)
    {
        var note = input.ContextBudget > 0
            ? $"Budget {AgentContext.Tokens(input.ContextBudget)}: the agent's CLI compacts its session near it, or at the model's window if that is smaller"
            : "Budget Auto: the agent's CLI compacts its session by its own window for the model";
        var compacted = input.Task is null ? 0 : TaskState.CompactCount(input.Task);
        if (compacted > 0) note += $"\nCompacted {(compacted == 1 ? "once" : $"{compacted} times")}";
        // The state is made from the record alone: the files are those of the record, without git's lines added and removed.
        IReadOnlyList<HarnessLink> links = input.Task is { } task
            ? [new HarnessLink("What Compact keeps") { Text = TaskState.Build(task, null, task.Workspace, input.AgentName), Note = "a fresh session starts from it" }]
            : [];
        if (input.ContextUsed is not { } used) return new HarnessSection("Context", "Not told yet") { Note = note, Links = links };
        var limit = AgentContext.Limit(input.ContextBudget, input.ContextWindow);
        return new HarnessSection("Context", AgentContext.Usage(used, input.ContextWindow, input.ContextBudget))
        {
            Note = note,
            Meter = limit > 0 ? Math.Clamp((double)used / limit.Value, 0, 1) : null,
            Links = links
        };
    }

    // What a new session starts with: Link's rules, the task's roles (the lead first) and the brief.
    private static HarnessSection Instructions(HarnessInput input, IReadOnlyList<AgentRole> roles, IReadOnlyList<AgentRole> all)
    {
        var links = new List<HarnessLink>
        {
            File.Exists(AgentRules.File)
                ? new HarnessLink("Link's rules") { Path = AgentRules.File, Note = "beside Link.exe, they change without a rebuild" }
                : new HarnessLink($"{Path.GetFileName(AgentRules.File)} is missing")
        };
        links.AddRange(roles.Select(role => new HarnessLink(role.Name)
        {
            Path = role.FilePath,
            Note = role.Description.Length > 0 ? role.Description : null,
            Color = ProjectRoles.ColorOf(role, all)
        }));
        if (input.Task is { Brief.Length: > 0 } task)
        {
            var text = task.BriefText;
            links.Add(new HarnessLink("The task's brief") { Text = text, Note = $"{text.Length.ToString("N0", CultureInfo.InvariantCulture)} characters" });
        }

        var value = roles.Count > 0 ? string.Join(" + ", roles.Select(role => role.Name))
            : input.Task?.RoleIds is { Count: > 0 } ids ? $"{string.Join(" + ", ids)} ({(ids.Count == 1 ? "its file is" : "their files are")} gone)"
            : "General — no role";
        return new HarnessSection("Instructions", value)
        {
            Note = roles.Count > 0 ? "A new session reads the rules, then the roles, then the brief" : "No specialist text goes with the rules",
            Links = links
        };
    }

    // What the agent knows of the project before it looks: the map, Claude's notes and the project's skills.
    private static HarnessSection Knowledge(HarnessInput input)
    {
        if (input.Workspace is not { } workspace) return new HarnessSection("Project knowledge", "No workspace chosen");
        var links = new List<HarnessLink>();
        var notes = new List<string>();
        var parts = new List<string>();
        if (ProjectMap.Shared(workspace) is { } shared)
        {
            parts.Add("the repository's map");
            links.Add(new HarnessLink(ProjectMap.Relative(workspace, shared)) { Path = shared, Note = "the team keeps it in git: Link's own map is not used" });
        }
        else if (ProjectMap.PathIn(workspace) is var map && File.Exists(map))
        {
            var lines = Lines(map);
            parts.Add("the map");
            links.Add(new HarnessLink(ProjectMap.RelativePath) { Path = map, Note = Plural(lines, "line") });
            if (lines > MapLines) notes.Add($"Past ~{MapLines} lines every new session pays for the map: a Light task \"condense {ProjectMap.RelativePath}\" keeps it lean");
        }
        else
        {
            parts.Add("no map");
            notes.Add("Without a map the agent searches the folders itself; the Map button of the project's column has it written");
        }

        var memory = ProjectMemory.Notes(workspace);
        if (memory.Count > 0)
        {
            parts.Add(Plural(memory.Count, "note"));
            links.Add(new HarnessLink("Claude's notes") { Path = Path.GetDirectoryName(memory[0]), Note = "what it keeps between tasks" });
        }

        var skills = ProjectSkills.In(workspace);
        var first = input.Task?.Skills ?? [];
        bool ReadFirst(ProjectSkill skill) => first.Contains(skill.Name, StringComparer.OrdinalIgnoreCase);
        parts.Add(skills.Count == 0 ? "no skills" : Plural(skills.Count, "skill"));
        links.AddRange(skills.OrderBy(skill => !ReadFirst(skill)).Select(skill => new HarnessLink(skill.Name)
        {
            Path = skill.FilePath,
            Note = string.Join(" · ", new[] { ReadFirst(skill) ? "read first" : null, skill.Description.Length > 0 ? Shortened(skill.Description, DescriptionShown) : null }.OfType<string>()) is { Length: > 0 } note ? note : null
        }));
        // The project's own skills grow with each task; a pack's are the tool's and changed in a copy (see the rules).
        var grown = skills.Where(skill => skill.Pack is null && Lines(skill.FilePath) > SkillLines).Select(skill => skill.Name).ToArray();
        if (grown.Length > 0) notes.Add($"Past ~{SkillLines} lines, read in every session that needs them: {string.Join(", ", grown)}; a Light task \"condense\" each keeps them lean");
        if (skills.Count == 0) notes.Add($"No skills in {ProjectSkills.RelativeFolder}: the agents write them as they learn the project");

        var value = string.Join(" · ", parts);
        return new HarnessSection("Project knowledge", char.ToUpperInvariant(value[0]) + value[1..])
        {
            Note = notes.Count > 0 ? string.Join('\n', notes) : null,
            Links = links
        };
    }

    private static HarnessSection Access(HarnessInput input)
    {
        var servers = input.Workspace is null ? [] : CliAgentProvider.McpServerNames(input.Workspace);
        var access = input.PlanOnly ? "Plan first, then the plan runs"
            : input.AutoApprove ? "Auto: edits and commands without asking"
            : "Edits: other commands ask you";
        var links = new List<HarnessLink>();
        if (input.Workspace is not null && CliAgentProvider.McpConfigIn(input.Workspace) is { } config && File.Exists(config))
            links.Add(new HarnessLink(Path.GetFileName(config)) { Path = config, Note = "the tools' servers" });
        if (input.ToolRules.Count > 0)
            links.Add(new HarnessLink("Tools the project allows") { Text = string.Join('\n', input.ToolRules.Select(rule => $"- `{rule}`")), Note = Plural(input.ToolRules.Count, "rule") });
        return new HarnessSection("What it may do", access)
        {
            Note = servers.Count == 0 ? "No MCP servers" : "MCP: " + string.Join(", ", servers),
            Links = links
        };
    }

    private static HarnessSection Verification(HarnessInput input)
    {
        var last = input.Task?.LastCheck;
        var lines = last?.Split('\n') ?? [];
        return new HarnessSection("Check", input.CheckCommand ?? "None — you check the result")
        {
            Note = lines.Length > 0 ? $"Last: {lines[0]}"
                : input.CheckCommand is null ? "A check command in the project's column runs after the agent's edits and sends a failure back" : null,
            Links = lines.Length > 1 ? [new HarnessLink("The last check") { Text = $"```\n{last}\n```", Note = Plural(lines.Length, "line") }] : []
        };
    }

    // The project's runs by role and agent, then its last two weeks looked back on.
    private static HarnessSection History(HarnessInput input)
    {
        var lines = input.Stats.Take(StatsShown).Select(Line).Concat(input.Reflection).ToArray();
        return new HarnessSection("How it went", input.Stats.Count == 0 ? "No runs yet" : Plural(input.Stats.Sum(row => row.Runs), "run") + " in this project")
        {
            Note = lines.Length > 0 ? string.Join('\n', lines) : $"No runs in {Services.Reflection.Period.TotalDays:0} days"
        };
    }

    /// <summary>"Rigger · Claude Code · opus — 3 tasks, 1 done, 3 failed, 2:30 a run, 127k tokens a run, 3 steers".</summary>
    private static string Line(StatsRow row) =>
        $"{row.Role} · {row.Agent} · {row.Model ?? "default model"} — {Plural(row.Tasks, "task")}, {row.Done} done" +
        (row.Failed > 0 ? $", {row.Failed} failed" : string.Empty) +
        (row.AverageTime > TimeSpan.Zero ? $", {(int)row.AverageTime.TotalMinutes}:{row.AverageTime.Seconds:00} a run" : string.Empty) +
        (row.AverageTokens is { } tokens ? $", {AgentContext.Tokens(tokens)} tokens a run" : string.Empty) +
        (row.Steers > 0 ? $", {Plural(row.Steers, "steer")}" : string.Empty);

    private static string Shortened(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    private static string Plural(int number, string one) => $"{number} {(number == 1 ? one : one + "s")}";
}
