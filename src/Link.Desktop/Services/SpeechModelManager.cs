using System.Net.Http;
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
    private readonly string _root;
    private readonly Dictionary<SpeechModel, Task> _downloads = [];
    private readonly Dictionary<SpeechModel, string> _failures = [];
    // Bytes of each downloading model on disk so far, for the status line.
    private readonly Dictionary<SpeechModel, long> _received = [];

    public SpeechModelManager(StoragePaths paths) => _root = paths.Models;

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
        var received = 0L;
        foreach (var file in model.Files)
        {
            var path = PathOf(model, file.Name);
            if (new FileInfo(path) is { Exists: true } info && info.Length == file.Size)
            {
                received += file.Size;
                continue;
            }

            var temporaryPath = path + ".download";
            using var stalled = new CancellationTokenSource(StallTimeout);
            try
            {
                using (var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, stalled.Token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(stalled.Token);
                    await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
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

                    if (output.Length != file.Size ||
                        !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"{file.Name} did not download intact.");
                    }
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            catch
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                throw;
            }
        }
    }
}
