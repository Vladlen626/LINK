using System.Runtime.InteropServices;
using System.Text;
using Link.Desktop.Services;

namespace Link.Desktop.Windows;

/// <summary>
/// Plays speech through waveOut, stretch after stretch in the order they began. A dedicated thread owns the device
/// and its buffers; Stop resets the device, which silences it at once. The device opens when there is something to
/// play and closes after a quiet moment, so an idle Link holds no audio endpoint.
/// </summary>
public sealed class AudioPlayer : IAudioOutput
{
    private const int BufferMilliseconds = 40;
    // Up to a quarter of a second queued in the device: enough to ride out a busy moment, quick to stop.
    private const int DeviceBuffers = 6;
    private const int BufferCapacity = 8192;
    // Speech that starts from silence waits for this much audio, so a slow stream does not stutter at once.
    private const int PrebufferMilliseconds = 120;
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(2);
    private readonly Lock _lock = new();
    private readonly Queue<Segment> _segments = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private bool _stopRequested;
    private bool _disposed;

    public IAudioSegment Begin()
    {
        var segment = new Segment(this);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _segments.Enqueue(segment);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Link speaker" };
                _thread.Start();
            }
        }

        _wake.Set();
        return segment;
    }

    public void Stop()
    {
        Segment[] dropped;
        lock (_lock)
        {
            dropped = [.. _segments];
            _segments.Clear();
            _stopRequested = true;
        }

        foreach (var segment in dropped) segment.Cancel();
        _wake.Set();
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }

        Stop();
        _wake.Set();
        // A thread stuck in the driver keeps its event; the process is ending anyway.
        if (thread is null || thread.Join(TimeSpan.FromSeconds(2))) _wake.Dispose();
    }

    /// <summary>Owns the device: writes queued audio into free buffers, collects played ones, finishes stretches.</summary>
    private void Run()
    {
        var headerSize = Marshal.SizeOf<WaveHeader>();
        var flagsOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32();
        var buffers = new DeviceBuffer[DeviceBuffers];
        for (var index = 0; index < buffers.Length; index++)
        {
            buffers[index] = new DeviceBuffer(Marshal.AllocHGlobal(headerSize), Marshal.AllocHGlobal(BufferCapacity));
        }

        var busy = new Queue<DeviceBuffer>();
        var free = new Stack<DeviceBuffer>(buffers);
        using var played = new AutoResetEvent(false);
        var device = IntPtr.Zero;
        var sampleRate = 0;
        var quietSince = Environment.TickCount64;
        var waits = new WaitHandle[] { played, _wake };

        void Collect(bool all)
        {
            while (busy.Count > 0 && (all || (Marshal.ReadInt32(busy.Peek().Header, flagsOffset) & NativeMethods.WaveHeaderDone) != 0))
            {
                var buffer = busy.Dequeue();
                NativeMethods.waveOutUnprepareHeader(device, buffer.Header, headerSize);
                buffer.Owner!.InDevice--;
                buffer.Owner = null;
                free.Push(buffer);
            }
        }

        void Close()
        {
            if (device == IntPtr.Zero) return;
            NativeMethods.waveOutReset(device);
            Collect(all: true);
            NativeMethods.waveOutClose(device);
            device = IntPtr.Zero;
            sampleRate = 0;
        }

        try
        {
            while (true)
            {
                bool stop, disposed;
                lock (_lock)
                {
                    stop = _stopRequested;
                    _stopRequested = false;
                    disposed = _disposed;
                }

                if (disposed) break;
                if (stop && device != IntPtr.Zero)
                {
                    // Reset hands every queued buffer back at once: this is what makes Stop immediate.
                    NativeMethods.waveOutReset(device);
                    Collect(all: true);
                }

                Collect(all: false);
                var finished = new List<Segment>();
                Segment? writing;
                lock (_lock)
                {
                    while (_segments.TryPeek(out var head) && head.IsWritten && head.InDevice == 0)
                    {
                        finished.Add(_segments.Dequeue());
                    }

                    writing = _segments.FirstOrDefault(segment => !segment.IsWritten);
                }

                foreach (var segment in finished) segment.Finish(null);

                if (writing is not null)
                {
                    try
                    {
                        Write(writing);
                    }
                    catch (Exception exception)
                    {
                        // No device can play: every stretch fails the same way, and the next one tries afresh.
                        Close();
                        Segment[] failed;
                        lock (_lock)
                        {
                            failed = [.. _segments];
                            _segments.Clear();
                        }

                        foreach (var segment in failed) segment.Finish(exception);
                    }
                }

                if (busy.Count > 0 || writing is not null) quietSince = Environment.TickCount64;
                else if (device != IntPtr.Zero && Environment.TickCount64 - quietSince > IdleClose.TotalMilliseconds) Close();

                WaitHandle.WaitAny(waits, busy.Count > 0 ? 20 : 500);
            }
        }
        finally
        {
            Close();
            foreach (var buffer in buffers)
            {
                Marshal.FreeHGlobal(buffer.Data);
                Marshal.FreeHGlobal(buffer.Header);
            }
        }

        void Write(Segment segment)
        {
            lock (_lock)
            {
                if (segment.SampleRate == 0 && !segment.IsComplete) return;
                // Starting from silence, a stretch that is still being made waits for a little cushion of audio.
                if (busy.Count == 0 && !segment.IsComplete
                    && segment.PendingBytes < segment.SampleRate * 2L * PrebufferMilliseconds / 1000)
                {
                    return;
                }
            }

            if (segment.SampleRate != 0 && segment.SampleRate != sampleRate)
            {
                // Another voice at another rate plays once the one before it has finished.
                if (busy.Count > 0) return;
                Close();
                device = Open(segment.SampleRate, played);
                sampleRate = segment.SampleRate;
            }

            var chunk = Math.Min(BufferCapacity, sampleRate * 2 * BufferMilliseconds / 1000) & ~1;
            while (free.Count > 0)
            {
                int length;
                var buffer = free.Peek();
                lock (_lock)
                {
                    // Stopped while being written: nothing more of it reaches the device.
                    if (segment.Finished.IsCompleted) return;
                    length = segment.Take(buffer.Data, chunk);
                    if (length == 0)
                    {
                        if (segment.IsComplete) segment.IsWritten = true;
                        return;
                    }
                }

                free.Pop();
                Marshal.StructureToPtr(new WaveHeader { Data = buffer.Data, BufferLength = length }, buffer.Header, false);
                Check(NativeMethods.waveOutPrepareHeader(device, buffer.Header, headerSize), "prepare the speakers");
                buffer.Owner = segment;
                segment.InDevice++;
                busy.Enqueue(buffer);
                Check(NativeMethods.waveOutWrite(device, buffer.Header, headerSize), "play audio");
                segment.MarkStarted();
            }
        }
    }

    private static IntPtr Open(int sampleRate, AutoResetEvent played)
    {
        var format = new WaveFormat
        {
            FormatTag = NativeMethods.WaveFormatPcm,
            Channels = 1,
            SamplesPerSecond = sampleRate,
            AverageBytesPerSecond = sampleRate * 2,
            BlockAlign = 2,
            BitsPerSample = 16
        };
        Check(NativeMethods.waveOutOpen(
            out var device,
            NativeMethods.WaveMapper,
            ref format,
            played.SafeWaitHandle.DangerousGetHandle(),
            IntPtr.Zero,
            NativeMethods.CallbackEvent), "open the speakers");
        return device;
    }

    private static void Check(int result, string action)
    {
        if (result == 0) return;
        var text = new StringBuilder(256);
        NativeMethods.waveOutGetErrorText(result, text, text.Capacity);
        var details = text.ToString().Trim();
        throw new InvalidOperationException(details.Length > 0
            ? $"Could not {action}: {details}"
            : $"Could not {action} (error {result}).");
    }

    private sealed class DeviceBuffer(IntPtr header, IntPtr data)
    {
        public IntPtr Header { get; } = header;
        public IntPtr Data { get; } = data;
        public Segment? Owner { get; set; }
    }

    /// <summary>One stretch of speech. Its audio fields are guarded by the player's lock.</summary>
    private sealed class Segment(AudioPlayer player) : IAudioSegment
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        private byte? _carry;

        public Task Started => _started.Task;
        public Task Finished => _finished.Task;
        public int SampleRate { get; private set; }
        public long PendingBytes { get; private set; }
        public bool IsComplete { get; private set; }

        /// <summary>All of its audio is in the device (or it had none).</summary>
        public bool IsWritten { get; set; }

        /// <summary>Device buffers that still hold its audio; touched by the player thread only.</summary>
        public int InDevice { get; set; }

        public void Write(PcmAudio audio)
        {
            if (audio.Data.Length == 0) return;
            lock (player._lock)
            {
                if (IsComplete || _finished.Task.IsCompleted) return;
                if (SampleRate == 0) SampleRate = audio.SampleRate;
                var data = audio.Data;
                if (_carry is { } first) data = [first, .. data];
                // Whole samples only; a split one waits for its second byte.
                _carry = data.Length % 2 == 0 ? null : data[^1];
                if (_carry is not null) data = data[..^1];
                if (data.Length == 0) return;
                _pending.Enqueue(data);
                PendingBytes += data.Length;
            }

            player._wake.Set();
        }

        public void Complete()
        {
            lock (player._lock) IsComplete = true;
            player._wake.Set();
        }

        /// <summary>Copies up to <paramref name="capacity"/> queued bytes into a device buffer.</summary>
        public int Take(IntPtr destination, int capacity)
        {
            var copied = 0;
            while (copied < capacity && _pending.TryPeek(out var block))
            {
                var length = Math.Min(capacity - copied, block.Length - _offset);
                Marshal.Copy(block, _offset, destination + copied, length);
                copied += length;
                _offset += length;
                if (_offset < block.Length) continue;
                _pending.Dequeue();
                _offset = 0;
            }

            PendingBytes -= copied;
            return copied;
        }

        public void MarkStarted() => _started.TrySetResult();

        public void Finish(Exception? error)
        {
            // A stretch without audio never sounded; whoever waits for its start is released too.
            if (error is null)
            {
                _started.TrySetResult();
                _finished.TrySetResult();
            }
            else
            {
                _started.TrySetException(error);
                _finished.TrySetException(error);
            }
        }

        public void Cancel()
        {
            _started.TrySetCanceled();
            _finished.TrySetCanceled();
        }
    }
}
