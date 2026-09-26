using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>A LINK request as the ChatGPT page gets it: the prompt with its context as text, and its images.</summary>
internal static class ChatPrompt
{
    private const long MaxAttachmentBytes = 15 * 1024 * 1024;
    private const long MaxTotalAttachmentBytes = 25 * 1024 * 1024;

    /// <summary>The page request for a prompt; its images are read from disk off the calling thread.</summary>
    public static async Task<ChatPageRequest> CreateAsync(
        PromptRequest request,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var attachments = await Task.Run(
            () => LoadImageAttachments(request.Context),
            cancellationToken).ConfigureAwait(false);
        return new ChatPageRequest
        {
            RequestId = request.Id,
            ConversationId = conversationId,
            Prompt = Format(request),
            Effort = request.Effort.ToString().ToLowerInvariant(),
            Attachments = attachments
        };
    }

    private static string Format(PromptRequest request)
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

    private static IReadOnlyList<ChatPageAttachment> LoadImageAttachments(IEnumerable<ContextItem> context)
    {
        var attachments = new List<ChatPageAttachment>();
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
                throw new InvalidOperationException("Image attachments exceed the 25 MB limit.");
            }

            attachments.Add(new ChatPageAttachment
            {
                FileName = Path.GetFileName(path),
                MimeType = item.MimeType!,
                Base64 = Convert.ToBase64String(File.ReadAllBytes(path))
            });
        }

        return attachments;
    }
}
