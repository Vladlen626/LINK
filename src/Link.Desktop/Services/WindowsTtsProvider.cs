using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Link.Desktop.Services;

/// <summary>
/// The voices installed in Windows (classic SAPI and the newer OneCore ones): the last resort, nothing downloaded.
/// They render into memory, so Link's player plays them like any other voice and silences them the same way.
/// </summary>
public sealed class WindowsTtsProvider : ITextToSpeechProvider
{
    private const int SampleRate = 24000;
    private readonly Lock _lock = new();
    private SpeechSynthesizer? _synthesizer;
    private string? _defaultVoice;

    public string Name => "Windows voice";

    public string? Unavailable => null;

    public int Generation => 0;

    /// <summary>The voices Windows offers, for Settings.</summary>
    public static IReadOnlyList<SpeechVoice> InstalledVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => new SpeechVoice(voice.VoiceInfo.Name, voice.VoiceInfo.Culture.Name))
            .ToArray();
    }

    public void Prepare(SpeechOptions options)
    {
    }

    public async Task SynthesizeAsync(
        string text, SpeechOptions options, Action<PcmAudio> audio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new Prompt(text);
        SpeechSynthesizer synthesizer;
        lock (_lock)
        {
            synthesizer = Synthesizer;
            // SelectVoice picks the exact voice; a voice element inside the prompt may swap in a namesake.
            if (ChooseVoice(options) is { } voice) synthesizer.SelectVoice(voice);
            synthesizer.Rate = ToRate(options.Speed);
            synthesizer.SetOutputToAudioStream(
                new PcmStream(bytes => audio(new PcmAudio(bytes, SampleRate))),
                new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        }

        void OnCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            if (e.Prompt != prompt) return;
            // A silenced prompt reports an error too; stopping is a normal end here.
            if (e.Error is not null && !e.Cancelled) completion.TrySetException(e.Error);
            else completion.TrySetResult();
        }

        synthesizer.SpeakCompleted += OnCompleted;
        try
        {
            synthesizer.SpeakAsync(prompt);
            using (cancellationToken.Register(() => synthesizer.SpeakAsyncCancel(prompt)))
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            synthesizer.SpeakCompleted -= OnCompleted;
            lock (_lock) synthesizer.SetOutputToNull();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_synthesizer is null) return;
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Dispose();
            _synthesizer = null;
        }
    }

    // Created on first use: most sessions never speak, and SAPI loads its voices lazily too.
    private SpeechSynthesizer Synthesizer
    {
        get
        {
            if (_synthesizer is not null) return _synthesizer;
            _synthesizer = new SpeechSynthesizer();
            _defaultVoice = _synthesizer.Voice.Name;
            return _synthesizer;
        }
    }

    private string? ChooseVoice(SpeechOptions options)
    {
        var voices = Synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => voice.VoiceInfo)
            .ToArray();
        if (options.WindowsVoice is { } name && voices.Any(voice => voice.Name == name)) return name;

        // The voice chosen in Windows speech settings wins when it speaks the language.
        var speakers = voices
            .Where(voice => voice.Culture.Name.StartsWith(options.Language, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return speakers.FirstOrDefault(voice => voice.Name == _defaultVoice)?.Name
            ?? speakers.FirstOrDefault()?.Name
            ?? _defaultVoice;
    }

    // SAPI rates run from -10 (a third of the normal pace) through 0 to 10 (three times faster).
    private static int ToRate(double speed) =>
        (int)Math.Clamp(Math.Round(10 * Math.Log(speed) / Math.Log(3)), -10, 10);

    /// <summary>Where SAPI writes its PCM: each write goes straight on to the player.</summary>
    private sealed class PcmStream(Action<byte[]> written) : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;
            written(buffer.AsSpan(offset, count).ToArray());
            _length += count;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
