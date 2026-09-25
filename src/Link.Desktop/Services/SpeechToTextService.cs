using System.Text;
using Link.Desktop.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Link.Desktop.Services;

/// <summary>
/// Whisper for the final text of an utterance. Where Whisper runs on a GPU, large-v3-turbo takes about a quarter
/// second a phrase and is far more accurate; it is fetched and warmed up in the background after the first dictation.
/// Until then, and on machines without such a GPU, Base does the job on the CPU.
/// </summary>
public sealed class SpeechToTextService : ISpeechToText
{
    private const int SampleRate = 16000;
    // whisper.cpp takes four threads by default; up to eight end the CPU pass about a third sooner on a desktop.
    private static readonly int Threads = Math.Clamp(Environment.ProcessorCount * 2 / 3, 4, 8);
    private readonly SpeechModelManager _models;
    private readonly object _gate = new();
    private Task? _turboPreparation;
    private bool _turboReady;
    private bool _turboUnavailable;

    public SpeechToTextService(SpeechModelManager models) => _models = models;

    public bool IsModelReady => TurboReady || _models.IsReady(SpeechModel.WhisperBase);

    public string? Note
    {
        get
        {
            lock (_gate)
            {
                // Only turbo on its way is worth a word; Base alone needs none.
                if (_turboReady || _turboUnavailable || _turboPreparation is null) return null;
                return _models.Status(SpeechModel.WhisperTurbo) is { } status
                    ? $"Whisper turbo {status}"
                    : "Whisper turbo is warming up";
            }
        }
    }

    private bool TurboReady
    {
        get
        {
            lock (_gate) return _turboReady;
        }
    }

    public async Task<string> TranscribeAsync(
        string waveFile,
        VoiceLanguage language,
        CancellationToken cancellationToken = default)
    {
        string? text = null;
        if (TurboReady)
        {
            try
            {
                text = await Task.Run(
                    () => TranscribeLocalAsync(waveFile, language, onGpu: true, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A GPU that stopped working hands the job back to Base for the rest of the session.
                lock (_gate)
                {
                    _turboReady = false;
                    _turboUnavailable = true;
                }
            }
        }

        if (text is null)
        {
            await EnsureBaseAsync(cancellationToken).ConfigureAwait(false);
            text = await Task.Run(
                () => TranscribeLocalAsync(waveFile, language, onGpu: false, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            PrepareTurbo();
        }

        return text.Length > 0 ? text : throw new InvalidOperationException("Speech was not recognized. Try recording again.");
    }

    private async Task EnsureBaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _models.EnsureAsync(SpeechModel.WhisperBase).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The local speech model could not be prepared. Check the internet connection and free disk space, then try Voice again.",
                exception);
        }
    }

    /// <summary>
    /// Once Whisper has loaded its Vulkan build, the GPU model is fetched and warmed up in the background.
    /// A failed download is tried again after the next dictation; a machine where it does not run on a GPU keeps Base.
    /// </summary>
    private void PrepareTurbo()
    {
        lock (_gate)
        {
            if (_turboReady || _turboUnavailable || _turboPreparation is { IsCompleted: false }) return;
            if (RuntimeOptions.LoadedLibrary != RuntimeLibrary.Vulkan) return;
            _turboPreparation = Task.Run(WarmUpTurboAsync);
        }
    }

    private async Task WarmUpTurboAsync()
    {
        try
        {
            await _models.EnsureAsync(SpeechModel.WhisperTurbo).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The status line tells why; the next dictation tries again.
            return;
        }

        var onGpu = false;
        try
        {
            // whisper.cpp names the backend it runs on; the Vulkan build falls back to the CPU without a device.
            using (LogProvider.AddLogger((_, message) =>
                   {
                       if (message?.Contains("whisper_backend_init_gpu: using", StringComparison.Ordinal) == true) onGpu = true;
                   }))
            {
                using var factory = WhisperFactory.FromPath(_models.PathOf(SpeechModel.WhisperTurbo));
                using var processor = factory.CreateBuilder().WithLanguage("en").Build();
                // The first run compiles the GPU's shaders, about ten seconds once; the driver keeps them afterwards.
                await foreach (var _ in processor.ProcessAsync(new float[SampleRate]).ConfigureAwait(false))
                {
                }
            }
        }
        catch (Exception)
        {
            // The download is intact, so a model that does not load or run here is not tried again.
            onGpu = false;
        }

        lock (_gate)
        {
            _turboReady = onGpu;
            _turboUnavailable = !onGpu;
        }
    }

    private async Task<string> TranscribeLocalAsync(
        string waveFile,
        VoiceLanguage language,
        bool onGpu,
        CancellationToken cancellationToken)
    {
        using var factory = onGpu
            ? WhisperFactory.FromPath(_models.PathOf(SpeechModel.WhisperTurbo))
            // Base stays on the CPU: it is quick there and never waits for the GPU's first shader compile.
            : WhisperFactory.FromPath(_models.PathOf(SpeechModel.WhisperBase), new WhisperFactoryOptions { UseGpu = false });
        using var processor = factory.CreateBuilder()
            .WithLanguage(language switch
            {
                VoiceLanguage.Russian => "ru",
                VoiceLanguage.English => "en",
                _ => "auto"
            })
            .WithThreads(Threads)
            .Build();
        await using var audio = File.OpenRead(waveFile);
        var transcript = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            transcript.Append(segment.Text);
        }

        return transcript.ToString().Trim();
    }
}
