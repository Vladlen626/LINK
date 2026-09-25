using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

internal sealed class BrowserBridgeServer : IDisposable
{
    public const int Port = 43127;
    private static readonly TimeSpan ConversationLifetime = TimeSpan.FromSeconds(15);
    // Requests that ended recently stay known, so a tab still working on one learns why LINK dropped it.
    private const int EndedRequestsKept = 64;
    private readonly object _gate = new();
    private readonly int _port;
    private readonly BridgeTimings _timings;
    private readonly Dictionary<string, AvailableChatConversation> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<Guid, string> _ended = [];
    private readonly Queue<Guid> _endedOrder = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _shutdown = new();
    private string[] _allowedHosts = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public BrowserBridgeServer()
        : this(Port, BridgeTimings.Default)
    {
    }

    /// <summary>A bridge on another port (0: any free one) with other waits, for tests.</summary>
    internal BrowserBridgeServer(int port, BridgeTimings timings)
    {
        _port = port;
        _timings = timings;
    }

    public bool IsRunning { get; private set; }
    public string? StartError { get; private set; }

    /// <summary>The port the bridge listens on once started.</summary>
    internal int BoundPort { get; private set; }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _allowedHosts = [$"127.0.0.1:{BoundPort}", $"localhost:{BoundPort}"];
            IsRunning = true;
            _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        }
        catch (Exception exception)
        {
            StartError = exception.Message;
            _listener?.Stop();
            _listener = null;
        }
    }

    public IReadOnlyList<AvailableChatConversation> GetAvailableConversations()
    {
        lock (_gate)
        {
            RemoveStaleConversations();
            return _conversations.Values
                .OrderByDescending(conversation => conversation.LastSeenAt)
                .ToArray();
        }
    }

    public bool IsConversationAvailable(string conversationId)
    {
        lock (_gate)
        {
            RemoveStaleConversations();
            return _conversations.ContainsKey(conversationId);
        }
    }

    /// <summary>
    /// Sends a prompt to the linked tab and waits for the answer. Every way it can end is watched: a tab that never
    /// speaks up, one that goes quiet for good, a request that takes too long. A tab quiet about a prompt for a while
    /// is offered it again, so a reloaded page or companion picks it up where it stands.
    /// </summary>
    public async Task<ChatResponse> EnqueueAsync(
        BrowserBridgeRequest request,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = Enqueue(request, progress);
        progress?.Report(new ChatProgress { State = ChatProgressState.Sending });
        // From the moment Stop is pressed, what the tab still sends is turned away, and the tab stops ChatGPT.
        using var cancelled = cancellationToken.Register(() => End(pending, EndReason.Cancelled));

        // Every exit removes the request, so a failed or cancelled prompt can never be picked up later.
        try
        {
            while (true)
            {
                if (pending.Completion.Task.IsCompleted) return await pending.Completion.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (FindStall(pending) is { } problem)
                {
                    End(pending, EndReason.Failed);
                    // The answer may have arrived just now; then it is returned after all.
                    if (!pending.Completion.Task.IsCompleted) throw new InvalidOperationException(problem);
                    continue;
                }

                await Task.WhenAny(pending.Completion.Task, Task.Delay(_timings.Watchdog, cancellationToken))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Stop may end the wait before its callback has run; the tab must still learn it was a Stop.
            if (cancellationToken.IsCancellationRequested) End(pending, EndReason.Cancelled);
            Forget(pending);
        }
    }

    /// <summary>What the linked tab shows now; the tab answers at once, even while a prompt there still waits.</summary>
    public async Task<ChatSnapshot> ReadAsync(string conversationId, CancellationToken cancellationToken)
    {
        var request = new BrowserBridgeRequest
        {
            RequestId = Guid.NewGuid(),
            ConversationId = conversationId,
            Kind = BrowserBridgeRequest.ReadKind,
            Prompt = string.Empty
        };
        var pending = Enqueue(request, null);
        try
        {
            return await pending.Snapshot.Task.WaitAsync(_timings.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "The ChatGPT tab did not respond. Make sure the browser with the linked chat is open, then try again.",
                exception);
        }
        finally
        {
            End(pending, EndReason.Failed);
            Forget(pending);
        }
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        _listener?.Stop();
        IsRunning = false;

        PendingRequest[] pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var request in pending)
        {
            lock (request)
            {
                request.EndReason ??= EndReason.Failed;
                var stopped = new InvalidOperationException("Browser bridge stopped.");
                request.Completion.TrySetException(stopped);
                request.Snapshot.TrySetException(stopped);
            }
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Shutdown is best-effort and must not block application exit.
        }

        _shutdown.Dispose();
    }

    private PendingRequest Enqueue(BrowserBridgeRequest request, IProgress<ChatProgress>? progress)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException($"Browser bridge could not start: {StartError ?? "unknown error"}");
        }

        if (!IsConversationAvailable(request.ConversationId))
        {
            throw new InvalidOperationException("The linked ChatGPT conversation is not available. Open it in a browser with the LINK companion enabled.");
        }

        var pending = new PendingRequest(request, progress);
        lock (_gate)
        {
            _pending.Add(request.RequestId, pending);
        }

        return pending;
    }

    /// <summary>Why a waiting prompt cannot be answered any more, or null while it still can.</summary>
    private string? FindStall(PendingRequest pending)
    {
        lock (_gate)
        {
            if (pending.HeardAt is not { } heardAt)
            {
                return Since(pending.QueuedAt) > _timings.Acknowledge
                    ? "The ChatGPT tab did not respond. Make sure the browser with the linked chat is open, then try again."
                    : null;
            }

            if (Since(heardAt) > _timings.Silence)
            {
                return pending.Sent
                    ? "Lost contact with the ChatGPT tab while it answered. The answer stays in the chat: Check chat reads it."
                    : "Lost contact with the ChatGPT tab before the prompt was sent. Try again.";
            }

            return Since(pending.QueuedAt) > _timings.Request ? "Timed out waiting for the browser companion." : null;
        }
    }

    private void Forget(PendingRequest pending)
    {
        lock (_gate)
        {
            _pending.Remove(pending.Request.RequestId);
            _ended[pending.Request.RequestId] = pending.EndReason ?? EndReason.Failed;
            _endedOrder.Enqueue(pending.Request.RequestId);
            while (_endedOrder.Count > EndedRequestsKept) _ended.Remove(_endedOrder.Dequeue());
        }
    }

    /// <summary>
    /// The request the tab should work on next. One is offered until the tab speaks up about it, and again whenever
    /// the tab has gone quiet about it: a page or companion that was replaced meanwhile picks it up where it stands.
    /// </summary>
    private Delivery? NextDelivery(string conversationId)
    {
        lock (_gate)
        {
            var pending = _pending.Values
                .Where(candidate => candidate.Request.ConversationId == conversationId &&
                                    candidate.EndReason is null &&
                                    (candidate.OfferedAt is not { } offeredAt ||
                                     Since(Math.Max(offeredAt, candidate.HeardAt ?? offeredAt)) >= _timings.Redeliver))
                .OrderBy(candidate => candidate.QueuedAt)
                .FirstOrDefault();
            if (pending is null) return null;

            pending.OfferedAt = Stopwatch.GetTimestamp();
            pending.Offers++;
            var request = pending.Request;
            // A prompt that reached the chat is only watched when offered again, so its images are not sent twice.
            return new Delivery(
                request.RequestId,
                request.ConversationId,
                request.Kind,
                request.Prompt,
                request.Effort,
                pending.Sent ? [] : request.Attachments,
                pending.Offers,
                pending.Sent);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            try
            {
                var request = await LoopbackHttpProtocol.ReadRequestAsync(stream, cancellationToken);
                var response = Route(request);
                await LoopbackHttpProtocol.WriteResponseAsync(
                    stream,
                    response,
                    request.Headers.GetValueOrDefault("Origin"),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                try
                {
                    await LoopbackHttpProtocol.WriteResponseAsync(
                        stream,
                        JsonResponse(500, new { error = "Bridge request failed." }),
                        null,
                        CancellationToken.None);
                }
                catch (Exception)
                {
                    // The peer may have disconnected while the request was being processed.
                }
            }
        }
    }

    private LoopbackHttpResponse Route(LoopbackHttpRequest request)
    {
        // DNS rebinding pages reach 127.0.0.1 under their own host name.
        if (request.Headers.GetValueOrDefault("Host") is not { } host ||
            !_allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return JsonResponse(403, new { error = "Forbidden" });
        }

        if (request.Method == "OPTIONS")
        {
            return new LoopbackHttpResponse(204, "text/plain", []);
        }

        if (request.Path == "/link/health" && request.Method == "GET")
        {
            return JsonResponse(200, new { status = "ok" });
        }

        if (!IsAuthorized(request))
        {
            return JsonResponse(403, new { error = "Forbidden" });
        }

        if (request.Path == "/link/announce" && request.Method == "POST")
        {
            var announcement = JsonSerializer.Deserialize<BrowserConversationAnnouncement>(request.Body, _json);
            if (announcement is null || string.IsNullOrWhiteSpace(announcement.ConversationId))
            {
                return JsonResponse(400, new { error = "Invalid conversation announcement." });
            }

            lock (_gate)
            {
                _conversations[announcement.ConversationId] = new AvailableChatConversation
                {
                    ConversationId = announcement.ConversationId,
                    Title = string.IsNullOrWhiteSpace(announcement.Title) ? "ChatGPT conversation" : announcement.Title.Trim(),
                    Url = announcement.Url,
                    LastSeenAt = DateTimeOffset.Now
                };
            }

            return JsonResponse(200, new { status = "registered" });
        }

        if (request.Path == "/link/request" && request.Method == "GET")
        {
            var conversationId = request.Query.GetValueOrDefault("conversationId");
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return JsonResponse(400, new { error = "conversationId is required." });
            }

            return NextDelivery(conversationId) is { } delivery
                ? JsonResponse(200, delivery)
                : new LoopbackHttpResponse(204, "application/json", []);
        }

        if (request.Path == "/link/event" && request.Method == "POST")
        {
            var bridgeEvent = JsonSerializer.Deserialize<BrowserBridgeEvent>(request.Body, _json);
            if (bridgeEvent is null)
            {
                return JsonResponse(400, new { error = "Invalid event." });
            }

            PendingRequest? pending;
            string? ended;
            lock (_gate)
            {
                if (_pending.TryGetValue(bridgeEvent.RequestId, out pending)) pending.HeardAt = Stopwatch.GetTimestamp();
                _ended.TryGetValue(bridgeEvent.RequestId, out ended);
            }

            // The tab learns why LINK no longer waits: only a cancelled request makes it stop ChatGPT.
            return pending is not null && ApplyEvent(pending, bridgeEvent)
                ? JsonResponse(200, new { status = "accepted" })
                : JsonResponse(404, new { error = "Request is no longer active.", reason = pending?.EndReason ?? ended ?? "unknown" });
        }

        return JsonResponse(404, new { error = "Not found" });
    }

    /// <summary>Applies what the tab reports; false when the request has already ended.</summary>
    private bool ApplyEvent(PendingRequest pending, BrowserBridgeEvent bridgeEvent)
    {
        var state = bridgeEvent.State.ToLowerInvariant();
        if (state == "snapshot") return ApplySnapshot(pending, bridgeEvent);

        lock (pending)
        {
            if (pending.EndReason is not null) return false;
            switch (state)
            {
                case "heartbeat":
                    // Keeps the tab informed that the request is still wanted; an ended one answers 404.
                    break;
                case "reconnecting":
                    pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Reconnecting });
                    break;
                case "notice":
                    if (!string.IsNullOrWhiteSpace(bridgeEvent.Notice))
                    {
                        pending.Progress?.Report(new ChatProgress
                        {
                            State = ChatProgressState.Sending,
                            Notice = bridgeEvent.Notice.Trim()
                        });
                    }
                    break;
                case "sending":
                    pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Sending });
                    break;
                case "waiting":
                    pending.Sent = true;
                    // A tab that picked the prompt up again reports waiting once more; the answer so far stays.
                    if (!pending.Receiving) pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Waiting });
                    break;
                case "receiving":
                    pending.Sent = true;
                    pending.Receiving = true;
                    pending.Progress?.Report(new ChatProgress
                    {
                        State = ChatProgressState.Receiving,
                        Text = bridgeEvent.Text ?? string.Empty
                    });
                    break;
                case "completed":
                    var text = bridgeEvent.Text?.Trim() ?? string.Empty;
                    if (text.Length == 0) Fail(pending, "ChatGPT returned an empty response.", bridgeEvent.Diagnostics);
                    else Complete(pending, text, bridgeEvent.Diagnostics);
                    break;
                case "error":
                    Fail(
                        pending,
                        string.IsNullOrWhiteSpace(bridgeEvent.Error)
                            ? "The browser companion could not complete the request."
                            : bridgeEvent.Error.Trim(),
                        bridgeEvent.Diagnostics);
                    break;
            }

            return true;
        }
    }

    private bool ApplySnapshot(PendingRequest read, BrowserBridgeEvent snapshot)
    {
        if (read.EndReason is not null) return false;
        var answer = snapshot.Text?.Trim() ?? string.Empty;
        var completedWaitingPrompt = !snapshot.Generating && answer.Length > 0 &&
                                     CompleteWaitingPrompts(read.Request.ConversationId, answer, snapshot.AnswerTo);
        lock (read)
        {
            if (read.EndReason is not null) return false;
            read.EndReason = EndReason.Completed;
            read.Snapshot.TrySetResult(new ChatSnapshot
            {
                Text = answer,
                Generating = snapshot.Generating,
                CompletedWaitingPrompt = completedWaitingPrompt,
                Diagnostics = snapshot.Diagnostics
            });
            return true;
        }
    }

    private static bool Complete(PendingRequest pending, string text, string? diagnostics)
    {
        lock (pending)
        {
            if (pending.EndReason is not null) return false;
            pending.EndReason = EndReason.Completed;
            pending.Progress?.Report(new ChatProgress
            {
                State = ChatProgressState.Completed,
                Text = text,
                Diagnostics = diagnostics
            });
            pending.Completion.TrySetResult(new ChatResponse { Text = text });
            return true;
        }
    }

    private static void Fail(PendingRequest pending, string error, string? diagnostics)
    {
        lock (pending)
        {
            if (pending.EndReason is not null) return;
            pending.EndReason = EndReason.Failed;
            pending.Progress?.Report(new ChatProgress
            {
                State = ChatProgressState.Error,
                Error = error,
                Diagnostics = diagnostics
            });
            pending.Completion.TrySetException(new InvalidOperationException(error));
        }
    }

    private static void End(PendingRequest pending, string reason)
    {
        lock (pending)
        {
            pending.EndReason ??= reason;
        }
    }

    /// <summary>
    /// A finished answer read from the chat completes the prompt there that still waits: the tab's own watch missed
    /// the answer's end. Only a prompt that reached the chat is answered by what the chat shows after it, and when
    /// the tab says which prompt the answer belongs to, only that one.
    /// </summary>
    private bool CompleteWaitingPrompts(string conversationId, string answer, Guid? answerTo)
    {
        PendingRequest[] waiting;
        lock (_gate)
        {
            waiting = _pending.Values
                .Where(pending => pending.Sent &&
                                  pending.Request.Kind != BrowserBridgeRequest.ReadKind &&
                                  pending.Request.ConversationId == conversationId &&
                                  (answerTo is null || pending.Request.RequestId == answerTo))
                .ToArray();
        }

        var completed = false;
        foreach (var pending in waiting) completed |= Complete(pending, answer, null);
        return completed;
    }

    private void RemoveStaleConversations()
    {
        var cutoff = DateTimeOffset.Now - ConversationLifetime;
        foreach (var id in _conversations.Where(pair => pair.Value.LastSeenAt < cutoff).Select(pair => pair.Key).ToArray())
        {
            _conversations.Remove(id);
        }
    }

    private static TimeSpan Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    private static bool IsAuthorized(LoopbackHttpRequest request)
    {
        if (!string.Equals(request.Headers.GetValueOrDefault("X-Link-Bridge"), "1", StringComparison.Ordinal)) return false;
        var origin = request.Headers.GetValueOrDefault("Origin");
        return string.IsNullOrEmpty(origin) ||
               origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
               origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase) ||
               origin.StartsWith("ms-browser-extension://", StringComparison.OrdinalIgnoreCase);
    }

    private LoopbackHttpResponse JsonResponse(int statusCode, object value) =>
        new(statusCode, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, _json));

    /// <summary>Why a request ended, as the tab is told when it still reports on it.</summary>
    private static class EndReason
    {
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    /// <summary>A request as the tab gets it: how often it was offered, and whether it already reached the chat.</summary>
    private sealed record Delivery(
        Guid RequestId,
        string ConversationId,
        string Kind,
        string Prompt,
        string Effort,
        IReadOnlyList<BrowserBridgeAttachment> Attachments,
        int Attempt,
        bool Sent);

    private sealed class PendingRequest
    {
        public PendingRequest(BrowserBridgeRequest request, IProgress<ChatProgress>? progress)
        {
            Request = request;
            Progress = progress;
        }

        public BrowserBridgeRequest Request { get; }
        public IProgress<ChatProgress>? Progress { get; }
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();

        // Written under the bridge's gate: when the request was offered to a tab, how often, and when a tab last spoke.
        public long? OfferedAt { get; set; }
        public int Offers { get; set; }
        public long? HeardAt { get; set; }

        // Written under the request's own lock, together with its progress, so nothing is reported after the end.
        /// <summary>The prompt reached the chat: what the chat shows after it is its answer.</summary>
        public bool Sent { get; set; }
        public bool Receiving { get; set; }
        public string? EndReason { get; set; }

        public TaskCompletionSource<ChatResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ChatSnapshot> Snapshot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>How long the bridge waits for the browser at each step.</summary>
/// <param name="Acknowledge">For the tab's first word about a prompt.</param>
/// <param name="Redeliver">Of quiet about a request before it is offered to the tab again.</param>
/// <param name="Silence">Of quiet about a prompt before its tab counts as lost.</param>
/// <param name="Request">For the whole answer.</param>
/// <param name="Read">For a check of the chat.</param>
/// <param name="Watchdog">Between checks of the waits above.</param>
internal sealed record BridgeTimings(
    TimeSpan Acknowledge,
    TimeSpan Redeliver,
    TimeSpan Silence,
    TimeSpan Request,
    TimeSpan Read,
    TimeSpan Watchdog)
{
    public static BridgeTimings Default { get; } = new(
        Acknowledge: TimeSpan.FromSeconds(20),
        Redeliver: TimeSpan.FromSeconds(10),
        Silence: TimeSpan.FromSeconds(45),
        Request: TimeSpan.FromMinutes(10),
        Read: TimeSpan.FromSeconds(15),
        Watchdog: TimeSpan.FromSeconds(1));
}

internal sealed class BrowserBridgeRequest
{
    /// <summary>A read of what the chat shows now, answered by the tab at once instead of a prompt.</summary>
    public const string ReadKind = "read";

    public required Guid RequestId { get; init; }
    public required string ConversationId { get; init; }
    public string Kind { get; init; } = "prompt";
    public required string Prompt { get; init; }
    public string Effort { get; init; } = "normal";
    public IReadOnlyList<BrowserBridgeAttachment> Attachments { get; init; } = [];
}

internal sealed class BrowserBridgeAttachment
{
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
    public required string Base64 { get; init; }
}

internal sealed class BrowserConversationAnnouncement
{
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
}

internal sealed class BrowserBridgeEvent
{
    public Guid RequestId { get; init; }
    public required string State { get; init; }
    public string? Text { get; init; }
    public string? Error { get; init; }
    public string? Notice { get; init; }
    public bool Generating { get; init; }

    /// <summary>The prompt a snapshot's answer belongs to, when the tab knows it.</summary>
    public Guid? AnswerTo { get; init; }

    public string? Diagnostics { get; init; }
}
