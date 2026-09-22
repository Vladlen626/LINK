namespace Link.Desktop.Services;

public interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }
    void Start();
    string Stop();
    void Cancel();
}
