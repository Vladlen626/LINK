namespace Link.Desktop.Models;

public sealed class PromptRequest
{
    public int SchemaVersion { get; init; } = 3;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public string CurrentTask { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public ResponseEffort Effort { get; init; } = ResponseEffort.Normal;
    public CurrentApplicationContext? CurrentApplication { get; init; }
    public IReadOnlyList<ContextItem> Context { get; init; } = [];
}

/// <summary>How much time and length the assistant should spend on an answer.</summary>
public enum ResponseEffort
{
    Normal,
    Fast,
    Deep
}
