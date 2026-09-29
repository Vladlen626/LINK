using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>The voice pipeline with fake voices and a fake player, and the Gemini client over a fake HTTP stream.</summary>
internal static class TextToSpeechTests
{
    private static readonly SpeechOptions Auto = new(TtsProvider.Auto, "Achird", 0, "ru", 1.2);

    public static async Task RunAsync()
    {
        await PipelineAsync();
        await FallbackAsync();
        await SilenceAsync();
        await PrebufferAsync();
        await GeminiAsync();
        OlderSettings();
    }

    /// <summary>Parts are said in order; text that queues up while one is made joins the next request.</summary>
    private static async Task PipelineAsync()
    {
        var rig = new Rig();
        rig.Gemini.Gate = new TaskCompletionSource();
        var first = rig.Speech.SpeakAsync("Один.", Auto, CancellationToken.None);
        await Program.EventuallyAsync("the first part is requested at once", () => rig.Gemini.Requests.Count, 1);
        var second = rig.Speech.SpeakAsync("Два.", Auto, CancellationToken.None);
        var third = rig.Speech.SpeakAsync("Три.", Auto, CancellationToken.None);
        rig.Gemini.Gate.SetResult();
        await Task.WhenAll(first, second, third);
        Program.Equal("parts that queued up go out as one request", string.Join(" | ", rig.Gemini.Requests), "Один. | Два. Три.");
        Program.Equal("each request plays as one stretch, in order", rig.Output.Played, "Один. | Два. Три.");

        rig = new Rig();
        await rig.Speech.SpeakAsync("Секунду.", Auto, CancellationToken.None);
        await rig.Speech.SpeakAsync("Секунду.", Auto, CancellationToken.None);
        Program.Equal("a short phrase is made once per session", rig.Gemini.Requests.Count, 1);
        Program.Equal("and still said twice", rig.Output.Played, "Секунду. | Секунду.");

        // Long parts never merge, which shows how far ahead of the ear the voice works.
        rig = new Rig();
        rig.Output.Hold = true;
        var parts = new[] { 'а', 'б', 'в' }.Select(letter => new string(letter, 300)).ToArray();
        var said = parts.Select(text => rig.Speech.SpeakAsync(text, Auto, CancellationToken.None)).ToArray();
        await Program.EventuallyAsync("the next part is made while the first plays", () => rig.Gemini.Requests.Count, 2);
        await Task.Delay(50);
        Program.Equal("but not the one after it", rig.Gemini.Requests.Count, 2);
        rig.Output.Release();
        await Program.EventuallyAsync("once the first is heard, the third is made", () => rig.Gemini.Requests.Count, 3);
        rig.Output.Release();
        rig.Output.Release();
        await Task.WhenAll(said);
    }

    /// <summary>A failing voice hands over to the next one and is left alone for a while; the user is told once.</summary>
    private static async Task FallbackAsync()
    {
        var rig = new Rig();
        rig.Gemini.Failure = new TtsProviderException("free limit reached", "HTTP 429", TimeSpan.FromMinutes(1));
        await rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None);
        Program.Equal("the local voice stands in", (rig.Gemini.Requests.Count, rig.Local.Requests.Count), (1, 1));
        Program.Equal("the user is told why, in a few words", string.Join(" | ", rig.Notices),
            "Gemini: free limit reached — the local voice speaks for now");
        await rig.Speech.SpeakAsync("Второе.", Auto, CancellationToken.None);
        Program.Equal("a paused voice is not asked again", (rig.Gemini.Requests.Count, rig.Local.Requests.Count), (1, 2));
        Program.Equal("the notice is not repeated", rig.Notices.Count, 1);
        Program.Equal("the log has the technical reason", rig.Log.Any(line => line.Contains("[TTS] provider error: Gemini") && line.Contains("HTTP 429")), true);

        rig.Gemini.Failure = null;
        rig.Gemini.Generation++;
        await rig.Speech.SpeakAsync("Третье.", Auto, CancellationToken.None);
        Program.Equal("a new key gets a fresh try", rig.Gemini.Requests.Count, 2);

        rig = new Rig();
        rig.Gemini.Failure = new TtsProviderException("no connection", "reset", TimeSpan.FromSeconds(20), retry: true);
        rig.Gemini.FailOnce = true;
        await rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None);
        Program.Equal("a quick transient failure is retried once", (rig.Gemini.Requests.Count, rig.Local.Requests.Count), (2, 0));

        rig = new Rig();
        rig.Gemini.Unavailable = "no API key";
        await rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None);
        Program.Equal("under Auto a voice without a key is skipped quietly", (rig.Local.Requests.Count, rig.Notices.Count), (1, 0));

        rig = new Rig();
        rig.Local.Unavailable = "not downloaded";
        Program.Equal("a chosen voice that is missing is named", await SpeakFailureAsync(rig, Auto with { Provider = TtsProvider.Local }),
            "No voice could speak (local voice: not downloaded).");

        rig = new Rig();
        rig.Gemini.Unavailable = "no API key";
        rig.Local.Unavailable = "not downloaded";
        Program.Equal("under Auto the last voice is named, the one never set up is not", await SpeakFailureAsync(rig, Auto),
            "No voice could speak (local voice: not downloaded).");

        rig = new Rig();
        rig.Gemini.Failure = new TtsProviderException("rejected the request", "HTTP 400", TimeSpan.FromMinutes(10));
        rig.Local.Failure = new InvalidOperationException("model crashed");
        await Program.ThrowsAsync<InvalidOperationException>("when every voice fails the part fails",
            () => rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None));

        rig = new Rig();
        rig.Gemini.AudioThenFailure = new TtsProviderException("connection lost", "reset", TimeSpan.FromSeconds(20));
        await rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None);
        Program.Equal("speech cut off midway is not repeated in another voice", (rig.Local.Requests.Count, rig.Output.Played),
            (0, "Первое."));
    }

    /// <summary>Silencing drops what waits, stops the request being made and the player, and nothing old comes back.</summary>
    private static async Task SilenceAsync()
    {
        var rig = new Rig();
        rig.Gemini.Gate = new TaskCompletionSource();
        using var answer = new CancellationTokenSource();
        var first = rig.Speech.SpeakAsync("Первое.", Auto, answer.Token);
        await Program.EventuallyAsync("speech is being made", () => rig.Gemini.Requests.Count, 1);
        var second = rig.Speech.SpeakAsync("Второе.", Auto, answer.Token);
        answer.Cancel();
        await Program.ThrowsAsync<TaskCanceledException>("the part being made is silenced", () => first);
        await Program.ThrowsAsync<TaskCanceledException>("the waiting part is dropped", () => second);
        await Program.EventuallyAsync("the request is cancelled", () => rig.Gemini.Cancelled, true);
        Program.Equal("the player is stopped", rig.Output.Stops > 0, true);
        Program.Equal("a dropped part is never made", rig.Gemini.Requests.Count, 1);

        rig.Gemini.Gate = null;
        await rig.Speech.SpeakAsync("Новое.", Auto, CancellationToken.None);
        Program.Equal("the next answer speaks without leftovers", rig.Output.Played, "Новое.");

        rig = new Rig();
        rig.Output.Hold = true;
        var playing = rig.Speech.SpeakAsync("Играет.", Auto, CancellationToken.None);
        await Program.EventuallyAsync("a part plays", () => rig.Output.Played, "Играет.");
        rig.Speech.Stop();
        await Program.ThrowsAsync<TaskCanceledException>("Stop silences what plays", () => playing);
        await Program.EventuallyAsync("nothing is left speaking", () => rig.Speech.IsSpeaking, false);
    }

    /// <summary>A voice that can arrive slower than it plays has its speech held back; the one that stands in sets its own.</summary>
    private static async Task PrebufferAsync()
    {
        var rig = new Rig();
        rig.Gemini.Prebuffer = TimeSpan.FromSeconds(2);
        await rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None);
        Program.Equal("the speech of a slow voice is held back", rig.Output.Prebuffers, "2");

        rig = new Rig();
        rig.Gemini.Prebuffer = TimeSpan.FromSeconds(2);
        rig.Gemini.Failure = new TtsProviderException("no connection", "reset", TimeSpan.FromSeconds(20));
        await rig.Speech.SpeakAsync("Первое.", Auto, CancellationToken.None);
        Program.Equal("the voice that stands in sets its own cushion", rig.Output.Prebuffers, "2,0");
    }

    /// <summary>The Gemini client: the request it sends and how it reads the audio stream and the errors.</summary>
    private static async Task GeminiAsync()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"link-tests-{Guid.NewGuid():N}.bin");
        var keys = new GeminiKeyStore(keyFile);
        try
        {
            var handler = new FakeHttp();
            using var gemini = new GeminiTtsProvider(keys, handler, TimeSpan.FromMilliseconds(300));
            Program.Equal("its speech is held back for 2 s", gemini.Prebuffer, TimeSpan.FromSeconds(2));
            await Program.ThrowsAsync<TtsProviderException>("without a key nothing is sent",
                () => gemini.SynthesizeAsync("Привет.", Auto, _ => { }, CancellationToken.None));
            Program.Equal("no request without a key", handler.Requests.Count, 0);

            keys.Save("  test-key  ");
            Program.Equal("the key is kept encrypted, not as text", File.ReadAllText(keyFile).Contains("test-key"), false);
            Program.Equal("the key reads back", new GeminiKeyStore(keyFile).Read(), "test-key");

            var samples = Enumerable.Range(0, 101).Select(index => (byte)index).ToArray();
            handler.Respond = _ => Sse(
                Event("interaction.created", """{"interaction":{"id":"v1"},"event_type":"interaction.created"}"""),
                Event("step.start", """{"index":0,"step":{"type":"model_output"},"event_type":"step.start"}"""),
                Audio(samples[..51]),
                Audio(samples[51..]),
                Event("interaction.completed", """{"interaction":{"status":"completed"},"event_type":"interaction.completed"}"""));
            var heard = new List<PcmAudio>();
            await gemini.SynthesizeAsync("Привет.", Auto, heard.Add, CancellationToken.None);
            var request = handler.Requests[0];
            Program.Equal("streams from the Interactions API", request.Uri, "https://generativelanguage.googleapis.com/v1beta/interactions");
            Program.Equal("the key goes in its header", request.Key, "test-key");
            using (var body = JsonDocument.Parse(request.Body))
            {
                var root = body.RootElement;
                Program.Equal("only the words, the voice and the format are sent",
                    string.Join(',', root.EnumerateObject().Select(property => property.Name)),
                    "model,input,response_format,generation_config,stream,store");
                Program.Equal("the Flash-Lite TTS model", root.GetProperty("model").GetString(), "gemini-3.8-flash-lite-tts");
                var content = root.GetProperty("input")[0].GetProperty("content")[0];
                Program.Equal("the transcript is the text alone", content.GetProperty("text").GetString(), "Привет.");
                Program.Equal("delivery goes in speech_metadata, not the words",
                    content.GetProperty("annotations")[0].GetProperty("type").GetString(), "speech_metadata");
                Program.Equal("the chosen voice", root.GetProperty("generation_config").GetProperty("speech_config")[0]
                    .GetProperty("voice").GetString(), "Achird");
                Program.Equal("raw PCM, streamed, not stored",
                    (root.GetProperty("response_format").GetProperty("mime_type").GetString(),
                        root.GetProperty("stream").GetBoolean(), root.GetProperty("store").GetBoolean()),
                    ("audio/l16", true, false));
            }

            Program.Equal("audio arrives as it streams, in whole samples",
                (heard.Count, heard.All(block => block.Data.Length % 2 == 0 && block.SampleRate == 24000)), (2, true));
            Program.Equal("no byte is lost between events", Convert.ToHexString(heard.SelectMany(block => block.Data).ToArray()),
                Convert.ToHexString(samples[..100]));

            handler.Respond = _ => Json(HttpStatusCode.TooManyRequests,
                """{"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"90s"}]}}""");
            var quota = await Failure(gemini);
            Program.Equal("quota: short message, wait as Google says", (quota?.Message, quota?.Pause, quota?.Retry),
                ("free limit reached", TimeSpan.FromSeconds(90), false));

            // As the real endpoint answers a streaming request with a bad key: the error comes in an array.
            handler.Respond = _ => Json(HttpStatusCode.BadRequest,
                """[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}]}}]""");
            var invalidKey = await Failure(gemini);
            Program.Equal("a bad key is off until it changes", (invalidKey?.Message, invalidKey?.Pause),
                ("API key was not accepted", Timeout.InfiniteTimeSpan));

            handler.Respond = _ => Json(HttpStatusCode.OK, "not json at all");
            Program.Equal("a page that is not the API is a failure, not a crash", (await Failure(gemini))?.Message, "sent no audio");

            handler.Respond = _ => Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"overloaded","status":"UNAVAILABLE"}}""");
            Program.Equal("an overloaded service is worth one retry", (await Failure(gemini))?.Retry, true);

            handler.Respond = _ => Sse(Event("error", """{"error":{"message":"Deadline expired","code":"gateway_timeout"},"event_type":"error"}"""));
            Program.Equal("an error inside the stream is a failure", (await Failure(gemini))?.Message, "temporarily unavailable");

            handler.Respond = _ => Sse(Event("interaction.completed", """{"event_type":"interaction.completed"}"""));
            Program.Equal("a stream without audio is a failure", (await Failure(gemini))?.Message, "sent no audio");

            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) };
            Program.Equal("a hung stream times out", (await Failure(gemini))?.Message, "not responding");

            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) };
            Exception? stopped = null;
            try
            {
                await gemini.SynthesizeAsync("Привет.", Auto, _ => { }, stop.Token);
            }
            catch (Exception exception)
            {
                stopped = exception;
            }

            Program.Equal("the user's stop is a cancellation, not a failure", stopped is OperationCanceledException, true);
        }
        finally
        {
            keys.Clear();
        }
    }

    /// <summary>Settings saved while the Windows voice (3, with its own voice name) existed still load; it becomes Auto.</summary>
    private static void OlderSettings()
    {
        var older = JsonSerializer.Deserialize<AppSettings>("""{"VoiceReplyProvider":3,"VoiceReplyVoice":"Microsoft Irina"}""")!;
        older.Normalize();
        Program.Equal("the removed Windows voice falls back to Auto", older.VoiceReplyProvider, TtsProvider.Auto);

        var local = JsonSerializer.Deserialize<AppSettings>("""{"VoiceReplyProvider":2}""")!;
        local.Normalize();
        Program.Equal("a chosen local voice stays", local.VoiceReplyProvider, TtsProvider.Local);
    }

    /// <summary>What the user is told when nothing could speak.</summary>
    private static async Task<string?> SpeakFailureAsync(Rig rig, SpeechOptions options)
    {
        try
        {
            await rig.Speech.SpeakAsync("Первое.", options, CancellationToken.None);
            return null;
        }
        catch (InvalidOperationException failure)
        {
            return failure.Message;
        }
    }

    private static async Task<TtsProviderException?> Failure(GeminiTtsProvider gemini)
    {
        try
        {
            await gemini.SynthesizeAsync("Привет.", Auto, _ => { }, CancellationToken.None);
            return null;
        }
        catch (TtsProviderException failure)
        {
            return failure;
        }
    }

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    private static string Audio(byte[] data) => Event("step.delta",
        $$"""{"index":0,"delta":{"type":"audio","data":"{{Convert.ToBase64String(data)}}"},"event_type":"step.delta"}""");

    private static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events), Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Rig
    {
        public Rig()
        {
            Speech = new TextToSpeechService(Gemini, Local, Output, line =>
            {
                lock (Log) Log.Add(line);
            });
            Speech.Notice += notice =>
            {
                lock (Notices) Notices.Add(notice);
            };
        }

        public FakeProvider Gemini { get; } = new("Gemini");
        public FakeProvider Local { get; } = new("local voice");
        public FakeOutput Output { get; } = new();
        public TextToSpeechService Speech { get; }
        public List<string> Log { get; } = [];
        public List<string> Notices { get; } = [];
    }

    /// <summary>A voice that "speaks" its text as bytes, one block per request, and fails or waits when told.</summary>
    private sealed class FakeProvider(string name) : ITextToSpeechProvider
    {
        private readonly List<string> _requests = [];

        public string Name => name;
        public string? Unavailable { get; set; }
        public int Generation { get; set; }
        public TimeSpan Prebuffer { get; set; }
        public Exception? Failure { get; set; }
        public bool FailOnce { get; set; }
        public TtsProviderException? AudioThenFailure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public bool Cancelled { get; private set; }

        public List<string> Requests
        {
            get
            {
                lock (_requests) return [.. _requests];
            }
        }

        public void Prepare(SpeechOptions options)
        {
        }

        public async Task SynthesizeAsync(string text, SpeechOptions options, Action<PcmAudio> audio, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(text);
            if (Failure is { } failure)
            {
                if (FailOnce) Failure = null;
                throw failure;
            }

            if (Gate is { } gate)
            {
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            audio(new PcmAudio(Encoding.UTF8.GetBytes(text), 24000));
            if (AudioThenFailure is { } late) throw late;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Plays stretches one after another: each starts once the one before it has finished, and finishes at once
    /// unless <see cref="Hold"/> keeps it playing until <see cref="Release"/>.
    /// </summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private readonly List<FakeSegment> _segments = [];
        private readonly List<string> _played = [];
        private readonly List<TimeSpan> _prebuffers = [];

        public bool Hold { get; set; }
        public int Stops { get; private set; }

        /// <summary>The cushion each voice asked for, in whole seconds and in the order asked.</summary>
        public string Prebuffers
        {
            get
            {
                lock (_segments) return string.Join(",", _prebuffers.Select(cushion => (int)cushion.TotalSeconds));
            }
        }

        public string Played
        {
            get
            {
                lock (_segments) return string.Join(" | ", _played);
            }
        }

        public IAudioSegment Begin()
        {
            var segment = new FakeSegment(this);
            lock (_segments) _segments.Add(segment);
            return segment;
        }

        public void Stop()
        {
            FakeSegment[] dropped;
            lock (_segments)
            {
                Stops++;
                dropped = [.. _segments];
                _segments.Clear();
            }

            foreach (var segment in dropped) segment.Cancel();
        }

        /// <summary>The stretch playing now ends.</summary>
        public void Release()
        {
            lock (_segments)
            {
                _segments.FirstOrDefault(segment => segment.Sounding)?.End();
            }

            Advance();
        }

        public void Dispose()
        {
        }

        /// <summary>Starts the first stretch that is not finished once there is something to play of it.</summary>
        public void Advance()
        {
            lock (_segments)
            {
                _segments.RemoveAll(segment => segment.Finished.IsCompleted);
                if (_segments.FirstOrDefault() is not { Sounding: false } next || next.Text.Length == 0 && !next.IsComplete) return;
                next.Start();
                if (next.Text.Length > 0) _played.Add(next.Text.ToString());
                if (!Hold && next.IsComplete) next.End();
            }

            Advance();
        }

        private sealed class FakeSegment(FakeOutput output) : IAudioSegment
        {
            private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public StringBuilder Text { get; } = new();
            public bool IsComplete { get; private set; }
            public bool Sounding { get; private set; }
            public Task Started => _started.Task;
            public Task Finished => _finished.Task;

            public void Start()
            {
                Sounding = true;
                _started.TrySetResult();
            }

            public void SetPrebuffer(TimeSpan audio)
            {
                lock (output._segments) output._prebuffers.Add(audio);
            }

            public void Write(PcmAudio audio)
            {
                lock (output._segments) Text.Append(Encoding.UTF8.GetString(audio.Data));
                output.Advance();
            }

            public void Complete()
            {
                lock (output._segments)
                {
                    IsComplete = true;
                    if (Sounding && !output.Hold) End();
                }

                output.Advance();
            }

            public void End()
            {
                _started.TrySetResult();
                _finished.TrySetResult();
            }

            public void Cancel()
            {
                _started.TrySetCanceled();
                _finished.TrySetCanceled();
            }
        }
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<(string Uri, string? Key, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null, body));
            return Respond(request);
        }
    }

    /// <summary>A response that never sends a byte, like a hung connection.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
