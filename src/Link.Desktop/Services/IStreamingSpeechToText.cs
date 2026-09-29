using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Local recognition that listens along while the microphone is open: speech detection, and optionally a live
/// draft of the words. The draft is only a preview; the final text still comes from <see cref="ISpeechToText"/>.
/// </summary>
public interface IStreamingSpeechToText
{
    /// <summary>
    /// Starts one utterance; <paramref name="draft"/> asks for the live words too, and <paramref name="handsFree"/>
    /// for the pause that ends a phrase. Models that are not on disk are fetched in the background meanwhile, and
    /// this utterance goes without them.
    /// </summary>
    IStreamingTranscription Start(VoiceLanguage language, bool draft, bool handsFree);
}

/// <summary>One utterance: samples come in on the capture thread, the draft goes out on a worker thread.</summary>
public interface IStreamingTranscription : IDisposable
{
    /// <summary>The words heard so far, raised whenever they change.</summary>
    event Action<string>? DraftChanged;

    /// <summary>
    /// Hands-free: the speaker paused after a phrase. Raised once, on a worker thread, with where the phrase began
    /// from the start of the utterance.
    /// </summary>
    event Action<TimeSpan>? PhraseEnded;

    /// <summary>
    /// Why something asked for is missing this time, e.g. the live draft or the pause detection while its model
    /// downloads; otherwise null.
    /// </summary>
    string? Note { get; }

    /// <summary>Whether speech has begun in this utterance.</summary>
    bool HeardSpeech { get; }

    /// <summary>Takes the next block of 16 kHz mono samples in -1..1 and returns at once.</summary>
    void Accept(float[] samples);

    /// <summary>
    /// Every sample is in. True when speech was heard, false when there was none, null when that is unknown
    /// because no detector ran.
    /// </summary>
    Task<bool?> FinishAsync();
}
