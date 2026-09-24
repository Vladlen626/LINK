using System.Speech.Synthesis;

namespace Link.Desktop.Services;

/// <summary>Speaks with the voices installed in Windows (classic SAPI and the newer OneCore ones); nothing is downloaded.</summary>
public sealed class TextToSpeechService : ITextToSpeech
{
    private SpeechSynthesizer? _synthesizer;
    private string? _defaultVoice;
    private int _pending;

    public bool IsSpeaking => Volatile.Read(ref _pending) > 0;

    public IReadOnlyList<SpeechVoice> GetVoices() =>
        Synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => new SpeechVoice(voice.VoiceInfo.Name, voice.VoiceInfo.Culture.Name))
            .ToArray();

    public async Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var synthesizer = Synthesizer;
        // SelectVoice picks the exact voice; a voice element inside the prompt may swap in a namesake.
        if (ChooseVoice(options) is { } voice) synthesizer.SelectVoice(voice);
        synthesizer.Rate = ToRate(options.Speed);

        var prompt = new Prompt(text);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, SpeakCompletedEventArgs e)
        {
            if (e.Prompt != prompt) return;
            // A silenced prompt reports an error too; stopping is a normal end here.
            if (e.Error is not null && !e.Cancelled) completion.TrySetException(e.Error);
            else completion.TrySetResult();
        }

        synthesizer.SpeakCompleted += OnCompleted;
        Interlocked.Increment(ref _pending);
        try
        {
            synthesizer.SpeakAsync(prompt);
            using (cancellationToken.Register(() => synthesizer.SpeakAsyncCancel(prompt)))
            {
                await completion.Task;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
            synthesizer.SpeakCompleted -= OnCompleted;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Stop() => _synthesizer?.SpeakAsyncCancelAll();

    public void Dispose()
    {
        if (_synthesizer is null) return;
        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.Dispose();
        _synthesizer = null;
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
        var voices = GetVoices();
        if (options.VoiceName is { } name && voices.Any(voice => voice.Name == name)) return name;

        // The voice chosen in Windows speech settings wins when it speaks the language.
        var speakers = voices
            .Where(voice => voice.Language.StartsWith(options.Language, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return speakers.FirstOrDefault(voice => voice.Name == _defaultVoice)?.Name
            ?? speakers.FirstOrDefault()?.Name
            ?? _defaultVoice;
    }

    // SAPI rates run from -10 (a third of the normal pace) through 0 to 10 (three times faster).
    private static int ToRate(double speed) =>
        (int)Math.Clamp(Math.Round(10 * Math.Log(speed) / Math.Log(3)), -10, 10);
}
