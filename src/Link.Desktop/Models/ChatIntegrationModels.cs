namespace Link.Desktop.Models;

public sealed class ChatConversationLink
{
    public required string TransportId { get; init; }
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
}

public sealed class AvailableChatConversation
{
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }

    public override string ToString() => Title;
}

public sealed class ChatResponse
{
    public required string Text { get; init; }
}

public sealed class ChatProgress
{
    public required ChatProgressState State { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Error { get; init; }

    /// <summary>Transport information that does not change the response, e.g. which chat mode was applied.</summary>
    public string? Notice { get; init; }
}

public enum ChatConnectionState
{
    NoLinkedConversation,
    BridgeUnavailable,
    ConversationAvailable
}

public enum ChatProgressState
{
    Sending,
    Waiting,
    Receiving,
    Completed,
    Error
}
