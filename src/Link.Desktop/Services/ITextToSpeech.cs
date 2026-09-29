using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Link's voice. Parts are said in the order they are handed over, each one starting as soon as its first audio
/// exists; the next part is made while the one before it plays.
/// </summary>
public interface ITextToSpeech : IDisposable
{
    /// <summary>Whether something is being said or waits to be said.</summary>
    bool IsSpeaking { get; }

    /// <summary>
    /// Queues the text after everything handed over before and completes once it has been said. Cancelling the
    /// token, or <see cref="Stop"/>, silences it at once and throws <see cref="OperationCanceledException"/>.
    /// Throws when no voice could say it.
    /// </summary>
    Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken);

    /// <summary>An answer to read is on its way: the voice gets ready (a connection, a model in memory).</summary>
    void Prepare(SpeechOptions options);

    /// <summary>Silences everything at once.</summary>
    void Stop();
}

/// <summary>
/// How to speak: <see cref="Provider"/> goes first and each provider speaks with its own chosen voice;
/// <see cref="Language"/> is "ru" or "en" and a <see cref="Speed"/> of 1 is a voice's normal pace.
/// </summary>
public sealed record SpeechOptions(
    TtsProvider Provider,
    string GeminiVoice,
    int LocalVoice,
    string Language,
    double Speed)
{
    public static SpeechOptions From(AppSettings settings, string language) => new(
        settings.VoiceReplyProvider,
        settings.GeminiVoice,
        settings.LocalVoice,
        language,
        settings.VoiceReplySpeed);
}

/// <summary>One way to turn text into speech; <see cref="TextToSpeechService"/> plays what it makes.</summary>
public interface ITextToSpeechProvider : IDisposable
{
    /// <summary>What the voice is called in notices and the log.</summary>
    string Name { get; }

    /// <summary>Null when the provider can speak; otherwise why not, e.g. "no API key".</summary>
    string? Unavailable { get; }

    /// <summary>What the voice depends on changed (a new key, a model on disk): an earlier failure is forgotten.</summary>
    int Generation { get; }

    /// <summary>
    /// How much of its speech is held back before it starts to sound, for a voice whose audio can arrive slower than
    /// it plays; zero for one that is made faster than it plays.
    /// </summary>
    TimeSpan Prebuffer { get; }

    void Prepare(SpeechOptions options);

    /// <summary>Hands the speech of the text over block by block while it is made; completes after the last one.</summary>
    Task SynthesizeAsync(string text, SpeechOptions options, Action<PcmAudio> audio, CancellationToken cancellationToken);
}

/// <summary>16-bit little-endian mono PCM.</summary>
public sealed record PcmAudio(byte[] Data, int SampleRate);

/// <summary>
/// A provider failed. The message is short and shown to the user; <see cref="Detail"/> goes to the log only.
/// The provider is left alone for <see cref="Pause"/>, or until its <see cref="ITextToSpeechProvider.Generation"/>
/// changes when that is <see cref="Timeout.InfiniteTimeSpan"/>.
/// </summary>
public sealed class TtsProviderException(string message, string detail, TimeSpan pause, bool retry = false)
    : Exception(message)
{
    public string Detail { get; } = detail;
    public TimeSpan Pause { get; } = pause;

    /// <summary>A quick failure that one immediate second try may get past.</summary>
    public bool Retry { get; } = retry;
}

/// <summary>Plays speech: stretches of audio one after another, silenced at once by <see cref="Stop"/>.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Starts a stretch that plays after those begun before; its audio is written while it is made.</summary>
    IAudioSegment Begin();

    /// <summary>Silences the device and drops every stretch; their <see cref="IAudioSegment.Finished"/> is cancelled.</summary>
    void Stop();
}

public interface IAudioSegment
{
    /// <summary>Completes when the stretch starts to sound.</summary>
    Task Started { get; }

    /// <summary>Completes once it has been played to the end; cancelled when silenced.</summary>
    Task Finished { get; }

    /// <summary>
    /// How much audio the stretch holds back before it starts to sound, and again whenever the device has run dry,
    /// so that a stream slower than the speech pauses seldom instead of stuttering. The player keeps a small
    /// cushion of its own; this only asks for more.
    /// </summary>
    void SetPrebuffer(TimeSpan audio);

    void Write(PcmAudio audio);

    /// <summary>No more audio comes; a stretch completed without any finishes at once.</summary>
    void Complete();
}
