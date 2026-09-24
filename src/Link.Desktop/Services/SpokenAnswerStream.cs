namespace Link.Desktop.Services;

/// <summary>
/// Picks what to say of an answer while it streams: <see cref="Next"/> gives the sentences completed since the last
/// call, never the one still being written, and <see cref="Finish"/> what is left once the answer is final.
/// Together they say what the final answer alone would, each sentence once.
/// </summary>
public sealed class SpokenAnswerStream
{
    private readonly List<string> _said = [];

    /// <summary>Whether part of the answer has been handed out to be said.</summary>
    public bool HasStarted { get; private set; }

    /// <summary>Sentences of the streaming answer completed since the last call; null when there are none.</summary>
    public SpokenAnswer? Next(string markdown) =>
        Take(SpokenAnswer.ReadOpening(markdown, final: false).Sentences, pointToScreen: false);

    /// <summary>The rest of the final answer, the pointer to the screen included; null when nothing is left to say.</summary>
    public SpokenAnswer? Finish(string markdown)
    {
        var (sentences, goesOn) = SpokenAnswer.ReadOpening(markdown, final: true);
        if (sentences.Count > 0 || HasStarted) return Take(sentences, goesOn);
        return SpokenAnswer.IsRussian(markdown)
            ? new SpokenAnswer("Ответ на экране.", "ru")
            : new SpokenAnswer("The answer is on screen.", "en");
    }

    private SpokenAnswer? Take(List<string> sentences, bool pointToScreen)
    {
        // Said sentences were complete, so they do not change; different ones mean the chat swapped the text it
        // streams (e.g. a note that came before the answer), and the new text is read from its start.
        var common = Math.Min(sentences.Count, _said.Count);
        if (!sentences.Take(common).SequenceEqual(_said.Take(common))) _said.Clear();

        var fresh = sentences.Skip(_said.Count).ToList();
        _said.AddRange(fresh);
        // Judged on all that is said so far, so a short English sentence does not switch a Russian answer's voice.
        var russian = SpokenAnswer.IsRussian(string.Join(' ', _said));
        var text = string.Join(' ', fresh);
        if (pointToScreen) text = $"{text} {(russian ? "Подробности на экране." : "Details are on screen.")}".TrimStart();
        if (text.Length == 0) return null;
        HasStarted = true;
        return new SpokenAnswer(text, russian ? "ru" : "en");
    }
}
