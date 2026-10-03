namespace Link.Desktop.Models;

public sealed class ContextItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ContextItemType Type { get; init; }
    public required string Label { get; init; }
    public string? Text { get; init; }
    public string? FilePath { get; init; }
    public string? MimeType { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public enum ContextItemType
{
    Screenshot,
    Clipboard,
    Voice,

    /// <summary>A short summary of the chat a project left, for the new chat's first prompt.</summary>
    ChatSummary,

    /// <summary>The project note. It goes to the chat and to an agent; "only my text" leaves it out.</summary>
    ProjectState,

    /// <summary>An agent's result and the files it changed, sent to the chat to ask about the work.</summary>
    AgentResult,

    /// <summary>
    /// The app the user works in: its name and window title go with a Chat question while this chip stands in the
    /// stack. The chip itself carries no text; removing it leaves them out of one question.
    /// </summary>
    Application
}
