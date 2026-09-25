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

/// <summary>
/// What the linked chat shows now: the answer to its last prompt, empty when there is none yet, and whether it is
/// still being written.
/// </summary>
public sealed class ChatSnapshot
{
    public required string Text { get; init; }
    public bool Generating { get; init; }

    /// <summary>A prompt that waited for this answer has been completed with it.</summary>
    public bool CompletedWaitingPrompt { get; init; }

    /// <summary>What the transport saw on the page, kept in link.log to find out why an answer went missing.</summary>
    public string? Diagnostics { get; init; }
}

public sealed class ChatProgress
{
    public required ChatProgressState State { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Error { get; init; }

    /// <summary>Transport information that does not change the response, e.g. which chat mode was applied.</summary>
    public string? Notice { get; init; }

    /// <summary>What the transport saw when the answer ended unusually, kept in link.log to find out why.</summary>
    public string? Diagnostics { get; init; }
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

    /// <summary>The transport lost the chat for a moment and restores it; the request goes on.</summary>
    Reconnecting,

    Completed,

    /// <summary>The user stopped the request.</summary>
    Cancelled,

    Error
}
