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

    Completed,
    Error
}

/// <summary>
/// Something an agent did or said. <see cref="Detail"/> is the file path for an artifact and the refused tool for
/// needs-input.
/// </summary>
public sealed record AgentEvent(AgentEventKind Kind, string Text, string? Detail = null);

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

    /// <summary>The tail of the CLI's stdout and stderr, shown on request.</summary>
    public string Log { get; init; } = string.Empty;
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
