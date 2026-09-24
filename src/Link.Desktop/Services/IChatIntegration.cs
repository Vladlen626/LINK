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
}
