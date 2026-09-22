using System.Text;
using Link.Desktop.Services;

namespace Link.Desktop.Windows;

public sealed class AudioRecorderService : IDisposable
{
    private const string Alias = "LinkVoiceCapture";
    private readonly StoragePaths _paths;
    private string? _pendingPath;

    public AudioRecorderService(StoragePaths paths) => _paths = paths;

    public bool IsRecording { get; private set; }

    public void Start()
    {
        if (IsRecording)
        {
            return;
        }

        _pendingPath = Path.Combine(_paths.Recordings, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        Send($"close {Alias}", ignoreError: true);
        Send($"open new type waveaudio alias {Alias}");

        try
        {
            Send($"set {Alias} time format milliseconds bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2");
            Send($"record {Alias}");
            IsRecording = true;
        }
        catch
        {
            Send($"close {Alias}", ignoreError: true);
            _pendingPath = null;
            throw;
        }
    }

    public string Stop()
    {
        if (!IsRecording || _pendingPath is null)
        {
            throw new InvalidOperationException("Voice recording has not been started.");
        }

        var path = _pendingPath;
        try
        {
            Send($"stop {Alias}");
            Send($"save {Alias} \"{path}\"");
            return path;
        }
        finally
        {
            Send($"close {Alias}", ignoreError: true);
            IsRecording = false;
            _pendingPath = null;
            PruneRecordings();
        }
    }

    public void Cancel()
    {
        Send($"stop {Alias}", ignoreError: true);
        Send($"close {Alias}", ignoreError: true);
        IsRecording = false;
        _pendingPath = null;
    }

    public void Dispose()
    {
        Cancel();
        GC.SuppressFinalize(this);
    }

    private static void Send(string command, bool ignoreError = false)
    {
        var error = NativeMethods.mciSendString(command, null, 0, IntPtr.Zero);
        if (error == 0 || ignoreError)
        {
            return;
        }

        var text = new StringBuilder(256);
        NativeMethods.mciGetErrorString(error, text, text.Capacity);
        throw new InvalidOperationException($"Windows audio capture failed: {text}");
    }

    private void PruneRecordings()
    {
        foreach (var file in new DirectoryInfo(_paths.Recordings)
                     .EnumerateFiles("*.wav")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(10))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Best-effort local history cleanup.
            }
        }
    }
}
