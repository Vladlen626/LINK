using Link.Desktop.Models;

namespace Link.Desktop.Services;

public interface IChatIntegration
{
    string TransportId { get; }
    ChatConnectionState GetConnectionState(ChatConversationLink? conversation);
    IReadOnlyList<AvailableChatConversation> GetAvailableConversations();
    Task<ChatResponse> SendAsync(
        PromptRequest request,
        ChatConversationLink conversation,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken);
}
