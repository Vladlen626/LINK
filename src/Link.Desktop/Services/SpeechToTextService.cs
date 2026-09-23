using System.Text;
using Link.Desktop.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace Link.Desktop.Services;

public sealed class SpeechToTextService : ISpeechToText
{
    private readonly string _modelPath;
    private readonly SemaphoreSlim _modelDownload = new(1, 1);

    public SpeechToTextService(StoragePaths paths) =>
        _modelPath = Path.Combine(paths.Models, "ggml-base.bin");

    public bool IsModelReady => File.Exists(_modelPath) && new FileInfo(_modelPath).Length > 1_000_000;

    public async Task<string> TranscribeAsync(
        string waveFile,
        VoiceLanguage language,
        CancellationToken cancellationToken = default)
    {
        await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => TranscribeLocalAsync(waveFile, language, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        if (IsModelReady) return;

        await _modelDownload.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsModelReady) return;
            var temporaryPath = _modelPath + ".download";
            try
            {
                using var model = await WhisperGgmlDownloader.Default
                    .GetGgmlModelAsync(GgmlType.Base).ConfigureAwait(false);
                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await model.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryPath, _modelPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The local speech model could not be prepared. Check the internet connection and free disk space, then try Voice again.",
                exception);
        }
        finally
        {
            _modelDownload.Release();
        }
    }

    private async Task<string> TranscribeLocalAsync(
        string waveFile,
        VoiceLanguage language,
        CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(_modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(language switch
            {
                VoiceLanguage.Russian => "ru",
                VoiceLanguage.English => "en",
                _ => "auto"
            })
            .Build();
        await using var audio = File.OpenRead(waveFile);
        var transcript = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            transcript.Append(segment.Text);
        }

        var result = transcript.ToString().Trim();
        return result.Length > 0
            ? result
            : throw new InvalidOperationException("Speech was not recognized. Try recording again.");
    }
}
