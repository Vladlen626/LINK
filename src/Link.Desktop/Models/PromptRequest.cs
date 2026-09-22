namespace Link.Desktop.Models;

public sealed class PromptRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public string CurrentTask { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<ContextItem> Context { get; init; } = [];
}
