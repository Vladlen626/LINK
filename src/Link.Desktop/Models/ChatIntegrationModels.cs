using System.Text.Json.Serialization;

namespace Link.Desktop.Models;

public sealed class ChatConversationLink
{
    public required string TransportId { get; init; }

    /// <summary>Empty for a new chat, which the next prompt starts at <see cref="Url"/>.</summary>
    public required string ConversationId { get; init; }

    public required string Title { get; init; }
    public required string Url { get; init; }

    /// <summary>A chat still to be started: its first prompt creates it, and the project is linked to it then.</summary>
    [JsonIgnore]
    public bool IsNew => ConversationId.Length == 0;
}

/// <summary>How a project's ChatGPT conversation was linked.</summary>
public static class ChatTransports
{
    /// <summary>LINK's own ChatGPT page.</summary>
    public const string Embedded = "embedded-chatgpt";

    /// <summary>Through the browser extension LINK used before; such links name the same conversations.</summary>
    public const string Browser = "browser-chatgpt";

    /// <summary>A ChatGPT conversation, linked either way: projects keep the chats they had.</summary>
    public static bool IsChatGpt(ChatConversationLink conversation) =>
        conversation.TransportId is Embedded or Browser;
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
    /// <summary>Empty when the answer is only a picture.</summary>
    public required string Text { get; init; }

    /// <summary>ChatGPT made a picture, which stays in its window to look at there.</summary>
    public bool Picture { get; init; }
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

    /// <summary>With <see cref="ChatProgressState.ChatStarted"/>: the conversation the prompt started.</summary>
    public AvailableChatConversation? StartedConversation { get; init; }

    /// <summary>With <see cref="ChatProgressState.Receiving"/>: ChatGPT is making a picture; the text then is not reported.</summary>
    public bool Picture { get; init; }
}

public enum ChatConnectionState
{
    NoLinkedConversation,

    /// <summary>The chat page is still loading.</summary>
    Connecting,

    BridgeUnavailable,

    /// <summary>ChatGPT shows no signed-in account: the linked conversation cannot be opened.</summary>
    SignInRequired,

    ConversationAvailable
}

public enum ChatProgressState
{
    Sending,
    Waiting,
    Receiving,

    /// <summary>The transport lost the chat for a moment and restores it; the request goes on.</summary>
    Reconnecting,

    /// <summary>A prompt to a new chat has started it: <see cref="ChatProgress.StartedConversation"/> names it.</summary>
    ChatStarted,

    Completed,

    /// <summary>The user stopped the request.</summary>
    Cancelled,

    Error
}
