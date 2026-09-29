using System.Diagnostics;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Link's voice: text parts → the first provider that can speak (Gemini, then the local voice) → the player.
/// A part is played while it is still being made, and the next one is made while it plays, never more than one
/// ahead, so a silenced answer wastes at most one request. A provider that fails is left alone for a while and
/// the next one takes over; only the log hears the technical reason.
/// </summary>
public sealed class TextToSpeechService : ITextToSpeech
{
    // Parts that queue up while another one is made go out as one request up to this length: fewer requests
    // for the free tier and whole phrases for the intonation.
    private const int MaxBatchLength = 400;
    // Short phrases that come back (waiting cues, "the code is on screen") are made once per session.
    private const int MaxCachedLength = 60;
    private const int CacheSize = 32;
    private static readonly TimeSpan QuickFailure = TimeSpan.FromSeconds(1.5);
    private readonly ITextToSpeechProvider _gemini;
    private readonly ITextToSpeechProvider _local;
    private readonly IAudioOutput _output;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    private readonly List<Part> _queue = [];
    private readonly List<Batch> _unfinished = [];
    private readonly Dictionary<ITextToSpeechProvider, (DateTime Until, int Generation, string Reason)> _paused = [];
    private readonly Dictionary<string, PcmAudio> _cache = [];
    private readonly Queue<string> _cacheOrder = new();
    private Task? _worker;
    private ITextToSpeechProvider? _speaker;
    private string? _notice;

    public TextToSpeechService(
        ITextToSpeechProvider gemini,
        ITextToSpeechProvider local,
        IAudioOutput output,
        Action<string> log)
    {
        _gemini = gemini;
        _local = local;
        _output = output;
        _log = log;
    }

    /// <summary>A short line for the user when another voice stands in, e.g. after Gemini's free limit; any thread.</summary>
    public event Action<string>? Notice;

    /// <summary>The provider that made the last speech, e.g. for Settings to say who spoke the sample.</summary>
    public string? LastSpeaker => _speaker?.Name;

    /// <summary>Why the voice a choice puts first cannot speak now, e.g. "Gemini: no API key"; null when it can.</summary>
    public string? Unavailability(TtsProvider choice)
    {
        var preferred = Chain(choice).First();
        return Absence(preferred) is { } reason ? $"{preferred.Name}: {reason}" : null;
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_lock) return _queue.Count > 0 || _unfinished.Count > 0;
        }
    }

    public Task SpeakAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (text.Length == 0) return Task.CompletedTask;

        var part = new Part(text, options, cancellationToken);
        lock (_lock)
        {
            _queue.Add(part);
            _worker ??= Task.Run(RunAsync);
        }

        part.Registration = cancellationToken.Register(() => Silence(cancellationToken));
        _log($"[TTS] text chunk ready: {text.Length} chars, {options.Language}");
        return part.Done.Task;
    }

    public void Prepare(SpeechOptions options)
    {
        if (Chain(options.Provider).FirstOrDefault(CanSpeak) is not { } provider) return;
        try
        {
            provider.Prepare(options);
        }
        catch (Exception exception)
        {
            _log($"[TTS] prepare failed: {provider.Name} — {exception.Message}");
        }
    }

    public void Stop() => Silence(null);

    public void Dispose()
    {
        Stop();
        _output.Dispose();
        _gemini.Dispose();
        _local.Dispose();
    }

    /// <summary>Every provider that could speak for this choice, in the order they are tried.</summary>
    private IEnumerable<ITextToSpeechProvider> Chain(TtsProvider first) => first switch
    {
        TtsProvider.Local => [_local],
        _ => [_gemini, _local]
    };

    private bool CanSpeak(ITextToSpeechProvider provider) => provider.Unavailable is null && !IsPaused(provider);

    private bool IsPaused(ITextToSpeechProvider provider)
    {
        lock (_lock)
        {
            if (!_paused.TryGetValue(provider, out var pause)) return false;
            // A new key or a downloaded model deserves a fresh try.
            if (DateTime.UtcNow < pause.Until && provider.Generation == pause.Generation) return true;
            _paused.Remove(provider);
            return false;
        }
    }

    private void PauseProvider(ITextToSpeechProvider provider, TtsProviderException failure)
    {
        lock (_lock)
        {
            _paused[provider] = (
                failure.Pause == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + failure.Pause,
                provider.Generation,
                failure.Message);
        }
    }

    /// <summary>Why a provider is not speaking now, in a few words; null when it can.</summary>
    private string? Absence(ITextToSpeechProvider provider)
    {
        if (provider.Unavailable is { } missing) return missing;
        if (!IsPaused(provider)) return null;
        lock (_lock) return _paused.TryGetValue(provider, out var pause) ? pause.Reason : null;
    }

    /// <summary>
    /// Silences everything said with this token, or everything at all without one: what waits, what is being made
    /// and what plays. Each part registers it on its token, so a second call finds nothing left to do.
    /// </summary>
    private void Silence(CancellationToken? token)
    {
        Part[] dropped;
        Batch[] silenced;
        lock (_lock)
        {
            dropped = _queue.Where(part => token is null || part.Token == token).ToArray();
            _queue.RemoveAll(part => token is null || part.Token == token);
            // The batch being made is among the unfinished ones, so its request stops too.
            silenced = _unfinished
                .Where(batch => (token is null || batch.Token == token) && !batch.Cancellation.IsCancellationRequested)
                .ToArray();
        }

        foreach (var part in dropped) part.Cancel();
        foreach (var batch in silenced) batch.Cancel();
        // One player speaks for everyone, so silencing it silences whatever it holds.
        if (token is null || silenced.Length > 0) _output.Stop();
        if (dropped.Length > 0 || silenced.Length > 0)
        {
            _log($"[TTS] silenced: {silenced.Length} parts stopped while made or played, {dropped.Length} waiting parts dropped");
        }
    }

    private async Task RunAsync()
    {
        var previous = Task.CompletedTask;
        while (true)
        {
            // Not more than one stretch ahead of the ear; text that queues up meanwhile joins the next request.
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Silenced or failed: that batch reports it itself.
            }

            Batch batch;
            IAudioSegment segment;
            lock (_lock)
            {
                if (TakeBatch() is not { } taken)
                {
                    _worker = null;
                    return;
                }

                batch = taken;
                segment = _output.Begin();
                // Behind speech that still plays, this part waits for its turn rather than for its voice.
                batch.FollowsSpeech = _unfinished.Count > 0;
                _unfinished.Add(batch);
            }

            previous = segment.Started;
            _ = FinishAsync(batch, segment);
            try
            {
                await MakeAsync(batch, segment).ConfigureAwait(false);
            }
            finally
            {
                segment.Complete();
            }
        }
    }

    /// <summary>The next parts to say: the first waiting one and those after it that sound the same way.</summary>
    private Batch? TakeBatch()
    {
        if (_queue.Count == 0) return null;
        var first = _queue[0];
        var count = 1;
        var length = first.Text.Length;
        while (count < _queue.Count
               && _queue[count].Token == first.Token
               && _queue[count].Options == first.Options
               && length + 1 + _queue[count].Text.Length <= MaxBatchLength)
        {
            length += 1 + _queue[count].Text.Length;
            count++;
        }

        var parts = _queue.GetRange(0, count).ToArray();
        _queue.RemoveRange(0, count);
        return new Batch(parts);
    }

    /// <summary>Makes the batch's audio with the first provider that manages it, straight into its stretch.</summary>
    private async Task MakeAsync(Batch batch, IAudioSegment segment)
    {
        var failures = new List<string>();
        foreach (var provider in Chain(batch.Options.Provider))
        {
            if (provider.Unavailable is { } missing)
            {
                // Under Auto a voice that was never set up is simply not in the running, unless it is the last one.
                if (batch.Options.Provider != TtsProvider.Auto || provider == _local) failures.Add($"{provider.Name}: {missing}");
                continue;
            }

            if (IsPaused(provider)) continue;
            for (var attempt = 1; ; attempt++)
            {
                var result = await TryMakeAsync(provider, batch, segment).ConfigureAwait(false);
                if (result.Done) return;
                if (result.Failure is not { } failure) return;
                if (failure.Retry && attempt == 1 && result.Elapsed < QuickFailure)
                {
                    _log($"[TTS] retry: {provider.Name} after {failure.Detail}");
                    continue;
                }

                PauseProvider(provider, failure);
                failures.Add($"{provider.Name}: {failure.Message}");
                // Part of it was heard: repeating it in another voice would be worse than the gap.
                if (result.HeardAudio) return;
                break;
            }
        }

        batch.Error = new InvalidOperationException(failures.Count > 0
            ? $"No voice could speak ({string.Join("; ", failures)})."
            : "No voice could speak.");
    }

    /// <summary>One attempt with one provider; Done also when the batch was silenced meanwhile.</summary>
    private async Task<Attempt> TryMakeAsync(ITextToSpeechProvider provider, Batch batch, IAudioSegment segment)
    {
        var started = Stopwatch.GetTimestamp();
        var heardAudio = false;
        long bytes = 0;
        var rate = 0;
        if (provider != _speaker)
        {
            _log(_speaker is null
                ? $"[TTS] provider selected: {provider.Name}"
                : $"[TTS] fallback provider: {provider.Name} instead of {_speaker.Name}");
            _speaker = provider;
        }

        var key = batch.Text.Length <= MaxCachedLength ? $"{provider.Name}|{batch.Options}|{batch.Text}" : null;
        PcmAudio? cached;
        lock (_lock) cached = key is null ? null : _cache.GetValueOrDefault(key);
        _log($"[TTS] request started: {provider.Name}, {batch.Text.Length} chars, {Since(batch.QueuedAt)} ms after the text was ready"
             + (cached is null ? string.Empty : ", from the session cache"));
        batch.RequestedAt = started;
        if (cached is not null)
        {
            batch.Provider = provider;
            batch.FirstAudioAt = Stopwatch.GetTimestamp();
            segment.Write(cached);
            batch.AudioBytes = cached.Data.Length;
            batch.SampleRate = cached.SampleRate;
            Announce(provider, batch.Options.Provider);
            return new Attempt(true, null, false, TimeSpan.Zero);
        }

        var audio = key is null ? null : new List<byte>();
        // Set per attempt: a voice that stands in for a failed one has its own pace.
        segment.SetPrebuffer(provider.Prebuffer);
        try
        {
            await provider.SynthesizeAsync(batch.Text, batch.Options, block =>
            {
                if (!heardAudio)
                {
                    heardAudio = true;
                    batch.FirstAudioAt = Stopwatch.GetTimestamp();
                    _log($"[TTS] first audio chunk: {provider.Name}, {Since(started)} ms after the request");
                }

                bytes += block.Data.Length;
                rate = block.SampleRate;
                audio?.AddRange(block.Data);
                segment.Write(block);
            }, batch.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception) when (batch.Cancellation.IsCancellationRequested)
        {
            _log($"[TTS] cancelled: {provider.Name} stopped {Since(started)} ms into the request");
            return new Attempt(true, null, heardAudio, Elapsed(started));
        }
        catch (Exception exception)
        {
            var failure = exception as TtsProviderException
                ?? new TtsProviderException("failed", exception.ToString(), TimeSpan.FromMinutes(1));
            _log($"[TTS] provider error: {provider.Name} — {failure.Message} ({failure.Detail}) after {Since(started)} ms"
                 + (failure.Pause == Timeout.InfiniteTimeSpan ? ", off until its setup changes"
                     : $", skipped for {failure.Pause.TotalSeconds:0} s"));
            return new Attempt(false, failure, heardAudio, Elapsed(started));
        }

        batch.Provider = provider;
        batch.AudioBytes = bytes;
        batch.SampleRate = rate;
        if (audio is { Count: > 0 } && rate > 0)
        {
            lock (_lock)
            {
                if (_cache.TryAdd(key!, new PcmAudio([.. audio], rate)))
                {
                    _cacheOrder.Enqueue(key!);
                    if (_cacheOrder.Count > CacheSize) _cache.Remove(_cacheOrder.Dequeue());
                }
            }
        }

        Announce(provider, batch.Options.Provider);
        return new Attempt(true, null, heardAudio, Elapsed(started));
    }

    /// <summary>Tells the user once when another voice stands in for the chosen one, and once when it is back.</summary>
    private void Announce(ITextToSpeechProvider provider, TtsProvider choice)
    {
        var preferred = Chain(choice).First();
        string? notice = null;
        // Under Auto a voice that was never set up is not missed; one that failed is.
        if (provider != preferred && (choice != TtsProvider.Auto || preferred.Unavailable is null))
        {
            notice = $"{Capitalized(preferred.Name)}: {Absence(preferred) ?? "unavailable"} — the {provider.Name} speaks for now";
        }

        lock (_lock)
        {
            if (notice == _notice) return;
            _notice = notice;
        }

        if (notice is not null) Notice?.Invoke(notice);
    }

    /// <summary>Completes the batch's parts once its stretch has been heard, silenced or has failed.</summary>
    private async Task FinishAsync(Batch batch, IAudioSegment segment)
    {
        try
        {
            await segment.Started.ConfigureAwait(false);
            if (batch.AudioBytes > 0 || batch.FirstAudioAt != 0)
            {
                _log(batch.FollowsSpeech
                    ? "[TTS] playback started right after the part before it"
                    : $"[TTS] playback started: {Since(batch.RequestedAt)} ms after the request, {Since(batch.QueuedAt)} ms after the text was ready");
            }

            await segment.Finished.ConfigureAwait(false);
            // Silenced before the player was: whatever was left of it never sounded.
            if (batch.Cancellation.IsCancellationRequested)
            {
                foreach (var part in batch.Parts) part.Cancel();
                return;
            }

            if (batch.Error is { } error)
            {
                foreach (var part in batch.Parts) part.Done.TrySetException(error);
                return;
            }

            if (batch.Provider is { } provider && batch.SampleRate > 0)
            {
                _log($"[TTS] chunk completed: {provider.Name}, {batch.AudioBytes / 2.0 / batch.SampleRate:0.0} s of speech");
            }

            foreach (var part in batch.Parts) part.Done.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            foreach (var part in batch.Parts) part.Cancel();
        }
        catch (Exception exception)
        {
            _log($"[TTS] playback failed: {exception.Message}");
            foreach (var part in batch.Parts) part.Done.TrySetException(exception);
        }
        finally
        {
            lock (_lock) _unfinished.Remove(batch);
            batch.Dispose();
        }
    }

    private static long Since(long timestamp) => (long)Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    private static TimeSpan Elapsed(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private readonly record struct Attempt(bool Done, TtsProviderException? Failure, bool HeardAudio, TimeSpan Elapsed);

    /// <summary>One text handed over to be said, with the caller's token and the task it waits on.</summary>
    private sealed class Part(string text, SpeechOptions options, CancellationToken token)
    {
        public string Text { get; } = text;
        public SpeechOptions Options { get; } = options;
        public CancellationToken Token { get; } = token;
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }

        public void Cancel() => Done.TrySetCanceled(Token.IsCancellationRequested ? Token : new CancellationToken(true));
    }

    /// <summary>Parts made in one request and played as one stretch.</summary>
    private sealed class Batch : IDisposable
    {
        public Batch(Part[] parts)
        {
            Parts = parts;
            Text = string.Join(' ', parts.Select(part => part.Text));
            // Not linked to the caller's token: Silence cancels it, off the caller's thread.
            Cancellation = new CancellationTokenSource();
        }

        public Part[] Parts { get; }
        public string Text { get; }
        public SpeechOptions Options => Parts[0].Options;
        public CancellationToken Token => Parts[0].Token;
        public long QueuedAt => Parts[0].QueuedAt;
        public CancellationTokenSource Cancellation { get; }
        public long RequestedAt { get; set; }
        public long FirstAudioAt { get; set; }
        public bool FollowsSpeech { get; set; }
        public ITextToSpeechProvider? Provider { get; set; }
        public long AudioBytes { get; set; }
        public int SampleRate { get; set; }
        public Exception? Error { get; set; }

        /// <summary>
        /// Stops the request on a pool thread: cancelling inline would run the provider's unwinding on the thread
        /// that silenced Link, usually the UI thread.
        /// </summary>
        public void Cancel()
        {
            try
            {
                _ = Cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        public void Dispose()
        {
            foreach (var part in Parts) part.Registration.Dispose();
            Cancellation.Dispose();
        }
    }
}
