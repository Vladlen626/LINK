namespace Link.Desktop.Services;

public interface IAudioRecorder : IDisposable
{
    /// <summary>Peak level of the latest captured block, 0..1. Raised on a background thread.</summary>
    event Action<double>? LevelChanged;

    bool IsRecording { get; }

    /// <summary>
    /// Starts recording from the given endpoint (null = Windows default).
    /// Returns false when that microphone is not connected and the default one is used instead.
    /// </summary>
    bool Start(string? deviceId);

    AudioRecording Stop(bool save = true);
    void Cancel();
}

public sealed record AudioRecording(string? FilePath, TimeSpan Duration, double PeakLevel)
{
    // About -40 dBFS: a muted, disconnected or privacy-blocked microphone stays below it.
    private const double SilencePeak = 0.01;

    public bool IsSilent => PeakLevel < SilencePeak;
}
