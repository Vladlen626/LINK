namespace Link.Desktop.Models;

public sealed class LinkProject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string DataFolder { get; init; }
    public string CurrentTask { get; set; } = string.Empty;

    /// <summary>What the project stands on now, in a few confirmed lines; null until the user writes it.</summary>
    public ProjectState? State { get; set; }

    /// <summary>The folder the project's work lives in, chosen by the user; null until chosen.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>The agent this project works with; null uses the default from Settings.</summary>
    public AgentProviderKind? AgentProvider { get; set; }

    /// <summary>
    /// Tool rules Claude Code may use without asking here, in Analyze as well: tools that only look, such as a Blender
    /// scene summary. Nothing that runs code or changes files.
    /// </summary>
    public List<string> AgentReadTools { get; set; } = [];

    /// <summary>Tool rules allowed only in Do: builds, exports, running code; "always for this project" adds here.</summary>
    public List<string> AgentDoTools { get; set; } = [];

    public ChatConversationLink? LinkedConversation { get; set; }
    public ProjectResponse? LastResponse { get; set; }

    public override string ToString() => Name;
}

/// <summary>A project's state: the task, decisions, key facts and links to chats, and when it was last confirmed.</summary>
public sealed class ProjectState
{
    public required string Text { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>The last assistant answer Link received from a project's linked conversation.</summary>
public sealed class ProjectResponse
{
    public required string ConversationId { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>A question to a project's chat and its answer, kept in the project's history.</summary>
public sealed class ProjectExchange
{
    public required string Question { get; init; }
    public required string Answer { get; init; }
    public DateTimeOffset AnsweredAt { get; init; } = DateTimeOffset.Now;
}
