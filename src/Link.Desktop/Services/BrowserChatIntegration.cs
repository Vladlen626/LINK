using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class BrowserChatIntegration : IChatIntegration, IDisposable
{
    public const string BrowserTransportId = "browser-chatgpt";
    private const long MaxAttachmentBytes = 15 * 1024 * 1024;
    private const long MaxTotalAttachmentBytes = 25 * 1024 * 1024;
    private readonly BrowserBridgeServer _bridge = new();

    public BrowserChatIntegration() => _bridge.Start();

    public string TransportId => BrowserTransportId;

    public ChatConnectionState GetConnectionState(ChatConversationLink? conversation)
    {
        if (conversation is null) return ChatConnectionState.NoLinkedConversation;
        return _bridge.IsRunning &&
               conversation.TransportId == TransportId &&
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

    public async Task<ChatResponse> SendAsync(
        PromptRequest request,
        ChatConversationLink conversation,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (conversation.TransportId != TransportId)
        {
            throw new InvalidOperationException($"Unsupported chat transport: {conversation.TransportId}");
        }

        var attachments = await Task.Run(
            () => LoadImageAttachments(request.Context),
            cancellationToken).ConfigureAwait(false);
        var bridgeRequest = new BrowserBridgeRequest
        {
            RequestId = request.Id,
            ConversationId = conversation.ConversationId,
            Prompt = FormatPrompt(request),
            Effort = request.Effort.ToString().ToLowerInvariant(),
            Attachments = attachments
        };
        return await _bridge.EnqueueAsync(bridgeRequest, progress, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _bridge.Dispose();

    private static string FormatPrompt(PromptRequest request)
    {
        var result = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Text)) result.Append(request.Text.Trim());
        else result.Append("Please use the attached context.");

        // The hint works in any chat; the companion additionally switches the ChatGPT mode when it can.
        var effortHint = request.Effort switch
        {
            ResponseEffort.Fast => "[Fast answer: reply briefly and directly — the answer first, a few sentences or a short list at most, no preamble or recap, no extended reasoning.]",
            ResponseEffort.Deep => "[Deep answer: think hard about this and give a thorough, carefully reasoned answer.]",
            _ => null
        };
        if (effortHint is not null) result.Append("\n\n").Append(effortHint);

        var contextLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.ProjectName) &&
            !string.Equals(request.ProjectName.Trim(), "General", StringComparison.OrdinalIgnoreCase))
        {
            contextLines.Add($"Project: {request.ProjectName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.CurrentApplication?.ProcessName))
        {
            contextLines.Add($"Application: {request.CurrentApplication.ProcessName.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.CurrentApplication?.WindowTitle))
        {
            contextLines.Add($"Window: {request.CurrentApplication.WindowTitle.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.CurrentTask))
        {
            contextLines.Add($"Current task: {request.CurrentTask.Trim()}");
        }

        foreach (var item in request.Context.Where(item => !string.IsNullOrWhiteSpace(item.Text)))
        {
            contextLines.Add($"{item.Label}:\n{item.Text!.Trim()}");
        }

        if (contextLines.Count > 0)
        {
            result.Append("\n\n---\nContext\n");
            result.Append(string.Join("\n\n", contextLines));
        }

        return result.ToString();
    }

    private static IReadOnlyList<BrowserBridgeAttachment> LoadImageAttachments(IEnumerable<ContextItem> context)
    {
        var attachments = new List<BrowserBridgeAttachment>();
        long totalBytes = 0;

        foreach (var item in context.Where(item =>
                     item.FilePath is not null &&
                     item.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
        {
            var path = item.FilePath!;
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("A context image is no longer available.", path);
            if (file.Length > MaxAttachmentBytes)
            {
                throw new InvalidOperationException($"Image '{item.Label}' is larger than 15 MB.");
            }

            totalBytes += file.Length;
            if (totalBytes > MaxTotalAttachmentBytes)
            {
                throw new InvalidOperationException("Image attachments exceed the 25 MB bridge limit.");
            }

            attachments.Add(new BrowserBridgeAttachment
            {
                FileName = Path.GetFileName(path),
                MimeType = item.MimeType!,
                Base64 = Convert.ToBase64String(File.ReadAllBytes(path))
            });
        }

        return attachments;
    }
}
