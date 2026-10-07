using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Link.Desktop.Services;

/// <summary>
/// Gemini 3.8 Flash-Lite TTS over the Interactions API, streamed: raw 24 kHz PCM arrives while it is made.
/// Only the words to say go out (with the voice and a short delivery style), never stored by Google (store: false).
/// </summary>
public sealed class GeminiTtsProvider : ITextToSpeechProvider
{
    public const string Model = "gemini-3.8-flash-lite-tts";
    private const int SampleRate = 24000;
    private static readonly Uri Endpoint = new("https://generativelanguage.googleapis.com/v1beta/interactions");
    private static readonly Uri Host = new("https://generativelanguage.googleapis.com/");
    private static readonly TimeSpan WarmInterval = TimeSpan.FromSeconds(50);
    private readonly ApiKeyStore _keys;
    private readonly HttpClient _http;
    // Speech usually starts within a second; a request that stays silent this long has hung.
    private readonly TimeSpan _firstAudioTimeout = TimeSpan.FromSeconds(6);
    private readonly TimeSpan _stallTimeout = TimeSpan.FromSeconds(5);
    private long _warmedAt;

    public GeminiTtsProvider(ApiKeyStore keys)
        : this(keys, new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        })
    {
    }

    internal GeminiTtsProvider(ApiKeyStore keys, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _keys = keys;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        if (timeout is { } shorter) _firstAudioTimeout = _stallTimeout = shorter;
    }

    /// <summary>The prebuilt voices with Google's one-word descriptions; each of them speaks Russian.</summary>
    public static IReadOnlyList<(string Name, string Description)> Voices { get; } =
    [
        ("Achird", "Friendly"), ("Sulafat", "Warm"), ("Callirrhoe", "Easy-going"), ("Umbriel", "Easy-going"),
        ("Zubenelgenubi", "Casual"), ("Vindemiatrix", "Gentle"), ("Achernar", "Soft"), ("Schedar", "Even"),
        ("Kore", "Firm"), ("Orus", "Firm"), ("Alnilam", "Firm"), ("Charon", "Informative"),
        ("Rasalgethi", "Informative"), ("Sadaltager", "Knowledgeable"), ("Iapetus", "Clear"), ("Erinome", "Clear"),
        ("Algieba", "Smooth"), ("Despina", "Smooth"), ("Aoede", "Breezy"), ("Zephyr", "Bright"),
        ("Autonoe", "Bright"), ("Puck", "Upbeat"), ("Laomedeia", "Upbeat"), ("Sadachbia", "Lively"),
        ("Fenrir", "Excitable"), ("Leda", "Youthful"), ("Gacrux", "Mature"), ("Pulcherrima", "Forward"),
        ("Enceladus", "Breathy"), ("Algenib", "Gravelly")
    ];

    public string Name => "Gemini";

    public string? Unavailable => _keys.HasKey ? null : "no API key";

    public int Generation => _keys.Version;

    /// <summary>
    /// Its audio can arrive slower than it plays (5 s of speech took 6–7 s to come on one line) and would stutter;
    /// 2 s held back ride out a phrase of about 7 s at that pace.
    /// </summary>
    public TimeSpan Prebuffer => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Opens the connection ahead of the first request, which saves its TLS handshake. The warm-up carries no key
    /// and no text: the host answers it with a plain error.
    /// </summary>
    public void Prepare(SpeechOptions options)
    {
        if (!_keys.HasKey) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _warmedAt) < WarmInterval.TotalMilliseconds) return;
        Interlocked.Exchange(ref _warmedAt, now);
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, Host);
                using var response = await _http.SendAsync(request);
            }
            catch (Exception)
            {
                // Only a warm-up: the request itself reports what is wrong.
            }
        });
    }

    public async Task SynthesizeAsync(
        string text, SpeechOptions options, Action<PcmAudio> audio, CancellationToken cancellationToken)
    {
        var key = _keys.Read() ?? throw new TtsProviderException("no API key", "no key saved", Timeout.InfiniteTimeSpan);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(RequestBody(text, options), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(_firstAudioTimeout);
        var heardAudio = false;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
            if (!response.IsSuccessStatusCode) throw await DescribeFailureAsync(response, watchdog.Token);

            await using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var carry = Array.Empty<byte>();
            var data = new StringBuilder();
            var completed = false;
            while (!completed && await reader.ReadLineAsync(watchdog.Token) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                    continue;
                }

                // An event ends with an empty line; its name repeats as event_type in the data.
                if (line.Length > 0 || data.Length == 0) continue;
                var payload = data.ToString();
                data.Clear();
                if (payload == "[DONE]") break;
                completed = ReadEvent(payload, ref carry, block =>
                {
                    heardAudio = true;
                    // Once speech flows, only a stream that stops mid-way is a failure.
                    watchdog.CancelAfter(_stallTimeout);
                    audio(new PcmAudio(block, SampleRate));
                });
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TtsProviderException("not responding", heardAudio ? "audio stream stalled" : "no audio in time",
                TimeSpan.FromSeconds(30));
        }
        catch (HttpRequestException exception)
        {
            throw new TtsProviderException("no connection", exception.Message, TimeSpan.FromSeconds(20), retry: true);
        }
        catch (IOException exception)
        {
            throw new TtsProviderException("connection lost", exception.Message, TimeSpan.FromSeconds(20), retry: !heardAudio);
        }

        if (!heardAudio) throw new TtsProviderException("sent no audio", "the stream ended without audio", TimeSpan.FromMinutes(1));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The request: the words to say, the voice, and one constant delivery style kept out of the words.</summary>
    internal static string RequestBody(string text, SpeechOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "user_input",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["annotations"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "speech_metadata",
                        ["style"] = Style(options.Speed)
                    })
                })
            }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "audio",
                ["mime_type"] = "audio/l16",
                ["sample_rate"] = SampleRate
            },
            ["generation_config"] = new JsonObject
            {
                ["speech_config"] = new JsonArray(new JsonObject
                {
                    ["voice"] = options.GeminiVoice,
                    ["language"] = options.Language == "ru" ? "ru-RU" : "en-US"
                })
            },
            ["stream"] = true,
            ["store"] = false
        };
        return body.ToJsonString();
    }

    /// <summary>
    /// Google advises one short style reused for every turn, so the voice stays the same; the pace is the only
    /// part that follows the settings.
    /// </summary>
    internal static string Style(double speed) => speed switch
    {
        < 0.85 => "calm, friendly and conversational, speaking slowly",
        >= 1.6 => "calm, friendly and conversational, speaking rapidly",
        // The default 1.2 stays at Gemini's own pace, which already suits it.
        >= 1.3 => "calm, friendly and conversational, at a brisk pace",
        _ => "calm, friendly and conversational"
    };

    /// <summary>
    /// Handles one event of the stream: audio goes to <paramref name="audio"/> in whole samples. True once the
    /// interaction is complete; an error event throws.
    /// </summary>
    private static bool ReadEvent(string payload, ref byte[] carry, Action<byte[]> audio)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new TtsProviderException("sent a damaged answer", exception.Message, TimeSpan.FromMinutes(1));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var type = root.TryGetProperty("event_type", out var eventType) ? eventType.GetString() : null;
            switch (type)
            {
                case "step.delta" when root.TryGetProperty("delta", out var delta)
                                       && delta.TryGetProperty("type", out var deltaType)
                                       && deltaType.GetString() == "audio"
                                       && delta.TryGetProperty("data", out var data):
                    var bytes = data.GetBytesFromBase64();
                    if (carry.Length > 0) bytes = [.. carry, .. bytes];
                    // A sample split between two events keeps its first byte for the next one.
                    carry = bytes.Length % 2 == 0 ? [] : [bytes[^1]];
                    var whole = bytes.Length - carry.Length;
                    if (whole > 0) audio(whole == bytes.Length ? bytes : bytes[..whole]);
                    return false;
                case "error":
                    throw DescribeError(null,
                        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error : null, null);
                case "interaction.completed":
                    return true;
                default:
                    return false;
            }
        }
    }

    private static async Task<TtsProviderException> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // The status alone says enough.
        }

        JsonElement? error = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            // A streaming request gets its error wrapped in an array: [{"error": {...}}].
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var element)
                && element.ValueKind == JsonValueKind.Object)
            {
                error = element.Clone();
            }
        }
        catch (JsonException)
        {
            // Not JSON, e.g. a proxy's page.
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        return DescribeError(response.StatusCode, error, retryAfter);
    }

    /// <summary>Turns an API error into what the user is told and how long Gemini is left alone.</summary>
    private static TtsProviderException DescribeError(HttpStatusCode? status, JsonElement? error, TimeSpan? retryAfter)
    {
        var message = error?.TryGetProperty("message", out var text) == true ? text.GetString() ?? string.Empty : string.Empty;
        var code = error?.TryGetProperty("status", out var statusText) == true ? statusText.GetString()
            : error?.TryGetProperty("code", out var codeValue) == true ? codeValue.ToString()
            : null;
        var detail = $"{(status is { } s ? $"HTTP {(int)s} " : string.Empty)}{code} {message}".Trim();
        var kind = $"{code} {message}";

        if (status == HttpStatusCode.TooManyRequests || Contains(kind, "RESOURCE_EXHAUSTED") || Contains(kind, "quota"))
        {
            var pause = retryAfter ?? RetryDelay(error) ?? TimeSpan.FromMinutes(2);
            return new TtsProviderException("free limit reached", detail,
                pause < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : pause > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : pause);
        }

        if (Contains(kind, "API key") || Contains(kind, "API_KEY") || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new TtsProviderException("API key was not accepted", detail, Timeout.InfiniteTimeSpan);
        }

        if (status is HttpStatusCode.NotFound) return new TtsProviderException("model is not available", detail, TimeSpan.FromHours(1));
        if (status is { } failure && (int)failure >= 500 || Contains(kind, "unavailable") || Contains(kind, "timeout"))
        {
            return new TtsProviderException("temporarily unavailable", detail, TimeSpan.FromSeconds(30), retry: true);
        }

        return new TtsProviderException("rejected the request", detail, TimeSpan.FromMinutes(10));
    }

    /// <summary>The wait that google.rpc.RetryInfo suggests, e.g. "37s".</summary>
    private static TimeSpan? RetryDelay(JsonElement? error)
    {
        if (error?.TryGetProperty("details", out var details) != true || details.ValueKind != JsonValueKind.Array) return null;
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.TryGetProperty("retryDelay", out var delay)
                && delay.GetString() is { } value && value.EndsWith('s')
                && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
