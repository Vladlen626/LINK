using System.Text.Json.Serialization;

namespace Link.Desktop.Models;

/// <summary>The command-line agent that does work in a workspace. The numbers stay those of older settings.</summary>
public enum AgentProviderKind
{
    ClaudeCode = 0,
    Codex = 1,

    /// <summary>Claude Code pointed at another server with an Anthropic-compatible API (see <see cref="CustomModelSettings"/>).</summary>
    Custom = 2
}

public static class AgentProviderKinds
{
    /// <summary>Whether Claude Code does the work: it is the CLI of the custom model too.</summary>
    public static bool RunsOnClaudeCode(this AgentProviderKind kind) => kind is AgentProviderKind.ClaudeCode or AgentProviderKind.Custom;

    /// <summary>The agent's name where no settings are at hand; the custom model is named in Settings.</summary>
    public static string DefaultName(this AgentProviderKind kind) => kind switch
    {
        AgentProviderKind.ClaudeCode => "Claude Code",
        AgentProviderKind.Codex => "Codex",
        _ => "Custom model"
    };
}

/// <summary>What an agent may do in the workspace. Analyze only reads; Do is chosen for one task, never by default.</summary>
public enum AgentAccess
{
    Analyze,
    Do
}

/// <summary>
/// One task of the user's, as the Task tab shows it: the goal and the brief Link gave, every run of every agent on it,
/// the user's words on the way, checks and hand-overs, the agent's plan and the files changed. Kept in the project's
/// data, so that it can be read and taken up again after Link restarts.
/// </summary>
public sealed class AgentTaskRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The task in a line: the brief's first.</summary>
    public required string Goal { get; set; }

    /// <summary>What Link gave the first agent: the brief with the context that went along.</summary>
    public string Brief { get; set; } = string.Empty;

    public required string Workspace { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Running;
    public List<AgentRunRecord> Runs { get; set; } = [];
    public List<AgentTaskEntry> Entries { get; set; } = [];

    /// <summary>Every file the task changed, relative to the workspace, as git or the copy last told.</summary>
    public List<string> ChangedFiles { get; set; } = [];

    /// <summary>The agent's own plan as it last wrote it.</summary>
    public List<AgentPlanItem> Plan { get; set; } = [];

    /// <summary>The last steps in Activity's words: for an agent that takes the task over.</summary>
    public List<string> Steps { get; set; } = [];

    /// <summary>How the last run ended, in the agent's words.</summary>
    public string LastWords { get; set; } = string.Empty;

    /// <summary>The project's check as it last ran: command, outcome and the end of its output.</summary>
    public string? LastCheck { get; set; }

    /// <summary>In Perforce: the pending changelist Link keeps the task's files in, as P4V shows it; null elsewhere.</summary>
    public int? Changelist { get; set; }

    /// <summary>
    /// Link picks each run's effort (see <see cref="Services.AutoEffort"/>). An effort chosen by hand pins it, so a task
    /// of an older Link, which had none, keeps its own.
    /// </summary>
    public bool AutoEffort { get; set; }

    /// <summary>The specialist the task runs as, the file name of its role (<see cref="Services.ProjectRoles"/>); null: General.</summary>
    public string? RoleId { get; set; }

    /// <summary>The names of the skills the agent is told to read first, as the card or the last change of specialist picked them.</summary>
    public List<string> Skills { get; set; } = [];

    [JsonIgnore]
    public AgentRunRecord? LastRun => Runs.Count == 0 ? null : Runs[^1];

    public void Add(AgentEntryKind kind, string text)
    {
        Entries.Add(new AgentTaskEntry(DateTimeOffset.Now, kind, text.Length <= 600 ? text : text[..600] + "…"));
        UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>A session of an earlier Link, which kept sessions rather than tasks: a task of one run.</summary>
    public static AgentTaskRecord FromSession(AgentSessionRecord session) => new()
    {
        Goal = session.Goal,
        Brief = session.Goal,
        Workspace = session.Workspace,
        CreatedAt = session.UpdatedAt,
        UpdatedAt = session.UpdatedAt,
        Status = AgentTaskStatus.Done,
        Runs =
        [
            new AgentRunRecord
            {
                Provider = session.Provider,
                Model = session.Model,
                Effort = session.Effort,
                Access = session.Access,
                AutoApprove = session.AutoApprove,
                SessionId = session.SessionId,
                StartedAt = session.UpdatedAt,
                Outcome = "an earlier session"
            }
        ],
        Steps = [.. session.Steps],
        LastWords = session.Summary
    };
}

/// <summary>How a task stands; only Finish closes it, and a closed one can still be taken up from the history.</summary>
public enum AgentTaskStatus
{
    Running,
    NeedsYou,
    Done,
    CheckFailed,
    Failed,
    Stopped,
    Finished
}

/// <summary>One run on a task: who worked, how, in which session, how it ended and what it spent.</summary>
public sealed record AgentRunRecord
{
    public AgentProviderKind Provider { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }

    /// <summary>Why Link chose the effort when the task was on Auto; null when it was chosen by hand.</summary>
    public string? EffortReason { get; init; }

    /// <summary>The specialist this run worked as; null: General.</summary>
    public string? RoleId { get; init; }

    public AgentAccess Access { get; init; }
    public bool AutoApprove { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; init; }
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The run failed, or the project's check did after it. A Stop is neither: it is the user's choice or a limit.</summary>
    [JsonIgnore]
    public bool Failed => Outcome.StartsWith("Failed", StringComparison.Ordinal) || Outcome.StartsWith("Check failed", StringComparison.Ordinal);

    /// <summary>This run's own tokens: for a Codex session the difference from its run before.</summary>
    public long? InputTokens { get; init; }

    public long? CachedTokens { get; init; }
    public long? OutputTokens { get; init; }
    public decimal? CostUsd { get; init; }

    /// <summary>What the session held when the run ended and the model's window; null when the CLI did not tell.</summary>
    public long? ContextTokens { get; init; }

    public long? ContextWindow { get; init; }

    /// <summary>The CLI's loop check named a kind of loop in the run.</summary>
    public bool Looped { get; init; }

    /// <summary>The run's steps in Activity's words, the last of them: what the task's thread folds under the run.</summary>
    public List<string> Steps { get; init; } = [];

    /// <summary>The files the run changed, relative to the workspace.</summary>
    public List<string> Files { get; init; } = [];

    /// <summary>The agent's answer at the end of the run, as the thread shows it.</summary>
    public string Words { get; init; } = string.Empty;
}

/// <summary>A line of a task's history.</summary>
public sealed record AgentTaskEntry(DateTimeOffset At, AgentEntryKind Kind, string Text);

public enum AgentEntryKind
{
    /// <summary>The brief the task began with.</summary>
    Brief,

    /// <summary>The user's words: a reply, a correction, words for after the run, a note.</summary>
    You,

    /// <summary>A run ended: who, how long, how.</summary>
    Run,

    Check,

    /// <summary>Another agent took the task over, or the first one took it back.</summary>
    HandOff,

    /// <summary>The same session went on with another model.</summary>
    Model,

    /// <summary>An agent's usage limit ran out or ran low.</summary>
    Limit,

    /// <summary>The agent's last words of a run.</summary>
    Said,

    Finished,

    /// <summary>The agent could not start: no run, no session.</summary>
    NoStart
}

/// <summary>
/// A step of the agent's own plan (Claude Code's tasks, Codex's plan). <see cref="Doing"/> is what the agent says while
/// it works on the step ("Fixing the jump"); <see cref="Number"/> is the task's number in Claude Code, by which its
/// TaskUpdate names it. Plans of an earlier Link have neither.
/// </summary>
public sealed record AgentPlanItem(string Text, bool Done, bool Active, string? Doing = null, int? Number = null);

/// <summary>
/// A usage window of an agent's subscription as the CLI last reported it: "5 h" or "week", how much of it is used and
/// when it resets. <see cref="Reached"/>: the agent refuses work until then.
/// </summary>
public sealed record AgentLimit(
    AgentProviderKind Provider,
    string Window,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    bool Reached,
    DateTimeOffset SeenAt);

/// <summary>How much model a run gets: each agent's three levels are a model and an effort chosen in Settings.</summary>
public enum AgentLevel
{
    /// <summary>Quick and cheap: small edits and Link's own answers (a map, a skill, a note).</summary>
    Light,

    /// <summary>The model and effort of everyday tasks.</summary>
    Standard,

    /// <summary>The strongest: plans, hard problems, and a task that failed on Standard.</summary>
    Deep
}

/// <summary>A level's model and effort; null leaves either to the CLI's own settings.</summary>
public sealed class AgentLevelChoice
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
}

/// <summary>
/// A session an earlier Link kept per project (agent-sessions.json); read once to become a task, see
/// <see cref="AgentTaskRecord.FromSession"/>.
/// </summary>
public sealed class AgentSessionRecord
{
    public required string SessionId { get; init; }
    public AgentProviderKind Provider { get; init; }
    public required string Workspace { get; init; }

    /// <summary>The task the session began with, as the card's goal said it.</summary>
    public required string Goal { get; init; }

    public AgentAccess Access { get; init; }
    public bool AutoApprove { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }

    /// <summary>The last run's result, or how it ended.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The task's last steps as Activity showed them: for the other agent, should it take the task over later.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>A model or effort level as the agent's CLI takes it, and as the user reads it.</summary>
public sealed record AgentChoice(string Value, string Label)
{
    // A list shows its label, in the box of a ComboBox too.
    public override string ToString() => Label;
}

/// <summary>
/// What the task card offers first; Settings chooses it. The user's tasks always change things: read only is Link's
/// own, for the answers it writes (a map, a skill). The numbers stay those of older settings.
/// </summary>
public enum AgentAccessChoice
{
    /// <summary>Changes files in the workspace; any other command needs the user's yes.</summary>
    Edit = 1,

    /// <summary>Changes files and runs commands without asking; the agent's own review stops risky ones.</summary>
    Auto = 2
}

/// <summary>Claude Code's tool rules as Link sorts them: what only looks and what changes things.</summary>
public static class AgentPermissions
{
    /// <summary>Claude Code's tools that change files; read only denies them whatever the project's rules allow.</summary>
    public static readonly IReadOnlyList<string> EditTools = ["Edit", "Write", "NotebookEdit"];

    /// <summary>
    /// Whether a rule lets the agent change things: a file tool, or a whole shell (any command, not one command such as
    /// <c>Bash(git log:*)</c>). Such a rule belongs to edits, never to read only.
    /// </summary>
    public static bool ChangesThings(string rule)
    {
        var tool = rule.Split('(')[0].Trim();
        return EditTools.Contains(tool) || tool is "MultiEdit" || (!rule.Contains('(') && tool is "Bash" or "PowerShell");
    }
}

/// <summary>One run of an agent: what to do, where and with what access. A resumed run continues its session.</summary>
public sealed record AgentTask
{
    public required AgentProviderKind Provider { get; init; }
    public required string Workspace { get; init; }
    public required string Prompt { get; init; }
    public AgentAccess Access { get; init; } = AgentAccess.Analyze;

    /// <summary>Plan first: the agent only reads and answers with a plan, whatever the access.</summary>
    public bool PlanOnly { get; init; }

    /// <summary>The session to continue; null starts a new one.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// Tool rules Claude Code may use without asking, e.g. <c>Bash(dotnet build:*)</c>: the project's read list, and
    /// its Do list when the access is Do.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Images to look at, such as the snapshot's screenshot.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>
    /// With edits: files change and commands run without asking, and the agent's own review stops risky ones (Claude
    /// Code's auto mode, Codex's automatic review of what would leave its sandbox).
    /// </summary>
    public bool AutoApprove { get; init; }

    /// <summary>The model to run, such as claude-opus-5-5; null leaves it to the CLI's own settings.</summary>
    public string? Model { get; init; }

    /// <summary>How hard the model thinks, in the CLI's own levels (see <c>IAgentProvider.Efforts</c>); null: its settings.</summary>
    public string? Effort { get; init; }

    /// <summary>Why Link chose <see cref="Effort"/> for a task on Auto (see <see cref="Services.AutoEffort"/>); null when the user did.</summary>
    public string? EffortReason { get; init; }

    /// <summary>The specialist the task runs as (see <see cref="Services.ProjectRoles"/>): a new session is told its role. Null: General.</summary>
    public string? RoleId { get; init; }

    /// <summary>The names of the workspace's skills picked for the task: a new session is told to read them first.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>The agent's plan so far, for a session that goes on: its TaskUpdate names a task by the number it was created with.</summary>
    public IReadOnlyList<AgentPlanItem> Plan { get; init; } = [];

    /// <summary>
    /// A guard against a run that goes on and on (Claude Code's <c>--max-turns</c>): each turn spends the provider's
    /// subscription. Well above real work: measured tasks took 21–71 tool calls, so 40 would stop them mid-way.
    /// </summary>
    public int MaxTurns { get; init; } = 150;

    /// <summary>The tokens near which the CLI compacts the session by itself (Settings' Context budget); null leaves it alone.</summary>
    public int? ContextBudget { get; init; }

    /// <summary>
    /// A file of the workspace, relative to it, that Link writes the agent's answer to, such as the project map: the
    /// only change of a read-only task, made by Link rather than by the agent.
    /// </summary>
    public string? AnswerFile { get; init; }

    /// <summary>The answer is a SKILL.md, which Link writes to the workspace's skills under the name it gives.</summary>
    public bool AnswerIsSkill { get; init; }

    /// <summary>The project's check, run in the workspace after Do; while it fails, the agent gets its output to fix.</summary>
    public string? CheckCommand { get; init; }
}

public enum AgentEventKind
{
    /// <summary>The session started: its id, model and what the provider brought.</summary>
    Status,

    /// <summary>A tool call: a command, a file read or an edit.</summary>
    Tool,

    /// <summary>What the agent says.</summary>
    Output,

    /// <summary>A file the agent changed.</summary>
    Artifact,

    /// <summary>The agent was refused something only the user can allow.</summary>
    NeedsInput,

    /// <summary>A tool call came back with an error; <see cref="AgentEvent.Text"/> is the error. Counted, not listed.</summary>
    ToolFailed,

    /// <summary>The agent seems to go round in circles; <see cref="AgentEvent.Detail"/> is the kind of loop. It goes on.</summary>
    Loop,

    /// <summary>The agent wrote its plan again; <see cref="AgentEvent.Plan"/> is the whole of it.</summary>
    Plan,

    /// <summary>The CLI told how much of the subscription is used; <see cref="AgentEvent.Limits"/> are its windows.</summary>
    Limit,

    /// <summary>The session's context is told: <see cref="AgentEvent.ContextTokens"/> is its size now. Counted, not listed.</summary>
    Context,

    Completed,
    Error
}

/// <summary>
/// Something an agent did or said. <see cref="Detail"/> is the file path for an artifact, the refused tool for
/// needs-input and the tool's name for a tool call or its failure.
/// </summary>
public sealed record AgentEvent(AgentEventKind Kind, string Text, string? Detail = null)
{
    /// <summary>A tool call that only looks around: it finds, lists or reads files.</summary>
    public bool IsSearch { get; init; }

    /// <summary>
    /// A tool call that works other than by editing a file: an MCP call or a command that runs code (a script, a build).
    /// In a game editor the work goes this way.
    /// </summary>
    public bool IsWork { get; init; }

    /// <summary>A tool call's arguments, as the loop check compares calls; the row shows only their start.</summary>
    public string Arguments { get; init; } = string.Empty;

    public IReadOnlyList<AgentPlanItem> Plan { get; init; } = [];
    public IReadOnlyList<AgentLimit> Limits { get; init; } = [];

    /// <summary>For a context event: what the session holds, and the model's window when the CLI named it.</summary>
    public long ContextTokens { get; init; }

    public long? ContextWindow { get; init; }
}

/// <summary>How a run ended. <see cref="Denials"/> are the tool rules the agent was refused, to allow and try again.</summary>
public sealed record AgentResult
{
    public string? SessionId { get; init; }
    public string Summary { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public bool Stopped { get; init; }
    public int ExitCode { get; init; }
    public IReadOnlyList<string> Denials { get; init; } = [];
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    public AgentStats Stats { get; init; } = new();

    /// <summary>The kinds of loop noticed in the run, such as "repeated error"; see <see cref="Services.AgentLoopDetector"/>.</summary>
    public IReadOnlyList<string> Loops { get; init; } = [];

    /// <summary>The tail of the CLI's stdout and stderr, shown on request.</summary>
    public string Log { get; init; } = string.Empty;
}

/// <summary>
/// What a run cost: its turns, tool calls and how many of them only looked around, failed calls, tokens, money and
/// time. They tell which parts of the harness save work; a number the provider does not report is null.
/// </summary>
public sealed record AgentStats
{
    public int Turns { get; init; }
    public int ToolCalls { get; init; }
    public int Searches { get; init; }

    /// <summary>Searches before the first changed file, null when nothing changed: what a map of the project cuts.</summary>
    public int? SearchesBeforeChange { get; init; }

    public int ToolErrors { get; init; }

    /// <summary>All the input the model read, the cached part included.</summary>
    public long? InputTokens { get; init; }

    public long? CachedTokens { get; init; }
    public long? OutputTokens { get; init; }

    /// <summary>What the run would cost on the API; Claude Code reports it, Codex does not.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// The tokens are the whole session's so far, not this run's: Codex counts them so when it resumes a session.
    /// </summary>
    public bool SessionTokens { get; init; }

    /// <summary>What the session held at the run's last word and the model's window: a level, not a sum.</summary>
    public long? ContextTokens { get; init; }

    public long? ContextWindow { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>These numbers and those of the run that continued this one, as one task's.</summary>
    public AgentStats Add(AgentStats next)
    {
        // A continuation that counts the session already holds this run's tokens.
        var tokens = next.SessionTokens ? next : null;
        return new AgentStats
        {
            Turns = Turns + next.Turns,
            ToolCalls = ToolCalls + next.ToolCalls,
            Searches = Searches + next.Searches,
            SearchesBeforeChange = SearchesBeforeChange ?? (next.SearchesBeforeChange is { } count ? Searches + count : null),
            ToolErrors = ToolErrors + next.ToolErrors,
            InputTokens = tokens is not null ? tokens.InputTokens : Sum(InputTokens, next.InputTokens),
            CachedTokens = tokens is not null ? tokens.CachedTokens : Sum(CachedTokens, next.CachedTokens),
            OutputTokens = tokens is not null ? tokens.OutputTokens : Sum(OutputTokens, next.OutputTokens),
            CostUsd = CostUsd is null && next.CostUsd is null ? null : (CostUsd ?? 0) + (next.CostUsd ?? 0),
            SessionTokens = SessionTokens || next.SessionTokens,
            ContextTokens = next.ContextTokens ?? ContextTokens,
            ContextWindow = next.ContextWindow ?? ContextWindow,
            Duration = Duration + next.Duration
        };

        static long? Sum(long? first, long? second) => first is null && second is null ? null : (first ?? 0) + (second ?? 0);
    }
}

/// <summary>What Settings shows for a provider: whether its CLI is found, signed in, and its version.</summary>
public sealed record AgentProviderStatus(string? ExecutablePath, string? Version, bool? SignedIn)
{
    public bool Installed => ExecutablePath is not null;
}

/// <summary>Where the agents' CLIs are and which one works by default.</summary>
public sealed class AgentSettings
{
    public AgentProviderKind DefaultProvider { get; set; } = AgentProviderKind.ClaudeCode;

    /// <summary>The access the task card starts with; the read only of older settings becomes Auto.</summary>
    public AgentAccessChoice DefaultAccess { get; set; } = AgentAccessChoice.Auto;

    /// <summary>A chosen claude.exe; null finds it in PATH and the usual install places.</summary>
    public string? ClaudePath { get; set; }

    /// <summary>A chosen codex.exe; null finds it in PATH and the usual install places.</summary>
    public string? CodexPath { get; set; }

    /// <summary>Claude Code's model, such as claude-opus-5-5 or the alias opus; null: Claude Code's own setting.</summary>
    public string? ClaudeModel { get; set; }

    /// <summary>Claude Code's effort, the last one chosen on the card; null: Claude Code's own setting.</summary>
    public string? ClaudeEffort { get; set; }

    /// <summary>Codex's model, such as gpt-5.6-sol; null: the model of Codex's config.</summary>
    public string? CodexModel { get; set; }

    /// <summary>Codex's reasoning effort, the last one chosen on the card; null: the effort of Codex's config.</summary>
    public string? CodexEffort { get; set; }

    // The Light and Deep levels; Standard is the model and effort above, the user's everyday choice.
    public AgentLevelChoice ClaudeLight { get; set; } = new() { Model = "claude-sonnet-5-5", Effort = "medium" };
    public AgentLevelChoice ClaudeDeep { get; set; } = new() { Model = "claude-opus-5-5", Effort = "xhigh" };
    public AgentLevelChoice CodexLight { get; set; } = new() { Effort = "low" };
    public AgentLevelChoice CodexDeep { get; set; } = new() { Effort = "xhigh" };

    /// <summary>Plan first runs on Deep, and the plan is carried out on Standard in the same session.</summary>
    public bool PlanOnDeep { get; set; } = true;

    /// <summary>Link's own answers (a map, a skill, a note for Start over) run on Light.</summary>
    public bool OwnAnswersOnLight { get; set; } = true;

    /// <summary>Link picks each run's effort for the tasks that start on Auto; the model and the agent stay the user's.</summary>
    public bool AutoEffort { get; set; } = true;

    /// <summary>The custom model: its server and model. It is the user's to pick on the card; it is never the default agent.</summary>
    public CustomModelSettings Custom { get; set; } = new();

    /// <summary>Link's own answers (a map, a skill, a note) run on the custom model, not on the subscription agent.</summary>
    public bool OwnAnswersOnCustom { get; set; }

    private const int DefaultContextBudget = 550_000;

    /// <summary>
    /// The tokens near which an agent's CLI compacts its session by itself, so that a long task does not grow without
    /// end; the CLI caps it at the model's own window.
    /// </summary>
    public int ContextBudget { get; set; } = DefaultContextBudget;

    public void Normalize()
    {
        if (!Enum.IsDefined(DefaultProvider) || DefaultProvider == AgentProviderKind.Custom) DefaultProvider = AgentProviderKind.ClaudeCode;
        if (!Enum.IsDefined(DefaultAccess)) DefaultAccess = AgentAccessChoice.Auto;
        if (ContextBudget is < 50_000 or > 2_000_000) ContextBudget = DefaultContextBudget;
        ClaudePath = Trimmed(ClaudePath);
        CodexPath = Trimmed(CodexPath);
        ClaudeModel = Trimmed(ClaudeModel);
        ClaudeEffort = Trimmed(ClaudeEffort);
        CodexModel = Trimmed(CodexModel);
        CodexEffort = Trimmed(CodexEffort);
        foreach (var level in new[] { ClaudeLight ??= new(), ClaudeDeep ??= new(), CodexLight ??= new(), CodexDeep ??= new() })
        {
            level.Model = Trimmed(level.Model);
            level.Effort = Trimmed(level.Effort);
        }

        Custom ??= new();
        Custom.Normalize();
    }

    /// <summary>A level's model and effort for an agent; null parts are left to the CLI.</summary>
    public (string? Model, string? Effort) Level(AgentProviderKind provider, AgentLevel level)
    {
        // One model without an effort setting: its levels are all alike.
        if (provider == AgentProviderKind.Custom) return (Custom.Model, null);
        var claude = provider == AgentProviderKind.ClaudeCode;
        var choice = level switch
        {
            AgentLevel.Light => claude ? ClaudeLight : CodexLight,
            AgentLevel.Deep => claude ? ClaudeDeep : CodexDeep,
            _ => null
        };
        return choice is null ? (ModelFor(provider), EffortFor(provider)) : (choice.Model, choice.Effort);
    }

    /// <summary>
    /// Who writes Link's own answers (a map, a skill, a note) and on what: Light, unless Settings says otherwise, and the
    /// custom model when Settings asks for it and it is set up (<paramref name="customConfigured"/>). The custom model is
    /// the user's choice for a task, so a project that last used it does not send its map there by itself. An answer
    /// that goes on in the task's session (<paramref name="inSession"/>) stays with the task's agent: another server is
    /// not trusted to read a session another model wrote (its thinking blocks are that model's).
    /// </summary>
    public (AgentProviderKind Provider, string? Model, string? Effort) OwnAnswers(AgentProviderKind provider, bool inSession, bool customConfigured)
    {
        var onCustom = OwnAnswersOnCustom && customConfigured && !inSession;
        var writer = onCustom ? AgentProviderKind.Custom : provider == AgentProviderKind.Custom && !inSession ? DefaultProvider : provider;
        var (model, effort) = Level(writer, OwnAnswersOnLight ? AgentLevel.Light : AgentLevel.Standard);
        return (writer, model, effort);
    }

    public string? PathFor(AgentProviderKind provider) => provider.RunsOnClaudeCode() ? ClaudePath : CodexPath;

    public string? ModelFor(AgentProviderKind provider) => provider switch
    {
        AgentProviderKind.ClaudeCode => ClaudeModel,
        AgentProviderKind.Custom => Custom.Model,
        _ => CodexModel
    };

    public string? EffortFor(AgentProviderKind provider) => provider switch
    {
        AgentProviderKind.ClaudeCode => ClaudeEffort,
        AgentProviderKind.Custom => null,
        _ => CodexEffort
    };

    public void SetEffort(AgentProviderKind provider, string? effort)
    {
        if (provider == AgentProviderKind.ClaudeCode) ClaudeEffort = Trimmed(effort);
        else if (provider == AgentProviderKind.Codex) CodexEffort = Trimmed(effort);
    }

    public AgentSettings Clone() => new()
    {
        DefaultProvider = DefaultProvider,
        DefaultAccess = DefaultAccess,
        ClaudePath = ClaudePath,
        CodexPath = CodexPath,
        ClaudeModel = ClaudeModel,
        ClaudeEffort = ClaudeEffort,
        CodexModel = CodexModel,
        CodexEffort = CodexEffort,
        ClaudeLight = new AgentLevelChoice { Model = ClaudeLight.Model, Effort = ClaudeLight.Effort },
        ClaudeDeep = new AgentLevelChoice { Model = ClaudeDeep.Model, Effort = ClaudeDeep.Effort },
        CodexLight = new AgentLevelChoice { Model = CodexLight.Model, Effort = CodexLight.Effort },
        CodexDeep = new AgentLevelChoice { Model = CodexDeep.Model, Effort = CodexDeep.Effort },
        PlanOnDeep = PlanOnDeep,
        OwnAnswersOnLight = OwnAnswersOnLight,
        AutoEffort = AutoEffort,
        Custom = Custom.Clone(),
        OwnAnswersOnCustom = OwnAnswersOnCustom,
        ContextBudget = ContextBudget
    };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
