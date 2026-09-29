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

    /// <summary>The project's state. It goes to the chat; a request that sends only the prompt leaves it out.</summary>
    ProjectState
}
