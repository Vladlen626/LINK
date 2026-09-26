using System.Diagnostics;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class BrowserChatIntegration : IChatIntegration, IDisposable
{
    private const string NewChatsBuiltIn =
        "New chats are started in the built-in ChatGPT window: Settings → ChatGPT → Connection.";
    private readonly BrowserBridgeServer _bridge = new();

    public BrowserChatIntegration() => _bridge.Start();

    public string TransportId => ChatTransports.Browser;

    public ChatConnectionState GetConnectionState(ChatConversationLink? conversation)
    {
        if (conversation is null) return ChatConnectionState.NoLinkedConversation;
        return _bridge.IsRunning &&
               ChatTransports.IsChatGpt(conversation) &&
               _bridge.IsConversationAvailable(conversation.ConversationId)
            ? ChatConnectionState.ConversationAvailable
            : ChatConnectionState.BridgeUnavailable;
    }

    public IReadOnlyList<AvailableChatConversation> GetAvailableConversations() =>
        _bridge.GetAvailableConversations();

    // A browser window is titled after its active tab: a ChatGPT page or a conversation the companion announced.
    public bool IsConversationWindow(string windowTitle) =>
        windowTitle.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        _bridge.GetAvailableConversations().Any(conversation =>
            windowTitle.StartsWith(conversation.Title, StringComparison.Ordinal));

    /// <summary>Opens the linked conversation, or ChatGPT itself, in the default browser.</summary>
    public void OpenChat(ChatConversationLink? conversation)
    {
        var address = conversation is null ? ChatPageAddress.Home : ChatPageAddress.Of(conversation);
        Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    // The browser companion only reaches chats open in tabs; a new one is started in the built-in ChatGPT window.
    public ChatConversationLink NewChat(ChatConversationLink? current) => throw new InvalidOperationException(NewChatsBuiltIn);

    public async Task<ChatResponse> SendAsync(
        PromptRequest request,
        ChatConversationLink conversation,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureChatGpt(conversation);
        var pageRequest = await ChatPrompt.CreateAsync(request, conversation.ConversationId, cancellationToken)
            .ConfigureAwait(false);
        return await _bridge.EnqueueAsync(pageRequest, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<ChatSnapshot> CheckAsync(ChatConversationLink conversation, CancellationToken cancellationToken)
    {
        EnsureChatGpt(conversation);
        return _bridge.ReadAsync(conversation.ConversationId, cancellationToken);
    }

    public void Dispose() => _bridge.Dispose();

    private static void EnsureChatGpt(ChatConversationLink conversation)
    {
        if (!ChatTransports.IsChatGpt(conversation))
        {
            throw new InvalidOperationException($"Unsupported chat transport: {conversation.TransportId}");
        }

        if (conversation.IsNew) throw new InvalidOperationException(NewChatsBuiltIn);
    }
}
