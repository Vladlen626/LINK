using System.Security.Cryptography;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// ChatGPT in LINK's own page (WebView2). The browser gives a background tab a few characters a second; this page is
/// kept visible to its engine, off every monitor, so answers arrive at full speed and no browser has to stay open.
/// </summary>
public sealed class EmbeddedChatIntegration : IChatIntegration, IDisposable
{
    private readonly string _token;
    private readonly ChatRequestBroker _broker;
    private readonly ChatPageHost _host;

    /// <param name="stateFile">Keeps the chat modes the companion set, across restarts.</param>
    public EmbeddedChatIntegration(string stateFile)
        : this(stateFile, BridgeTimings.Default, PageTimings.Default, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
    {
    }

    /// <summary>An integration with other waits and a known token, for tests.</summary>
    internal EmbeddedChatIntegration(string stateFile, BridgeTimings timings, PageTimings pageTimings, string token)
    {
        _token = token;
        _broker = new ChatRequestBroker(timings);
        _host = new ChatPageHost(_broker, stateFile, pageTimings, token);
    }

    public string TransportId => ChatTransports.Embedded;

    /// <summary>The script the page runs in every new document; it carries this session's token.</summary>
    internal string ComposePageScript() => ChatPageScript.Compose(_token);

    internal void Attach(IChatPage page) => _host.Attach(page);

    public ChatConnectionState GetConnectionState(ChatConversationLink? conversation)
    {
        if (conversation is null) return ChatConnectionState.NoLinkedConversation;
        // Any linked conversation can be opened: the page goes there when a prompt is sent.
        return ChatTransports.IsChatGpt(conversation) ? _host.State : ChatConnectionState.BridgeUnavailable;
    }

    public IReadOnlyList<AvailableChatConversation> GetAvailableConversations() => _host.Conversations;

    // LINK's own page belongs to LINK; a ChatGPT tab in the browser is still the chat, not the user's work.
    public bool IsConversationWindow(string windowTitle) =>
        windowTitle.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);

    public void OpenChat(ChatConversationLink? conversation) =>
        _host.Reveal(conversation is not null && ChatTransports.IsChatGpt(conversation) ? conversation : null);

    public ChatConversationLink NewChat(ChatConversationLink? current)
    {
        var place = ChatPageAddress.NewChatPlace(current is not null && ChatTransports.IsChatGpt(current) ? current : null);
        return new ChatConversationLink
        {
            TransportId = TransportId,
            ConversationId = string.Empty,
            Title = ChatPageAddress.IsProjectPlace(place) ? "New chat in the ChatGPT project" : "New chat",
            Url = place.AbsoluteUri
        };
    }

    public async Task<ChatResponse> SendAsync(
        PromptRequest request,
        ChatConversationLink conversation,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureChatGpt(conversation);
        var pageRequest = await ChatPrompt.CreateAsync(request, conversation.ConversationId, cancellationToken)
            .ConfigureAwait(false);
        // A new chat opens fresh at its place; the page names the conversation once ChatGPT has started it.
        if (conversation.IsNew)
        {
            await _host.OpenNewChatAsync(ChatPageAddress.NewChatPlace(conversation), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _host.OpenAsync(conversation, cancellationToken).ConfigureAwait(false);
        }

        return await _broker.SendAsync(pageRequest, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatSnapshot> CheckAsync(ChatConversationLink conversation, CancellationToken cancellationToken)
    {
        EnsureChatGpt(conversation);
        if (conversation.IsNew)
        {
            throw new InvalidOperationException("The new chat starts with your next message; there is nothing to check yet.");
        }

        await _host.OpenAsync(conversation, cancellationToken).ConfigureAwait(false);
        return await _broker.ReadAsync(conversation.ConversationId, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _host.Dispose();
        _broker.Dispose();
    }

    private static void EnsureChatGpt(ChatConversationLink conversation)
    {
        if (!ChatTransports.IsChatGpt(conversation))
        {
            throw new InvalidOperationException($"Unsupported chat transport: {conversation.TransportId}");
        }
    }
}
