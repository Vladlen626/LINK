namespace Link.Desktop.Services;

/// <summary>
/// The words being dictated, kept at the end of the prompt. Each recognition pass replaces them, so the prompt
/// never holds two versions of the same words; text typed before them stays as it was.
/// </summary>
public sealed class PromptDraft
{
    // What the draft put at the end of the prompt, without the space that joins it to the typed text.
    private string _words = string.Empty;

    /// <summary>The prompt with the draft's words replaced by <paramref name="words"/>.</summary>
    public string Show(string prompt, string words)
    {
        var typed = prompt;
        // The prompt may have lost its sent part meanwhile; the draft is still what it ends with.
        if (_words.Length > 0 && prompt.EndsWith(_words, StringComparison.Ordinal))
        {
            // The space that joined the draft goes with it.
            typed = prompt[..^_words.Length].TrimEnd();
        }

        _words = words;
        if (words.Length == 0) return typed;
        return string.IsNullOrWhiteSpace(typed) ? words : $"{typed.TrimEnd()} {words}";
    }

    /// <summary>The prompt with <paramref name="text"/> in place of the draft, which is then ordinary text.</summary>
    public string Commit(string prompt, string text)
    {
        var result = Show(prompt, text);
        _words = string.Empty;
        return result;
    }

    /// <summary>The draft stays in the prompt as ordinary text.</summary>
    public void Keep() => _words = string.Empty;
}
