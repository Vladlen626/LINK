using System.Globalization;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// One part of what the agent really gets, for the inspector: a title, a line of what it is, short notes that always
/// show, a longer <see cref="Detail"/> behind a toggle, a <see cref="FilePath"/> to show in Explorer, and whether the
/// Console has the raw text. <see cref="Meter"/> (0..1) draws a bar under the value, <see cref="Color"/> ("#RRGGBB")
/// a jellyfish beside it.
/// </summary>
public sealed record HarnessSection(string Title, string Value)
{
    public string? Note { get; init; }
    public string? Detail { get; init; }
    public string? FilePath { get; init; }
    public bool ShowsConsole { get; init; }
    public double? Meter { get; init; }
    public string? Color { get; init; }
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

    /// <summary>Team against solo per kind of work, from the measured tasks (<see cref="Services.TeamVsSolo"/>).</summary>
    public IReadOnlyList<string> TeamVsSolo { get; init; } = [];

    /// <summary>The project's last two weeks looked back on (<see cref="Services.Reflection"/>).</summary>
    public IReadOnlyList<string> Reflection { get; init; } = [];

    /// <summary>Names an agent as Settings does; null: the default names.</summary>
    public Func<AgentProviderKind, string>? AgentName { get; init; }
}

/// <summary>
/// The harness Link gives an agent, as the inspector lists it: setup, context, rules, role, project memory, task
/// state, skills, the brief, tools and MCP, the check, the project's stats and its last two weeks looked back on. Built from files and records, not from a
/// model, so that it says what is, and refreshes cheaply.
/// </summary>
public sealed record HarnessView(IReadOnlyList<HarnessSection> Sections)
{
    // So many of the stats' rows show; the rest is in the data.
    private const int StatsShown = 5;

    // A skill's description cut for the list.
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
        var role = ProjectRoles.Find(input.Task?.RoleId, input.Workspace);
        return new HarnessView(
        [
            Setup(input, role),
            Context(input),
            Rules(),
            RoleSection(input, role),
            Memory(input.Workspace),
            State(input),
            Skills(input),
            Injected(input),
            Tools(input),
            Verification(input),
            Stats(input),
            Reflect(input)
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

    private static HarnessSection Setup(HarnessInput input, AgentRole? role)
    {
        var caps = input.Capabilities;
        return new HarnessSection("Setup", $"{role?.Name ?? input.Task?.RoleId ?? "General"} · {input.Agent} · {input.Setup}")
        {
            Note = $"Effort: {input.EffortWhy}\n{(caps.Subscription ? "Subscription" : "API, no subscription")} · code goes to {caps.SeesCode}"
        };
    }

    private static HarnessSection Context(HarnessInput input)
    {
        var note = input.ContextBudget > 0
            ? $"Budget {AgentContext.Tokens(input.ContextBudget)}: the agent's CLI compacts its session near it, or at the model's window if that is smaller"
            : "Budget Auto: the agent's CLI compacts its session by its own window for the model";
        var compacted = input.Task is null ? 0 : TaskState.CompactCount(input.Task);
        if (compacted > 0) note += $"\nCompacted {(compacted == 1 ? "once" : $"{compacted} times")}";
        if (input.ContextUsed is not { } used) return new HarnessSection("Context", "Not told yet") { Note = note };
        var limit = AgentContext.Limit(input.ContextBudget, input.ContextWindow);
        return new HarnessSection("Context", AgentContext.Usage(used, input.ContextWindow, input.ContextBudget))
        {
            Note = note,
            Meter = limit > 0 ? Math.Clamp((double)used / limit.Value, 0, 1) : null
        };
    }

    private static HarnessSection Rules()
    {
        var exists = File.Exists(AgentRules.File);
        var name = Path.GetFileName(AgentRules.File);
        return new HarnessSection("Rules", exists ? name : $"{name} is missing")
        {
            Note = "Every new session starts with them; the file is beside Link.exe and changes without a rebuild",
            FilePath = exists ? AgentRules.File : null
        };
    }

    private static HarnessSection RoleSection(HarnessInput input, AgentRole? role)
    {
        if (role is not null)
        {
            return new HarnessSection("Role", role.Name)
            {
                Note = role.Description.Length > 0 ? role.Description : null,
                FilePath = role.FilePath,
                Color = ProjectRoles.ColorOf(role, ProjectRoles.In(input.Workspace))
            };
        }

        return input.Task?.RoleId is { } id
            ? new HarnessSection("Role", $"{id} (its file is gone)") { Note = "The agent gets no role text" }
            : new HarnessSection("Role", "General — no role") { Note = "No specialist text goes with the rules", Color = ProjectRoles.LinkColor };
    }

    private static HarnessSection Memory(string? workspace)
    {
        if (workspace is null) return new HarnessSection("Project memory", "No workspace chosen");
        var map = ProjectMap.PathIn(workspace);
        var lines = File.Exists(map) ? Lines(map) : 0;
        return File.Exists(map)
            ? new HarnessSection("Project memory", $"{ProjectMap.RelativePath} · {Plural(lines, "line")}")
            {
                FilePath = map,
                Note = lines > MapLines ? $"Past ~{MapLines} lines every new session pays for it: a Light task \"condense {ProjectMap.RelativePath}\" keeps it lean" : null
            }
            : new HarnessSection("Project memory", $"{ProjectMap.RelativePath} · missing")
            {
                Note = "Without it the agent searches the folders itself; the Map button of the Project tab has it written"
            };
    }

    // The state is made from the record alone: the files are those of the record, without git's lines added and removed.
    private static HarnessSection State(HarnessInput input) =>
        input.Task is { } task
            ? new HarnessSection("Task state", "What Compact would start a fresh session from")
            {
                Detail = TaskState.Build(task, null, task.Workspace, input.AgentName)
            }
            : new HarnessSection("Task state", "No task yet");

    private static HarnessSection Skills(HarnessInput input)
    {
        var skills = ProjectSkills.In(input.Workspace);
        if (skills.Count == 0) return new HarnessSection("Skills", $"None in {ProjectSkills.RelativeFolder}");
        var first = input.Task?.Skills ?? [];
        bool ReadFirst(ProjectSkill skill) => first.Contains(skill.Name, StringComparer.OrdinalIgnoreCase);
        var marked = skills.Count(ReadFirst);
        // The project's own skills grow with each task; a pack's are the tool's and changed in a copy (see the rules).
        var grown = skills.Where(skill => skill.Pack is null && Lines(skill.FilePath) > SkillLines).Select(skill => skill.Name).ToArray();
        return new HarnessSection("Skills", Plural(skills.Count, "skill") + (marked > 0 ? $" · {marked} to read first" : string.Empty))
        {
            Note = grown.Length > 0 ? $"Past ~{SkillLines} lines, read in every session that needs them: {string.Join(", ", grown)}; a Light task \"condense\" each keeps them lean" : null,
            Detail = string.Join('\n', skills.Select(skill =>
                skill.Name + (ReadFirst(skill) ? " (read first)" : string.Empty) +
                (skill.Description.Length > 0 ? ": " + Shortened(skill.Description, DescriptionShown) : string.Empty)))
        };
    }

    /// <summary>
    /// The task's brief and hand-overs; while a team works, what the worker of the current step is told instead, and in
    /// a training's exam what its session was told: the lessons and the rubric, not the sources.
    /// </summary>
    private static HarnessSection Injected(HarnessInput input)
    {
        if (input.Task is not { Brief.Length: > 0 } task) return new HarnessSection("Injected context", "Nothing yet") { ShowsConsole = true };
        var step = TeamRunner.Current(task);
        var exam = task.Training is { } course ? Training.OpenExam(course) : null;
        var text = step is not null ? TaskState.StepBrief(task, null, task.Workspace, step, input.AgentName)
            : exam is not null ? Training.ExamPrompt(AgentRules.ReadHarness(Training.ExamFile), task.Training!, ProjectRoles.Find(task.RoleId, task.Workspace), exam.Brief)
            : task.BriefText;
        var length = text.Length.ToString("N0", CultureInfo.InvariantCulture);
        return new HarnessSection("Injected context",
            step is not null ? $"Step {TeamRunner.Number(task, step)}'s brief: {length} characters"
            : exam is not null ? $"Exam {task.Training!.Exams.IndexOf(exam) + 1}'s brief: {length} characters"
            : $"The brief Link gave: {length} characters")
        {
            Note = "The rules and the role go before it in a new session; the Console has the exact text",
            Detail = text,
            ShowsConsole = true
        };
    }

    private static HarnessSection Tools(HarnessInput input)
    {
        var servers = input.Workspace is null ? [] : CliAgentProvider.McpServerNames(input.Workspace);
        var access = input.PlanOnly ? "plan first, then the plan runs"
            : input.AutoApprove ? "auto: edits and commands without asking"
            : "edits: other commands ask you";
        return new HarnessSection("Tools and MCP", servers.Count == 0 ? "No MCP servers" : "MCP: " + string.Join(", ", servers))
        {
            Note = $"Access: {access}\nTool rules the project allows: {input.ToolRules.Count}",
            Detail = input.ToolRules.Count == 0 ? null : string.Join('\n', input.ToolRules),
            FilePath = input.Workspace is null ? null : CliAgentProvider.McpConfigIn(input.Workspace)
        };
    }

    private static HarnessSection Verification(HarnessInput input)
    {
        var last = input.Task?.LastCheck;
        var lines = last?.Split('\n') ?? [];
        return new HarnessSection("Verification", input.CheckCommand ?? "None — you check the result")
        {
            Note = lines.Length > 0 ? $"Last: {lines[0]}"
                : input.CheckCommand is null ? "A check command in the Project tab runs after the agent's edits and sends a failure back" : null,
            Detail = lines.Length > 1 ? last : null
        };
    }

    private static HarnessSection Stats(HarnessInput input) =>
        input.Stats.Count == 0
            ? new HarnessSection("Stats", "No runs yet")
            : new HarnessSection("Stats", Plural(input.Stats.Sum(row => row.Runs), "run") + " in this project")
            {
                Note = string.Join('\n', input.Stats.Take(StatsShown).Select(Line)) +
                       (input.TeamVsSolo.Count > 0 ? "\nTeam vs solo, a done task:\n" + string.Join('\n', input.TeamVsSolo) : string.Empty)
            };

    private static HarnessSection Reflect(HarnessInput input) =>
        input.Reflection.Count == 0
            ? new HarnessSection("Reflect", $"No runs in {Services.Reflection.Period.TotalDays:0} days")
            : new HarnessSection("Reflect", input.Reflection[0]) { Note = input.Reflection.Count > 1 ? string.Join('\n', input.Reflection.Skip(1)) : null };

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
