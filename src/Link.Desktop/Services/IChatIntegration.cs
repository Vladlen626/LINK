using Link.Desktop.Models;

namespace Link.Desktop.Services;

public interface IChatIntegration
{
    string TransportId { get; }
    ChatConnectionState GetConnectionState(ChatConversationLink? conversation);
    IReadOnlyList<AvailableChatConversation> GetAvailableConversations();

    /// <summary>Whether a desktop window with this title shows the chat itself rather than the user's work.</summary>
    bool IsConversationWindow(string windowTitle);

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
