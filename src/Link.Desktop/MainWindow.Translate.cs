using System.Text.RegularExpressions;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// A task in another language goes to the agent in English: the agent works with fewer tokens in it, and ChatGPT, which
/// costs no agent tokens, translates it in a chat of its own.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(90);

    private const string TranslatePrompt =
        "Translate the task below into English for a coding agent. Keep code, file paths, names, numbers, commands and " +
        "quoted text exactly as they are; keep the meaning, every detail and the line breaks; add nothing. Reply with the " +
        "translated task only: no preface, no notes, no quotes around it.\n\n";

    /// <summary>
    /// <paramref name="text"/> in English, translated in a new ChatGPT chat; the text as it is when it has no letters of
    /// another script, while the user's own message is under way, or when ChatGPT does not answer in time.
    /// </summary>
    private async Task<string> InEnglishAsync(string text, LinkProject project)
    {
        if (!OtherScript().IsMatch(text) || _sending) return text;
        using var timeout = new CancellationTokenSource(TranslationTimeout);
        try
        {
            SetStatus("ChatGPT translates the task into English…");
            var request = new PromptRequest
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                Route = RequestRoute.Direct,
                Effort = ResponseEffort.Fast,
                Text = TranslatePrompt + text.Trim()
            };
            var response = await _chat.SendAsync(request, _chat.NewChat(null), null, timeout.Token);
            var english = response.Text.Trim();
            // An answer that is empty or still in the other script is no translation.
            return english.Length == 0 || OtherScript().Matches(english).Count * 4 > english.Length ? text : english;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetStatus($"ChatGPT did not translate the task ({exception.Message}): it goes as written");
            return text;
        }
    }

    // Letters of the scripts a user here writes in besides Latin.
    [GeneratedRegex(@"[\p{IsCyrillic}]")]
    private static partial Regex OtherScript();
}
