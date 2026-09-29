using SherpaOnnx;

namespace Link.Desktop.Services;

/// <summary>
/// The free local voice: Supertonic 3 on sherpa-onnx, on the CPU, offline. Its model is downloaded only on request.
/// Loaded it holds ~300 MB, so it loads when an answer is coming and unloads after a few quiet minutes.
/// </summary>
public sealed class LocalTtsProvider : ITextToSpeechProvider
{
    public const int Speakers = 10;
    // More flow steps sound cleaner but cost time; six keep a sentence at a fifth of its spoken length.
    private const int Steps = 6;
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(3);
    private readonly SpeechModelManager _models;
    private readonly Lock _lock = new();
    private readonly Timer _unload;
    private Task<OfflineTts>? _engine;
    private int _users;

    public LocalTtsProvider(SpeechModelManager models)
    {
        _models = models;
        _unload = new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Name => "local voice";

    public string? Unavailable => IsDownloaded ? null : "not downloaded";

    public int Generation => IsDownloaded ? 1 : 0;

    public TimeSpan Prebuffer => TimeSpan.Zero;

    public bool IsDownloaded => _models.IsReady(SpeechModel.Supertonic);

    /// <summary>
    /// Supertonic ships five female and five male styles, in that order (their pitch was measured: the first five
    /// speak at 165–225 Hz, the rest at 85–155 Hz).
    /// </summary>
    public static string VoiceName(int speaker) => speaker < 5 ? $"Female {speaker + 1}" : $"Male {speaker - 4}";

    public void Prepare(SpeechOptions options)
    {
        if (!IsDownloaded) return;
        lock (_lock)
        {
            _ = Load();
            _unload.Change(IdleUnload, Timeout.InfiniteTimeSpan);
        }
    }

    public async Task SynthesizeAsync(
        string text, SpeechOptions options, Action<PcmAudio> audio, CancellationToken cancellationToken)
    {
        Task<OfflineTts> engine;
        lock (_lock)
        {
            engine = Load();
            _users++;
            _unload.Change(Timeout.Infinite, Timeout.Infinite);
        }

        try
        {
            OfflineTts tts;
            try
            {
                tts = await engine.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new TtsProviderException("could not start", exception.Message, Timeout.InfiniteTimeSpan);
            }

            // The model renders a whole text at once, so sentence by sentence the first one sounds sooner.
            await Task.Run(() =>
            {
                foreach (var sentence in SpeechTextSanitizer.SplitSentences(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var config = new OfflineTtsGenerationConfig
                    {
                        Sid = Math.Clamp(options.LocalVoice, 0, Math.Max(tts.NumSpeakers, 1) - 1),
                        NumSteps = Steps,
                        Speed = (float)options.Speed
                    };
                    config.Extra["lang"] = options.Language;
                    var generated = tts.GenerateWithConfig(sentence, config,
                        (_, _, _, _) => cancellationToken.IsCancellationRequested ? 0 : 1);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        audio(new PcmAudio(ToPcm(generated.Samples), generated.SampleRate));
                    }
                    finally
                    {
                        // Native audio that is not IDisposable, though it has Dispose.
                        generated.Dispose();
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (--_users == 0) _unload.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        _unload.Dispose();
        Unload();
    }

    private Task<OfflineTts> Load()
    {
        if (_engine is { IsFaulted: false } engine) return engine;
        var model = SpeechModel.Supertonic;
        return _engine = Task.Run(() =>
        {
            var config = new OfflineTtsConfig();
            config.Model.Supertonic.DurationPredictor = _models.PathOf(model, "duration_predictor.int8.onnx");
            config.Model.Supertonic.TextEncoder = _models.PathOf(model, "text_encoder.int8.onnx");
            config.Model.Supertonic.VectorEstimator = _models.PathOf(model, "vector_estimator.int8.onnx");
            config.Model.Supertonic.Vocoder = _models.PathOf(model, "vocoder.int8.onnx");
            config.Model.Supertonic.TtsJson = _models.PathOf(model, "tts.json");
            config.Model.Supertonic.UnicodeIndexer = _models.PathOf(model, "unicode_indexer.bin");
            config.Model.Supertonic.VoiceStyle = _models.PathOf(model, "voice.bin");
            config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
            config.Model.Provider = "cpu";
            return new OfflineTts(config);
        });
    }

    private void Unload()
    {
        Task<OfflineTts>? engine;
        lock (_lock)
        {
            if (_users > 0) return;
            engine = _engine;
            _engine = null;
        }

        if (engine is { IsCompletedSuccessfully: true }) engine.Result.Dispose();
        // One still loading is let go once it is ready.
        else engine?.ContinueWith(task => task.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private static byte[] ToPcm(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var value = (short)Math.Clamp(samples[index] * 32767f, short.MinValue, short.MaxValue);
            pcm[2 * index] = (byte)value;
            pcm[2 * index + 1] = (byte)(value >> 8);
        }

        return pcm;
    }
}
