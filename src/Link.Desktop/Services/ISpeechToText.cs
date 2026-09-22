using Link.Desktop.Models;

namespace Link.Desktop.Services;

public interface ISpeechToText
{
    Task<string> TranscribeAsync(
        string waveFile,
        VoiceLanguage language,
        CancellationToken cancellationToken = default);
}
