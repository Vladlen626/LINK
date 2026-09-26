using Link.Desktop.Models;

namespace Link.Desktop.Services;

public interface IChatIntegration
{
    string TransportId { get; }
    ChatConnectionState GetConnectionState(ChatConversationLink? conversation);
    IReadOnlyList<AvailableChatConversation> GetAvailableConversations();

    /// <summary>Whether a desktop window with this title shows the chat itself rather than the user's work.</summary>
    bool IsConversationWindow(string windowTitle);

    /// <summary>Shows the user the chat itself: the linked conversation when there is one.</summary>
    void OpenChat(ChatConversationLink? conversation);

    /// <summary>
    /// A link to a new chat next to <paramref name="current"/>, in its ChatGPT project when it has one. The next prompt
    /// starts the chat, and <see cref="ChatProgressState.ChatStarted"/> names it then.
    /// </summary>
    ChatConversationLink NewChat(ChatConversationLink? current);

    Task<ChatResponse> SendAsync(
        PromptRequest request,
        ChatConversationLink conversation,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads what the linked chat shows now, for an answer that did not arrive. A prompt that reached the chat and
    /// still waits is completed with the chat's answer once that answer is finished.
    /// </summary>
    Task<ChatSnapshot> CheckAsync(ChatConversationLink conversation, CancellationToken cancellationToken);
}
