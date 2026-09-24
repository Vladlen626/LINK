namespace Link.Desktop.Services;

public interface ITextToSpeech : IDisposable
{
    /// <summary>Whether something is being said or waits to be said.</summary>
    bool IsSpeaking { get; }

    IReadOnlyList<SpeechVoice> GetVoices();

    /// <summary>
    /// Says the text and completes once it has been spoken or <see cref="Stop"/> silenced it.
    /// Cancelling the token silences only this text and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken);

    /// <summary>Silences everything at once.</summary>
    void Stop();
}

/// <summary>An installed voice; <paramref name="Language"/> is a culture name such as ru-RU.</summary>
public sealed record SpeechVoice(string Name, string Language);

/// <summary>
/// How to speak: a voice by name, or null for one that speaks <paramref name="Language"/> (a two-letter code),
/// and a speed where 1 is the voice's normal pace.
/// </summary>
public sealed record SpeechOptions(string? VoiceName, string Language, double Speed);
