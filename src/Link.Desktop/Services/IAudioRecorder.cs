namespace Link.Desktop.Services;

public interface IAudioRecorder : IDisposable
{
    /// <summary>Peak level of the latest captured block, 0..1. Raised on a background thread.</summary>
    event Action<double>? LevelChanged;

    /// <summary>
    /// The latest captured block as 16 kHz mono samples in -1..1, so recognition can listen along with the one
    /// microphone session. Raised on the capture thread; the array belongs to the handler.
    /// </summary>
    event Action<float[]>? SamplesCaptured;

    bool IsRecording { get; }

    /// <summary>
    /// Starts recording from the given endpoint (null = Windows default).
    /// Returns false when that microphone is not connected and the default one is used instead.
    /// </summary>
    bool Start(string? deviceId);

    /// <summary>Ends the recording; the audio before <paramref name="skip"/> is left out of it.</summary>
    AudioRecording Stop(bool save = true, TimeSpan skip = default);

    void Cancel();
}

public sealed record AudioRecording(string? FilePath, TimeSpan Duration, double PeakLevel)
{
    // About -40 dBFS: a muted, disconnected or privacy-blocked microphone stays below it.
    private const double SilencePeak = 0.01;

    public bool IsSilent => PeakLevel < SilencePeak;
}
