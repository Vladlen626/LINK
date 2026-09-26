using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The loopback bridge the browser companion extension polls: tabs announce their conversations, take LINK's requests
/// and report back; the requests themselves are followed by <see cref="ChatRequestBroker"/>.
/// </summary>
internal sealed class BrowserBridgeServer : IDisposable
{
    public const int Port = 43127;
    private static readonly TimeSpan ConversationLifetime = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private readonly int _port;
    private readonly ChatRequestBroker _broker;
    private readonly Dictionary<string, AvailableChatConversation> _conversations = new(StringComparer.Ordinal);
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
        _broker = new ChatRequestBroker(timings, "ChatGPT tab", "Make sure the browser with the linked chat is open, then try again.");
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

    /// <summary>Sends a prompt to the linked tab and waits for the answer.</summary>
    public async Task<ChatResponse> EnqueueAsync(
        ChatPageRequest request,
        IProgress<ChatProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(request.ConversationId);
        return await _broker.SendAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the linked tab shows now; the tab answers at once, even while a prompt there still waits.</summary>
    public async Task<ChatSnapshot> ReadAsync(string conversationId, CancellationToken cancellationToken)
    {
        EnsureAvailable(conversationId);
        return await _broker.ReadAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        _listener?.Stop();
        IsRunning = false;
        _broker.Dispose();

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

    private void EnsureAvailable(string conversationId)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException($"Browser bridge could not start: {StartError ?? "unknown error"}");
        }

        if (!IsConversationAvailable(conversationId))
        {
            throw new InvalidOperationException("The linked ChatGPT conversation is not available. Open it in a browser with the LINK companion enabled.");
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
            var announcement = JsonSerializer.Deserialize<ChatPageConversation>(request.Body, _json);
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
                    Url = announcement.Url ?? string.Empty,
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

            return _broker.NextDelivery(conversationId) is { } delivery
                ? JsonResponse(200, delivery)
                : new LoopbackHttpResponse(204, "application/json", []);
        }

        if (request.Path == "/link/event" && request.Method == "POST")
        {
            var pageEvent = JsonSerializer.Deserialize<ChatPageEvent>(request.Body, _json);
            if (pageEvent is null)
            {
                return JsonResponse(400, new { error = "Invalid event." });
            }

            // The tab learns why LINK no longer waits: only a cancelled request makes it stop ChatGPT.
            return _broker.Report(pageEvent, out var reason)
                ? JsonResponse(200, new { status = "accepted" })
                : JsonResponse(404, new { error = "Request is no longer active.", reason });
        }

        return JsonResponse(404, new { error = "Not found" });
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
}
