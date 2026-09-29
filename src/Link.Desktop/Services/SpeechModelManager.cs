using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Link.Desktop.Services;

/// <summary>A local speech model: files fetched once into Models\<see cref="Folder"/>.</summary>
public sealed record SpeechModel(string Folder, IReadOnlyList<SpeechModelFile> Files)
{
    private const string WhisperRepository =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private const string SherpaReleases = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";
    private const string RussianRepository =
        "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16/resolve/31fa603e4f31279c6e1f7600fed13dc4312663ab/";
    private const string EnglishRepository =
        "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-20M-2023-02-17/resolve/d42f2d9f7ca24806fb667456a18a9f1b60f70d16/";
    private const string SupertonicRepository =
        "https://huggingface.co/csukuangfj2/sherpa-onnx-supertonic-3-tts-int8-2026-05-11/resolve/cca5a0e6c96e1d2c720986bf7e75fcc81dee3ae4/";

    /// <summary>Whisper Base (MIT), the final pass on the CPU. It predates model folders and stays in Models itself.</summary>
    public static SpeechModel WhisperBase { get; } = new("",
    [
        new("ggml-base.bin", WhisperRepository + "ggml-base.bin", 147_951_465,
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe")
    ]);

    /// <summary>Whisper large-v3-turbo (MIT), 5-bit: the final pass on a GPU, too slow for the CPU.</summary>
    public static SpeechModel WhisperTurbo { get; } = new("",
    [
        new("ggml-large-v3-turbo-q5_0.bin", WhisperRepository + "ggml-large-v3-turbo-q5_0.bin", 574_041_195,
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")
    ]);

    /// <summary>Silero VAD v5 (MIT). The v4 model that sherpa-onnx names silero_vad.onnx missed whole Russian phrases.</summary>
    public static SpeechModel SileroVad { get; } = new("silero-vad-v5",
    [
        new("silero_vad.onnx", SherpaReleases + "silero_vad_v5.onnx", 2_313_101,
            "6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396")
    ]);

    /// <summary>Alpha Cephei's small streaming Zipformer for Russian (Apache-2.0), int8.</summary>
    public static SpeechModel StreamingRussian { get; } = new("streaming-ru-vosk-small-2025-08-16",
    [
        new("encoder.onnx", RussianRepository + "encoder.int8.onnx", 26_214_060,
            "e0db705e94ec35d803b1df4f40cda23d064e1142977c80ab288430b109777a9d"),
        new("decoder.onnx", RussianRepository + "decoder.onnx", 2_093_080,
            "89b3088a9e20e1ef7f2e85ce1a3478afe6a9c4ac57369cabcc4beb8e95328ea0"),
        new("joiner.onnx", RussianRepository + "joiner.int8.onnx", 259_417,
            "b55784b071ab7512eab4c7c44e4f5478284ef33c83562cc6a249b972515a31e5"),
        new("tokens.txt", RussianRepository + "tokens.txt", 6_388,
            "93bbbc0bae6b78c0bbb743d4aa9fded3bb5ff3aac5f0200e3a769a5a05e0fdf6")
    ]);

    /// <summary>The icefall 20M streaming Zipformer for English (Apache-2.0), int8.</summary>
    public static SpeechModel StreamingEnglish { get; } = new("streaming-en-20m-2023-02-17",
    [
        new("encoder.onnx", EnglishRepository + "encoder-epoch-99-avg-1.int8.onnx", 42_845_182,
            "3810755ce7c3ab26b42a8bcf39d191308fa27fb0f53358823ba46141d03b7eb3"),
        new("decoder.onnx", EnglishRepository + "decoder-epoch-99-avg-1.int8.onnx", 539_499,
            "21e2a2acd961b3ac72f55be2f10f1a285e1b0b0ba010d7c0b6eab141411b163c"),
        new("joiner.onnx", EnglishRepository + "joiner-epoch-99-avg-1.int8.onnx", 259_572,
            "e085d73b593cf9b0707f370dbd656d58327d3fe36d80d849202ef81df02cb01e"),
        new("tokens.txt", EnglishRepository + "tokens.txt", 5_048,
            "49e3c2646595fd907228b3c6787069658f67b17377c60aeb8619c4551b2316fb")
    ]);

    /// <summary>
    /// Supertone's Supertonic 3 (model OpenRAIL-M, code MIT), int8 for sherpa-onnx: the local voice, 31 languages
    /// with Russian and English, ten speakers. Fetched only when the user asks for it.
    /// </summary>
    public static SpeechModel Supertonic { get; } = new("supertonic-3-int8-2026-05-11",
    [
        new("duration_predictor.int8.onnx", SupertonicRepository + "duration_predictor.int8.onnx", 3_700_147,
            "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        new("text_encoder.int8.onnx", SupertonicRepository + "text_encoder.int8.onnx", 36_416_150,
            "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        new("vector_estimator.int8.onnx", SupertonicRepository + "vector_estimator.int8.onnx", 78_400_833,
            "20cd86fa5c6effedfda0e7cffe5b0569ca401c440a0c3a1d72bf39286c0db3fd"),
        new("vocoder.int8.onnx", SupertonicRepository + "vocoder.int8.onnx", 25_991_073,
            "e923d60f53f95eb1ce235f1dc33ec56d9c057823c96fa6f8acf98f32b0da6152"),
        new("tts.json", SupertonicRepository + "tts.json", 8_253,
            "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
        new("unicode_indexer.bin", SupertonicRepository + "unicode_indexer.bin", 262_144,
            "8402ca48e5189a8950138580b0fff64db6f072f24ac07cd54ba8b2fbb9883b30"),
        new("voice.bin", SupertonicRepository + "voice.bin", 517_168,
            "67d5209b0ee8ce6c74105ffbe12fe6a7628aea3b4ba2fcb308a4a67938a93ce8")
    ]);

    public long Size => Files.Sum(file => file.Size);
}

/// <summary>One file of a model, saved as <paramref name="Name"/> and checked against its size and SHA-256.</summary>
public sealed record SpeechModelFile(string Name, string Url, long Size, string Sha256);

/// <summary>
/// Fetches speech models into %LocalAppData%\Link\Models on first use, off the UI thread. A file only takes its
/// name once its size and SHA-256 match, so an interrupted or altered download never looks ready.
/// </summary>
public sealed class SpeechModelManager
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    // A download fails when no data comes for this long; big models may take many minutes on a slow line.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(1);
    // Tries per file before the download counts as failed; each one continues the part already on disk.
    private const int Attempts = 3;
    private readonly string _root;
    private readonly Action<string> _log;
    private readonly Dictionary<SpeechModel, Task> _downloads = [];
    private readonly Dictionary<SpeechModel, string> _failures = [];
    // Bytes of each downloading model on disk so far, for the status line.
    private readonly Dictionary<SpeechModel, long> _received = [];

    public SpeechModelManager(StoragePaths paths, Action<string>? log = null) : this(paths.Models, log)
    {
    }

    /// <summary>Models in another folder, for checks that must not touch the user's data.</summary>
    internal SpeechModelManager(string root, Action<string>? log = null)
    {
        _root = root;
        _log = log ?? (_ => { });
    }

    public string PathOf(SpeechModel model, string file) => Path.Combine(_root, model.Folder, file);

    /// <summary>The path of a model that is a single file.</summary>
    public string PathOf(SpeechModel model) => PathOf(model, model.Files[0].Name);

    public bool IsReady(SpeechModel model) => model.Files.All(file =>
        new FileInfo(PathOf(model, file.Name)) is { Exists: true } info && info.Length == file.Size);

    /// <summary>Completes once the model is on disk, fetching it unless that is under way.</summary>
    public Task EnsureAsync(SpeechModel model)
    {
        if (IsReady(model)) return Task.CompletedTask;
        lock (_downloads)
        {
            if (_downloads.TryGetValue(model, out var running)) return running;
            var download = Task.Run(() => DownloadAsync(model));
            _downloads[model] = download;
            _ = download.ContinueWith(task =>
            {
                lock (_downloads)
                {
                    // A failed download is tried again the next time the model is needed.
                    _downloads.Remove(model);
                    _received.Remove(model);
                    if (task.IsCompletedSuccessfully) _failures.Remove(model);
                    else _failures[model] = task.Exception?.GetBaseException().Message ?? "timed out";
                }

                if (!task.IsCompletedSuccessfully)
                {
                    _log($"[Models] {model.Folder}: download failed ({task.Exception?.GetBaseException().Message ?? "timed out"})");
                }
            }, TaskScheduler.Default);
            return download;
        }
    }

    /// <summary>
    /// Starts fetching a model that is not on disk, unless that is under way. Null when the model is ready;
    /// otherwise what the user waits for, or why the previous attempt failed.
    /// </summary>
    public string? Prepare(SpeechModel model)
    {
        if (IsReady(model)) return null;
        _ = EnsureAsync(model);
        return Status(model);
    }

    /// <summary>Bytes a running download has on disk, or why the last one failed; both null when neither applies.</summary>
    public (long? Received, string? Failure) DownloadState(SpeechModel model)
    {
        lock (_downloads)
        {
            long? received = _downloads.ContainsKey(model) ? _received.GetValueOrDefault(model) : null;
            return (received, _failures.GetValueOrDefault(model));
        }
    }

    /// <summary>Null when the model is ready; otherwise how its download goes, or why the last one failed.</summary>
    public string? Status(SpeechModel model)
    {
        if (IsReady(model)) return null;
        lock (_downloads)
        {
            if (_failures.TryGetValue(model, out var failure)) return $"model download failed ({failure.TrimEnd('.')})";
            var percent = _received.GetValueOrDefault(model) * 100 / model.Size;
            return $"model is downloading ({percent}% of {model.Size / 1_000_000} MB, once)";
        }
    }

    private async Task DownloadAsync(SpeechModel model)
    {
        Directory.CreateDirectory(Path.Combine(_root, model.Folder));
        var done = 0L;
        foreach (var file in model.Files)
        {
            var path = PathOf(model, file.Name);
            if (new FileInfo(path) is { Exists: true } info && info.Length == file.Size)
            {
                done += file.Size;
                continue;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await FetchAsync(model, file, done);
                    break;
                }
                // A connection that breaks or stalls goes on where it stopped; a damaged file starts over.
                catch (Exception exception) when (attempt < Attempts
                                                  && exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    _log($"[Models] {file.Name}: attempt {attempt} failed ({exception.Message}), trying again");
                    await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
                }
            }

            File.Move(path + ".download", path, overwrite: true);
            done += file.Size;
        }

        _log($"[Models] {model.Folder} downloaded");
    }

    /// <summary>
    /// Fetches one file into its .download twin, continuing the part an earlier attempt left there when the server
    /// allows; throws unless the whole file has the pinned size and SHA-256. <paramref name="done"/>: bytes of the
    /// model's files already complete, for the progress.
    /// </summary>
    private async Task FetchAsync(SpeechModel model, SpeechModelFile file, long done)
    {
        var temporaryPath = PathOf(model, file.Name) + ".download";
        var existing = new FileInfo(temporaryPath) is { Exists: true } part && part.Length < file.Size ? part.Length : 0;
        using var stalled = new CancellationTokenSource(StallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
        response.EnsureSuccessStatusCode();

        // A server that sends the whole file again gets it written from the start.
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (append)
        {
            await HashFileAsync(temporaryPath, hash, stalled.Token);
            _log($"[Models] {file.Name}: continuing from {existing / 1_000_000.0:0.0} MB");
        }
        else
        {
            existing = 0;
        }

        lock (_downloads) _received[model] = done + existing;

        bool intact;
        await using (var source = await response.Content.ReadAsStreamAsync(stalled.Token))
        await using (var output = new FileStream(temporaryPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            var received = done + existing;
            int read;
            while ((read = await source.ReadAsync(buffer, stalled.Token)) > 0)
            {
                stalled.CancelAfter(StallTimeout);
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), stalled.Token);
                received += read;
                lock (_downloads)
                {
                    _received[model] = received;
                    // Data flows again, so the last failure is history.
                    _failures.Remove(model);
                }
            }

            intact = output.Length == file.Size
                     && Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.Ordinal);
        }

        if (intact) return;
        File.Delete(temporaryPath);
        throw new InvalidDataException($"{file.Name} did not download intact.");
    }

    private static async Task HashFileAsync(string path, IncrementalHash hash, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
    }
}
