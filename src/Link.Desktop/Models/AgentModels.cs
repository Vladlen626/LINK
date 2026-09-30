namespace Link.Desktop.Models;

/// <summary>The command-line agent that does work in a workspace.</summary>
public enum AgentProviderKind
{
    ClaudeCode,
    Codex
}

/// <summary>What an agent may do in the workspace. Analyze only reads; Do is chosen for one task, never by default.</summary>
public enum AgentAccess
{
    Analyze,
    Do
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

    /// <summary>A guard against a run that goes on and on; each turn spends the provider's subscription.</summary>
    public int MaxTurns { get; init; } = 40;

    /// <summary>
    /// A file of the workspace, relative to it, that Link writes the agent's answer to, such as the project map: the
    /// only change of a read-only task, made by Link rather than by the agent.
    /// </summary>
    public string? AnswerFile { get; init; }

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

    /// <summary>A tool call's arguments, as the loop check compares calls; the row shows only their start.</summary>
    public string Arguments { get; init; } = string.Empty;
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

    /// <summary>A chosen claude.exe; null finds it in PATH and the usual install places.</summary>
    public string? ClaudePath { get; set; }

    /// <summary>A chosen codex.exe; null finds it in PATH and the usual install places.</summary>
    public string? CodexPath { get; set; }

    public void Normalize()
    {
        if (!Enum.IsDefined(DefaultProvider)) DefaultProvider = AgentProviderKind.ClaudeCode;
        ClaudePath = string.IsNullOrWhiteSpace(ClaudePath) ? null : ClaudePath.Trim();
        CodexPath = string.IsNullOrWhiteSpace(CodexPath) ? null : CodexPath.Trim();
    }

    public string? PathFor(AgentProviderKind provider) =>
        provider == AgentProviderKind.ClaudeCode ? ClaudePath : CodexPath;

    public AgentSettings Clone() => new() { DefaultProvider = DefaultProvider, ClaudePath = ClaudePath, CodexPath = CodexPath };
}
