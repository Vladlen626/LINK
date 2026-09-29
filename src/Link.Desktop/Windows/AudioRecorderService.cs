using System.Runtime.InteropServices;
using System.Text;
using Link.Desktop.Services;

namespace Link.Desktop.Windows;

/// <summary>
/// Records 16 kHz mono PCM through waveIn, which lets Windows resample any microphone for Whisper.
/// The same capture session hands every block to live recognition, so the microphone is opened only once.
/// </summary>
public sealed class AudioRecorderService : IAudioRecorder
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;
    private const int KeptRecordings = 10;
    private readonly StoragePaths _paths;
    private CaptureSession? _session;

    public AudioRecorderService(StoragePaths paths) => _paths = paths;

    public event Action<double>? LevelChanged;

    public event Action<float[]>? SamplesCaptured;

    public bool IsRecording => _session is not null;

    public bool Start(string? deviceId)
    {
        if (_session is not null) throw new InvalidOperationException("The microphone is already recording.");

        var waveInId = NativeMethods.WaveMapper;
        var usedSelectedDevice = true;
        if (deviceId is not null)
        {
            waveInId = AudioInputDevices.FindWaveInDevice(deviceId);
            if (waveInId < 0)
            {
                waveInId = NativeMethods.WaveMapper;
                usedSelectedDevice = false;
            }
        }

        var session = new CaptureSession(waveInId, level => LevelChanged?.Invoke(level), PublishSamples);
        session.Start();
        _session = session;
        return usedSelectedDevice;
    }

    public AudioRecording Stop(bool save = true, TimeSpan skip = default)
    {
        var session = _session ?? throw new InvalidOperationException("Voice recording has not been started.");
        _session = null;
        var recorded = session.Stop();
        var start = Math.Clamp((long)(skip.TotalSeconds * SampleRate) * BytesPerSample, 0, recorded.Length);
        var samples = recorded.AsMemory((int)start);
        var duration = TimeSpan.FromSeconds(samples.Length / (double)(SampleRate * BytesPerSample));
        if (!save) return new AudioRecording(null, duration, session.PeakLevel);

        var path = Path.Combine(_paths.Recordings, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        WriteWave(path, samples.Span);
        PruneRecordings();
        return new AudioRecording(path, duration, session.PeakLevel);
    }

    public void Cancel()
    {
        var session = _session;
        _session = null;
        session?.Abort();
    }

    public void Dispose() => Cancel();

    private void PublishSamples(ReadOnlySpan<short> samples)
    {
        // Only a listener pays for the conversion.
        if (SamplesCaptured is not { } handler) return;
        var block = new float[samples.Length];
        for (var index = 0; index < samples.Length; index++) block[index] = samples[index] / 32768f;
        handler(block);
    }

    private static void WriteWave(string path, ReadOnlySpan<byte> samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)NativeMethods.WaveFormatPcm);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * BytesPerSample);
        writer.Write((short)BytesPerSample);
        writer.Write((short)(BytesPerSample * 8));
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
    }

    private void PruneRecordings()
    {
        foreach (var file in new DirectoryInfo(_paths.Recordings)
                     .EnumerateFiles("*.wav")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(KeptRecordings))
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

    /// <summary>
    /// One recording. A dedicated thread owns every waveIn call and the native buffers,
    /// so the UI thread only signals start/stop and never races the driver.
    /// </summary>
    private sealed class CaptureSession
    {
        private const int BufferCount = 8;
        private const int BufferBytes = SampleRate * BytesPerSample / 20;
        private const int MaxBytes = SampleRate * BytesPerSample * 60 * 5;
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
        private readonly int _deviceId;
        private readonly Action<double> _levelChanged;
        private readonly Action<ReadOnlySpan<short>> _samplesCaptured;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();
        private readonly ManualResetEvent _stopRequested = new(false);
        private readonly MemoryStream _samples = new();
        private readonly byte[] _scratch = new byte[BufferBytes];
        private Exception? _error;

        public CaptureSession(int deviceId, Action<double> levelChanged, Action<ReadOnlySpan<short>> samplesCaptured)
        {
            _deviceId = deviceId;
            _levelChanged = levelChanged;
            _samplesCaptured = samplesCaptured;
            _thread = new Thread(Run) { IsBackground = true, Name = "Link microphone" };
        }

        public double PeakLevel { get; private set; }

        public void Start()
        {
            _thread.Start();
            if (!_started.Wait(ResponseTimeout))
            {
                _stopRequested.Set();
                throw new InvalidOperationException("The microphone did not respond.");
            }

            if (_error is null) return;
            _thread.Join();
            ReleaseEvents();
            throw _error;
        }

        public byte[] Stop()
        {
            _stopRequested.Set();
            if (!_thread.Join(ResponseTimeout)) throw new InvalidOperationException("The microphone did not stop recording.");
            ReleaseEvents();
            if (_error is not null) throw _error;
            return _samples.ToArray();
        }

        public void Abort()
        {
            _stopRequested.Set();
            if (_thread.Join(ResponseTimeout)) ReleaseEvents();
        }

        private void ReleaseEvents()
        {
            _started.Dispose();
            _stopRequested.Dispose();
        }

        private void Run()
        {
            var handle = IntPtr.Zero;
            var headers = new IntPtr[BufferCount];
            var headerSize = Marshal.SizeOf<WaveHeader>();
            using var bufferDone = new AutoResetEvent(false);
            try
            {
                var format = new WaveFormat
                {
                    FormatTag = NativeMethods.WaveFormatPcm,
                    Channels = 1,
                    SamplesPerSecond = SampleRate,
                    AverageBytesPerSecond = SampleRate * BytesPerSample,
                    BlockAlign = BytesPerSample,
                    BitsPerSample = BytesPerSample * 8
                };
                // WAVE_MAPPED lets the mapper convert a specific device to the Whisper format.
                var flags = NativeMethods.CallbackEvent |
                            (_deviceId == NativeMethods.WaveMapper ? 0 : NativeMethods.WaveMapped);
                Check(NativeMethods.waveInOpen(
                    out handle,
                    _deviceId,
                    ref format,
                    bufferDone.SafeWaitHandle.DangerousGetHandle(),
                    IntPtr.Zero,
                    flags), "open the microphone");

                for (var index = 0; index < BufferCount; index++)
                {
                    var data = Marshal.AllocHGlobal(BufferBytes);
                    var header = Marshal.AllocHGlobal(headerSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = BufferBytes }, header, false);
                    headers[index] = header;
                    Check(NativeMethods.waveInPrepareHeader(handle, header, headerSize), "prepare the microphone");
                    Check(NativeMethods.waveInAddBuffer(handle, header, headerSize), "prepare the microphone");
                }

                Check(NativeMethods.waveInStart(handle), "start the microphone");
                _started.Set();

                var next = 0;
                var waits = new WaitHandle[] { bufferDone, _stopRequested };
                while (WaitHandle.WaitAny(waits) == 0)
                {
                    next = Collect(handle, headers, next, requeue: true);
                }

                // Reset returns the partially filled buffer, so the last words are kept.
                NativeMethods.waveInReset(handle);
                Collect(handle, headers, next, requeue: false);
            }
            catch (Exception exception)
            {
                _error = exception;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.waveInReset(handle);
                    foreach (var header in headers.Where(header => header != IntPtr.Zero))
                    {
                        NativeMethods.waveInUnprepareHeader(handle, header, headerSize);
                    }
                    NativeMethods.waveInClose(handle);
                }

                foreach (var header in headers.Where(header => header != IntPtr.Zero))
                {
                    Marshal.FreeHGlobal(Marshal.PtrToStructure<WaveHeader>(header).Data);
                    Marshal.FreeHGlobal(header);
                }

                _started.Set();
            }
        }

        private int Collect(IntPtr handle, IntPtr[] headers, int next, bool requeue)
        {
            var headerSize = Marshal.SizeOf<WaveHeader>();
            var flagsOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32();
            for (var checkedBuffers = 0; checkedBuffers < headers.Length; checkedBuffers++)
            {
                var pointer = headers[next];
                var header = Marshal.PtrToStructure<WaveHeader>(pointer);
                if ((header.Flags & NativeMethods.WaveHeaderDone) == 0) break;

                if (header.BytesRecorded > 0) Append(header.Data, Math.Min(header.BytesRecorded, BufferBytes));
                if (requeue)
                {
                    Marshal.WriteInt32(pointer, flagsOffset, header.Flags & ~NativeMethods.WaveHeaderDone);
                    Check(NativeMethods.waveInAddBuffer(handle, pointer, headerSize), "continue recording");
                }

                next = (next + 1) % headers.Length;
            }

            return next;
        }

        private void Append(IntPtr data, int bytes)
        {
            Marshal.Copy(data, _scratch, 0, bytes);
            var samples = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, bytes & ~1));
            var peak = 0;
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
            }

            var level = Math.Min(1.0, peak / 32768.0);
            PeakLevel = Math.Max(PeakLevel, level);
            // A stuck push-to-talk key must not grow memory without bound.
            if (_samples.Length + bytes <= MaxBytes) _samples.Write(_scratch, 0, bytes);
            _levelChanged(level);
            _samplesCaptured(samples);
        }

        private static void Check(int result, string action)
        {
            if (result == 0) return;
            var text = new StringBuilder(256);
            NativeMethods.waveInGetErrorText(result, text, text.Capacity);
            var details = text.ToString().Trim();
            throw new InvalidOperationException(details.Length > 0
                ? $"Could not {action}: {details}"
                : $"Could not {action} (error {result}).");
        }
    }
}
