using Link.Desktop.Models;

namespace Link.Desktop.Services;

public interface ISpeechToText
{
    bool IsModelReady { get; }

    /// <summary>What the final pass is waiting on for a better model, e.g. its download; null when nothing.</summary>
    string? Note { get; }

    Task<string> TranscribeAsync(
        string waveFile,
        VoiceLanguage language,
        CancellationToken cancellationToken = default);
}
