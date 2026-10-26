using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Link.Desktop.Services;

/// <summary>
/// The pages a run opened on this computer (a model's review window) for another computer: plain HTTP/1.1 on 127.0.0.1,
/// one request per connection, reached through the tunnel (<see cref="RemoteTunnel"/>), so no port is opened to the
/// network. Each page is passed through under <c>/view/&lt;ticket&gt;/</c>: a long random ticket the Telegram bot's link
/// carries, since a page's own requests carry no token. Nothing else is served.
/// </summary>
public sealed class RemoteServer(IRemoteHost host, Action<string> log) : IDisposable
{
    private const int MaxHeader = 16 * 1024;
    // A review window takes a reference picture or model the user drops on it.
    private const int MaxBody = 32 << 20;
    private const string ViewPrefix = "/view/";
    private static readonly TimeSpan ViewIdle = TimeSpan.FromHours(2);
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(20);
    // The pages are on this computer: never through the system's proxy (a VPN client's).
    private static readonly HttpClient Pages = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Dictionary<string, (Uri Address, DateTimeOffset Used)> _views = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;

    /// <summary>The port it listens on; null while it is off.</summary>
    public int? Port { get; private set; }

    /// <summary>Listens on a free port of this computer.</summary>
    public void Start()
    {
        Stop();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        _stop = new CancellationTokenSource();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync(listener, _stop.Token);
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
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The path a page of the task's last run is passed through at: Link serves the page again if it stopped. Null when
    /// it does not open.
    /// </summary>
    public async Task<string?> ViewPathAsync(Guid id, int page)
    {
        if (await host.OpenPageAsync(id, page) is not { IsLoopback: true } address) return null;
        var root = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
        lock (_views)
        {
            var now = DateTimeOffset.Now;
            foreach (var old in _views.Where(view => now - view.Value.Used > ViewIdle).Select(view => view.Key).ToArray()) _views.Remove(old);
            var ticket = _views.FirstOrDefault(view => view.Value.Address == root).Key ?? System.Security.Cryptography.RandomNumberGenerator.GetHexString(32, lowercase: true);
            _views[ticket] = (root, now);
            return ViewPrefix + ticket + address.PathAndQuery;
        }
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
                log($"[Remote] {exception.GetType().Name}: {exception.Message}");
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
        if (size is < 0 or > MaxBody) return null;
        byte[]? Whole() => chunked ? Unchunked(received.GetBuffer().AsSpan(0, (int)received.Length))
            : received.Length >= size ? received.ToArray()[..size]
            : null;

        byte[]? body;
        while ((body = Whole()) is null)
        {
            if (received.Length > MaxBody) return null;
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
        403 => "Forbidden",
        404 => "Not Found",
        _ => "Error"
    };

    /// <summary>A request of an opened page, passed to its server on this computer as from the page's own address.</summary>
    internal async Task<Response> HandleAsync(Request request, CancellationToken token)
    {
        var rest = request.Path.StartsWith(ViewPrefix, StringComparison.Ordinal) ? request.Path[ViewPrefix.Length..] : string.Empty;
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

    private static Response Error(int status, string message) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message));
}
