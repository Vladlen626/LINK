namespace Link.Desktop.Services;

/// <summary>
/// Picks what to say of an answer while it streams: <see cref="Next"/> gives what became ready since the last call —
/// complete sentences only, and a very short opening ("Да.") waits for the sentence after it so the voice does not
/// start with a lone word — and <see cref="Finish"/> the rest once the answer is final. Together they say what the
/// final answer alone would, each sentence once. Past <see cref="MaxSpokenLength"/> characters the voice stops
/// with a pointer to the screen: a long analysis is read on screen, not for minutes.
/// </summary>
public sealed class VoiceTextChunker
{
    public const int MaxSpokenLength = 900;
    private const int MinOpeningLength = 24;
    private readonly List<SpeechUnit> _said = [];
    private readonly bool _russianQuestion;

    /// <param name="questionLanguage">"ru" or "en": the language of pointers when the answer has no words of its own.</param>
    public VoiceTextChunker(string questionLanguage = "en") => _russianQuestion = questionLanguage == "ru";

    /// <summary>Whether part of the answer has been handed out to be said.</summary>
    public bool HasStarted { get; private set; }

    /// <summary>What became ready to say in the streaming answer; null when nothing did.</summary>
    public SpokenAnswer? Next(string markdown) => Take(SpeechTextSanitizer.Read(markdown, final: false), final: false);

    /// <summary>The rest of the final answer; null when nothing is left to say.</summary>
    public SpokenAnswer? Finish(string markdown)
    {
        var rest = Take(SpeechTextSanitizer.Read(markdown, final: true), final: true);
        if (rest is not null || HasStarted) return rest;
        return _russianQuestion
            ? new SpokenAnswer("Ответ на экране.", "ru")
            : new SpokenAnswer("The answer is on screen.", "en");
    }

    private SpokenAnswer? Take(List<SpeechUnit> units, bool final)
    {
        // Judged on all the sentences so far, so a short English one does not switch a Russian answer's voice.
        var words = string.Join(' ', units.Select(unit => unit.Text));
        var russian = words.Any(char.IsLetter) ? SpeechTextSanitizer.SpeaksRussian(words) : _russianQuestion;
        var speech = WithinBudget(units, russian);

        // Said units were complete, so they do not change; different ones mean the chat swapped the text it
        // streams (e.g. a note that came before the answer), and the new text is read from its start.
        var common = Math.Min(speech.Count, _said.Count);
        if (!speech.Take(common).SequenceEqual(_said.Take(common))) _said.Clear();

        var fresh = speech.Skip(_said.Count).ToList();
        var text = string.Join(' ', fresh.Select(unit => unit.Note is { } note ? SpeechTextSanitizer.Say(note, russian) : unit.Text));
        if (text.Length == 0 || (!HasStarted && !final && text.Length < MinOpeningLength)) return null;

        _said.AddRange(fresh);
        HasStarted = true;
        return new SpokenAnswer(text, russian ? "ru" : "en");
    }

    /// <summary>The units that fit the spoken length, and a pointer to the screen where the rest was cut off.</summary>
    private static List<SpeechUnit> WithinBudget(List<SpeechUnit> units, bool russian)
    {
        var speech = new List<SpeechUnit>(units.Count);
        var length = 0;
        foreach (var unit in units)
        {
            var spoken = unit.Note is { } note ? SpeechTextSanitizer.Say(note, russian).Length : unit.Text.Length;
            if (speech.Count > 0 && length + spoken > MaxSpokenLength)
            {
                speech.Add(new SpeechUnit(string.Empty, SpeechNote.Rest));
                break;
            }

            speech.Add(unit);
            length += spoken + 1;
        }

        return speech;
    }
}
