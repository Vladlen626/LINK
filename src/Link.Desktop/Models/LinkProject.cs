namespace Link.Desktop.Models;

public sealed class LinkProject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string DataFolder { get; init; }
    public string CurrentTask { get; set; } = string.Empty;
    public ChatConversationLink? LinkedConversation { get; set; }
    public ProjectResponse? LastResponse { get; set; }

    public override string ToString() => Name;
}

/// <summary>The last assistant answer Link received from a project's linked conversation.</summary>
public sealed class ProjectResponse
{
    public required string ConversationId { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}
