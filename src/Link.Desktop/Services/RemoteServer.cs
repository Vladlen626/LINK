using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Link.Desktop.Services;

/// <summary>
/// The remote page and its API, on 127.0.0.1 only: plain HTTP/1.1, one request per connection. Another device comes
/// through a tunnel of the user's (<c>tailscale serve</c> gives it HTTPS), so no port is opened to the network and no
/// URL reservation or admin right is needed. Every API call but pairing needs a paired device's token
/// (<c>Authorization: Bearer</c>); the page itself holds no data. Changes reach the page by long polling. A page a run
/// opened on this computer (a model's review window) is passed through under <c>/view/</c> by a ticket the API gives.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    private const int MaxHeader = 16 * 1024;
    private const int MaxBody = 256 * 1024;
    // A review window takes a reference picture or model the user drops on it.
    private const int MaxViewBody = 32 << 20;
    private const string ViewPrefix = "/view/";
    private static readonly TimeSpan ViewIdle = TimeSpan.FromHours(2);
    // The pages are on this computer: never through the system's proxy (a VPN client's).
    private static readonly HttpClient Pages = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRemoteHost _host;
    private readonly RemotePairing _pairing;
    private readonly Action<string> _log;
    private readonly Func<string>? _pushKey;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private long _version;
    private TaskCompletionSource _change = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The pages a paired device opened, by a random ticket in their path: a page's own requests carry no token.
    private readonly Dictionary<string, (Uri Address, DateTimeOffset Used)> _views = [];

    /// <param name="pushKey">Link's public key for notifications (<see cref="RemotePush.PublicKey"/>); null offers none.</param>
    public RemoteServer(IRemoteHost host, RemotePairing pairing, Action<string> log, Func<string>? pushKey = null)
    {
        _host = host;
        _pairing = pairing;
        _log = log;
        _pushKey = pushKey;
        _host.RemoteChanged += Changed;
    }

    /// <summary>The port it listens on; null while it is off.</summary>
    public int? Port { get; private set; }

    /// <summary>Listens on the port, or tells why it cannot (another program has it).</summary>
    public string? Start(int port)
    {
        Stop();
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            return $"Port {port} is taken: {exception.Message}";
        }

        _listener = listener;
        _stop = new CancellationTokenSource();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync(listener, _stop.Token);
        _log($"[Remote] Listening on http://127.0.0.1:{Port}/");
        return null;
    }

    public void Stop()
    {
        if (_listener is null) return;
        _stop?.Cancel();
        _listener.Stop();
        _listener = null;
        _stop?.Dispose();
        _stop = null;
        Port = null;
        _log("[Remote] Stopped");
    }

    public void Dispose()
    {
        _host.RemoteChanged -= Changed;
        Stop();
    }

    /// <summary>A task changed: the pages waiting on <c>/api/events</c> are answered.</summary>
    private void Changed()
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _change, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
                reading.CancelAfter(ReadWait);
                if (await ReadAsync(stream, reading.Token) is not { } request) return;
                var response = await HandleAsync(request, token);
                await WriteAsync(stream, response, token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            catch (Exception exception)
            {
                _log($"[Remote] {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    internal sealed record Request(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    internal sealed record Response(int Status, string ContentType, byte[] Body);

    /// <summary>One request: its line, headers and body; null for one cut off, too big or not HTTP.</summary>
    private static async Task<Request?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeader];
        var length = 0;
        var end = -1;
        while (end < 0)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // What came after the headers, read on until the body is whole: by its length, or chunk by chunk.
        var received = new MemoryStream();
        received.Write(buffer, end + 4, length - end - 4);
        var chunked = headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        var size = headers.TryGetValue("Content-Length", out var value) && int.TryParse(value, out var parsed) ? parsed : 0;
        var limit = start[1].StartsWith(ViewPrefix, StringComparison.Ordinal) ? MaxViewBody : MaxBody;
        if (size < 0 || size > limit) return null;
        byte[]? Whole() => chunked ? Unchunked(received.GetBuffer().AsSpan(0, (int)received.Length))
            : received.Length >= size ? received.ToArray()[..size]
            : null;

        byte[]? body;
        while ((body = Whole()) is null)
        {
            if (received.Length > limit) return null;
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) return null;
            received.Write(buffer, 0, read);
        }

        var target = start[1];
        var question = target.IndexOf('?');
        return new Request(start[0].ToUpperInvariant(), question < 0 ? target : target[..question], question < 0 ? string.Empty : target[(question + 1)..],
            headers, body);
    }

    /// <summary>A chunked body put together; null while its last chunk has not come.</summary>
    private static byte[]? Unchunked(ReadOnlySpan<byte> data)
    {
        var body = new List<byte>();
        while (true)
        {
            var line = data.IndexOf("\r\n"u8);
            if (line < 0) return null;
            var sizeText = Encoding.ASCII.GetString(data[..line]).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0) return [];
            data = data[(line + 2)..];
            if (size == 0) return [.. body];
            if (data.Length < size + 2) return null;
            body.AddRange(data[..size]);
            data = data[(size + 2)..];
        }
    }

    private static async Task WriteAsync(NetworkStream stream, Response response, CancellationToken token)
    {
        var head = $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\nContent-Type: {response.ContentType}\r\n" +
                   $"Content-Length: {response.Body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                   "Referrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(response.Body, token);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        _ => "Error"
    };

    /// <summary>What a request gets: the page's files for anyone, the API for a paired device.</summary>
    internal async Task<Response> HandleAsync(Request request, CancellationToken token)
    {
        if (request.Path.StartsWith(ViewPrefix, StringComparison.Ordinal)) return await ViewAsync(request, token);
        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return request.Method == "GET" && RemotePage.File(request.Path) is var (type, bytes) ? new Response(200, type, bytes) : Error(404, "Not found");
        }

        if (request.Path == "/api/pair")
        {
            if (request.Method != "POST" || Body<PairRequest>(request) is not { } pair) return Error(400, "Send the code");
            if (_pairing.Pair(pair.Code ?? string.Empty, pair.Name ?? string.Empty) is not { } issued)
            {
                _log("[Remote] A wrong or old pairing code");
                return Error(401, "Wrong or old code: make a new one in Link's Settings");
            }

            _log($"[Remote] Paired: {pair.Name}");
            return Ok(new { token = issued });
        }

        var auth = request.Headers.GetValueOrDefault("Authorization");
        var bearer = auth?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? auth[7..].Trim() : null;
        var device = _pairing.DeviceOf(bearer);
        if (device is null) return Error(401, "Pair this device first");
        if (request.Path == "/api/push") return Push(request, bearer);

        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Guid id = default;
        var hasId = parts.Length >= 3 && parts[1] == "tasks" && Guid.TryParse(parts[2], out id);
        switch (request.Method, parts.Length, hasId)
        {
            case ("GET", 2, _) when parts[1] == "projects":
                return Ok(await _host.ProjectsAsync());
            case ("GET", 2, _) when parts[1] == "tasks":
                return Ok(await _host.TasksAsync());
            case ("GET", 2, _) when parts[1] == "usage":
                return Ok(await _host.UsageAsync() ?? new RemoteUsage(string.Empty, string.Empty));
            case ("GET", 2, _) when parts[1] == "accounts":
                return Ok(await _host.AccountsAsync());
            case ("POST", 2, _) when parts[1] == "accounts":
                return Body<AccountRequest>(request) is { } account ? Done(await _host.SetAccountAsync(account.Name, device)) : Error(400, "Name the account");
            case ("GET", 2, _) when parts[1] == "events":
                return Ok(new { version = await WaitForChangeAsync(request.Query, token) });
            case ("GET", 3, true):
                return await _host.TaskAsync(id) is { } view ? Ok(view) : Error(404, "No such task");
            case ("POST", 2, _) when parts[1] == "tasks":
                return Body<RemoteNewTask>(request) is { Goal.Length: > 0 } task
                    // The page names no files on this computer.
                    ? Done(await _host.StartAsync(task with { Goal = task.Goal.Trim(), Attached = null }, device))
                    : Error(400, "Write the task");
            case ("POST", 4, true):
                return parts[3] switch
                {
                    "reply" => Body<WordsRequest>(request) is { Words.Length: > 0 } reply ? Done(await _host.ReplyAsync(id, reply.Words, device)) : Error(400, "Write something"),
                    "decide" => Done(await _host.DecideAsync(id, Body<DecideRequest>(request)?.Always == true, device)),
                    "refuse" => Done(await _host.RefuseAsync(id, device)),
                    "stop" => Done(await _host.StopAsync(id, device)),
                    "nostop" => Body<SwitchRequest>(request) is { } change ? Done(await _host.SetNoStopAsync(id, change.On, device)) : Error(400, "Say on or off"),
                    "page" => await OpenPageAsync(id, Body<PageRequest>(request)?.Page ?? 0),
                    _ => Error(404, "Not found")
                };
            default:
                return Error(404, "Not found");
        }
    }

    /// <summary>
    /// A page of the task's last run for the device: Link serves it again if it stopped and gives a path of its own
    /// under <see cref="ViewPrefix"/>, so the page reaches the device the way this server does.
    /// </summary>
    private async Task<Response> OpenPageAsync(Guid id, int page)
    {
        if (await _host.OpenPageAsync(id, page) is not { IsLoopback: true } address) return Error(409, "The page does not open any more: ask the agent to open it again");
        var root = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
        lock (_views)
        {
            var now = DateTimeOffset.Now;
            foreach (var old in _views.Where(view => now - view.Value.Used > ViewIdle).Select(view => view.Key).ToArray()) _views.Remove(old);
            var ticket = _views.FirstOrDefault(view => view.Value.Address == root).Key ?? System.Security.Cryptography.RandomNumberGenerator.GetHexString(32, lowercase: true);
            _views[ticket] = (root, now);
            return Ok(new { url = ViewPrefix + ticket + address.PathAndQuery });
        }
    }

    /// <summary>A request of an opened page, passed to its server on this computer as from the page's own address.</summary>
    private async Task<Response> ViewAsync(Request request, CancellationToken token)
    {
        var rest = request.Path[ViewPrefix.Length..];
        var slash = rest.IndexOf('/');
        Uri root;
        lock (_views)
        {
            if (slash < 0 || !_views.TryGetValue(rest[..slash], out var view) || DateTimeOffset.Now - view.Used > ViewIdle) return Error(404, "This page is closed: open it again from its task");
            _views[rest[..slash]] = (view.Address, DateTimeOffset.Now);
            root = view.Address;
        }

        var target = new Uri(root, rest[(slash + 1)..] + (request.Query.Length > 0 ? "?" + request.Query : string.Empty));
        // "//host/..." would lead off the page's server.
        if (target.GetLeftPart(UriPartial.Authority) != root.GetLeftPart(UriPartial.Authority)) return Error(404, "Not found");
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (request.Method != "GET")
        {
            message.Content = new ByteArrayContent(request.Body);
            if (request.Headers.TryGetValue("Content-Type", out var type)) message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }

        try
        {
            using var answer = await Pages.SendAsync(message, token);
            return new Response((int)answer.StatusCode, answer.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                await answer.Content.ReadAsByteArrayAsync(token));
        }
        catch (HttpRequestException)
        {
            return Error(502, "The page's server stopped: open the page again from its task");
        }
    }

    /// <summary>Notifications: the key the browser subscribes with, and its subscription for the device.</summary>
    private Response Push(Request request, string? bearer)
    {
        if (_pushKey is null) return Error(404, "Link sends no notifications");
        if (request.Method == "GET") return Ok(new { publicKey = _pushKey() });
        if (Body<SubscriptionRequest>(request) is not { Endpoint: { } endpoint, Keys: { P256dh: { } p256dh, Auth: { } secret } } ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            return Error(400, "Not a push subscription");
        }

        return _pairing.Subscribe(bearer, new Models.RemotePushSubscription(endpoint, p256dh, secret)) ? Ok(new { ok = true }) : Error(401, "Pair this device first");
    }

    /// <summary>The version of Link's tasks once it differs from the page's, or after the wait: the page then asks again.</summary>
    private async Task<long> WaitForChangeAsync(string query, CancellationToken token)
    {
        var since = query.Split('&').Select(pair => pair.Split('=')).FirstOrDefault(pair => pair is ["since", _]) is { } found &&
                    long.TryParse(found[1], out var parsed) ? parsed : -1;
        var change = Volatile.Read(ref _change);
        if (Interlocked.Read(ref _version) == since) await Task.WhenAny(change.Task, Task.Delay(PollWait, token));
        return Interlocked.Read(ref _version);
    }

    private static T? Body<T>(Request request) where T : class
    {
        try
        {
            return request.Body.Length == 0 ? null : JsonSerializer.Deserialize<T>(request.Body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Response Ok(object value) => new(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static Response Done(string? error) => error is null ? Ok(new { ok = true }) : Error(409, error);

    private static Response Error(int status, string message) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = message }, Json));

    private sealed record PairRequest(string? Code, string? Name);

    private sealed record WordsRequest(string Words);

    private sealed record DecideRequest(bool Always);

    private sealed record SwitchRequest(bool On);

    private sealed record PageRequest(int Page);

    private sealed record AccountRequest(string? Name);

    private sealed record SubscriptionRequest(string? Endpoint, SubscriptionKeys? Keys);

    private sealed record SubscriptionKeys(string? P256dh, string? Auth);
}
