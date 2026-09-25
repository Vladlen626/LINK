using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

internal sealed class BrowserBridgeServer : IDisposable
{
    public const int Port = 43127;
    private static readonly TimeSpan ConversationLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AcknowledgeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] AllowedHosts = [$"127.0.0.1:{Port}", $"localhost:{Port}"];
    private readonly object _gate = new();
    private readonly Dictionary<string, AvailableChatConversation> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Guid>> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public bool IsRunning { get; private set; }
    public string? StartError { get; private set; }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
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

    public async Task<ChatResponse> EnqueueAsync(
        BrowserBridgeRequest request,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = Enqueue(request, progress);
        progress?.Report(new ChatProgress { State = ChatProgressState.Sending });

        // Every exit removes the request, so a failed or cancelled prompt can never be picked up later.
        try
        {
            try
            {
                // A discarded, frozen or stale tab never reports back; fail fast instead of waiting the full timeout.
                await pending.Acknowledged.Task.WaitAsync(AcknowledgeTimeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "The ChatGPT tab did not respond. Reload the linked tab (and the LINK companion after updates), then try again.",
                    exception);
            }

            return await pending.Completion.Task.WaitAsync(RequestTimeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("Timed out waiting for the browser companion.", exception);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(request.RequestId);
            }
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
            return await pending.Snapshot.Task.WaitAsync(AcknowledgeTimeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "The ChatGPT tab did not respond. Reload the linked tab (and the LINK companion after updates), then try again.",
                exception);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(request.RequestId);
            }
        }
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        _listener?.Stop();
        IsRunning = false;

        lock (_gate)
        {
            foreach (var pending in _pending.Values)
            {
                pending.Completion.TrySetException(new InvalidOperationException("Browser bridge stopped."));
            }
            _pending.Clear();
            _queues.Clear();
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
            if (!_queues.TryGetValue(request.ConversationId, out var queue))
            {
                queue = new Queue<Guid>();
                _queues.Add(request.ConversationId, queue);
            }

            queue.Enqueue(request.RequestId);
        }

        return pending;
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
                var response = await RouteAsync(request);
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

    private Task<LoopbackHttpResponse> RouteAsync(LoopbackHttpRequest request)
    {
        // DNS rebinding pages reach 127.0.0.1 under their own host name.
        if (request.Headers.GetValueOrDefault("Host") is not { } host ||
            !AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(JsonResponse(403, new { error = "Forbidden" }));
        }

        if (request.Method == "OPTIONS")
        {
            return Task.FromResult(new LoopbackHttpResponse(204, "text/plain", []));
        }

        if (request.Path == "/link/health" && request.Method == "GET")
        {
            return Task.FromResult(JsonResponse(200, new { status = "ok" }));
        }

        if (!IsAuthorized(request))
        {
            return Task.FromResult(JsonResponse(403, new { error = "Forbidden" }));
        }

        if (request.Path == "/link/announce" && request.Method == "POST")
        {
            var announcement = JsonSerializer.Deserialize<BrowserConversationAnnouncement>(request.Body, _json);
            if (announcement is null || string.IsNullOrWhiteSpace(announcement.ConversationId))
            {
                return Task.FromResult(JsonResponse(400, new { error = "Invalid conversation announcement." }));
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

            return Task.FromResult(JsonResponse(200, new { status = "registered" }));
        }

        if (request.Path == "/link/request" && request.Method == "GET")
        {
            var conversationId = request.Query.GetValueOrDefault("conversationId");
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return Task.FromResult(JsonResponse(400, new { error = "conversationId is required." }));
            }

            BrowserBridgeRequest? bridgeRequest = null;
            lock (_gate)
            {
                if (_queues.TryGetValue(conversationId, out var queue))
                {
                    while (queue.Count > 0 && bridgeRequest is null)
                    {
                        var id = queue.Dequeue();
                        if (_pending.TryGetValue(id, out var pending)) bridgeRequest = pending.Request;
                    }
                }
            }

            return Task.FromResult(bridgeRequest is null
                ? new LoopbackHttpResponse(204, "application/json", [])
                : JsonResponse(200, bridgeRequest));
        }

        if (request.Path == "/link/event" && request.Method == "POST")
        {
            var bridgeEvent = JsonSerializer.Deserialize<BrowserBridgeEvent>(request.Body, _json);
            if (bridgeEvent is null)
            {
                return Task.FromResult(JsonResponse(400, new { error = "Invalid event." }));
            }

            PendingRequest? pending;
            lock (_gate)
            {
                _pending.TryGetValue(bridgeEvent.RequestId, out pending);
            }

            if (pending is null)
            {
                return Task.FromResult(JsonResponse(404, new { error = "Request is no longer active." }));
            }

            ApplyEvent(pending, bridgeEvent);
            return Task.FromResult(JsonResponse(200, new { status = "accepted" }));
        }

        return Task.FromResult(JsonResponse(404, new { error = "Not found" }));
    }

    private void ApplyEvent(PendingRequest pending, BrowserBridgeEvent bridgeEvent)
    {
        pending.Acknowledged.TrySetResult();
        switch (bridgeEvent.State.ToLowerInvariant())
        {
            case "heartbeat":
                // Keeps the tab informed that the request is still wanted; a cancelled one answers 404.
                break;
            case "snapshot":
                var answer = bridgeEvent.Text?.Trim() ?? string.Empty;
                pending.Snapshot.TrySetResult(new ChatSnapshot
                {
                    Text = answer,
                    Generating = bridgeEvent.Generating,
                    CompletedWaitingPrompt = !bridgeEvent.Generating && answer.Length > 0 &&
                                             CompleteWaitingPrompts(pending.Request.ConversationId, answer),
                    Diagnostics = bridgeEvent.Diagnostics
                });
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
                pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Waiting });
                break;
            case "receiving":
                pending.Sent = true;
                pending.Progress?.Report(new ChatProgress
                {
                    State = ChatProgressState.Receiving,
                    Text = bridgeEvent.Text ?? string.Empty
                });
                break;
            case "completed":
                var text = bridgeEvent.Text?.Trim() ?? string.Empty;
                if (text.Length == 0)
                {
                    pending.Completion.TrySetException(new InvalidOperationException("ChatGPT returned an empty response."));
                    break;
                }

                Complete(pending, text);
                break;
            case "error":
                var error = string.IsNullOrWhiteSpace(bridgeEvent.Error)
                    ? "The browser companion could not complete the request."
                    : bridgeEvent.Error.Trim();
                pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Error, Error = error });
                pending.Completion.TrySetException(new InvalidOperationException(error));
                break;
        }
    }

    private static void Complete(PendingRequest pending, string text)
    {
        pending.Progress?.Report(new ChatProgress { State = ChatProgressState.Completed, Text = text });
        pending.Completion.TrySetResult(new ChatResponse { Text = text });
    }

    /// <summary>
    /// A finished answer read from the chat completes the prompt there that still waits: the tab's own watch missed
    /// the answer's end. Only a prompt that reached the chat is answered by what the chat shows after it.
    /// </summary>
    private bool CompleteWaitingPrompts(string conversationId, string answer)
    {
        PendingRequest[] waiting;
        lock (_gate)
        {
            waiting = _pending.Values
                .Where(pending => pending.Sent && pending.Request.ConversationId == conversationId)
                .ToArray();
        }

        foreach (var pending in waiting) Complete(pending, answer);
        return waiting.Length > 0;
    }

    private void RemoveStaleConversations()
    {
        var cutoff = DateTimeOffset.Now - ConversationLifetime;
        foreach (var id in _conversations.Where(pair => pair.Value.LastSeenAt < cutoff).Select(pair => pair.Key).ToArray())
        {
            _conversations.Remove(id);
        }
    }

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

    private sealed class PendingRequest
    {
        public PendingRequest(BrowserBridgeRequest request, IProgress<ChatProgress>? progress)
        {
            Request = request;
            Progress = progress;
        }

        public BrowserBridgeRequest Request { get; }
        public IProgress<ChatProgress>? Progress { get; }

        /// <summary>The prompt reached the chat: what the chat shows after it is its answer.</summary>
        public bool Sent { get; set; }

        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ChatResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ChatSnapshot> Snapshot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
    public string? Diagnostics { get; init; }
}
