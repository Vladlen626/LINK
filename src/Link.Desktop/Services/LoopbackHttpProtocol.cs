using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Link.Desktop.Services;

internal static class LoopbackHttpProtocol
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    public static async Task<LoopbackHttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[8192];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new IOException("Unexpected end of HTTP request.");
            received.Write(buffer, 0, read);
            if (received.Length > MaxHeaderBytes + MaxBodyBytes) throw new InvalidOperationException("Bridge request is too large.");
            headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            if (headerEnd < 0 && received.Length > MaxHeaderBytes) throw new InvalidOperationException("HTTP headers are too large.");
        }

        var bytes = received.ToArray();
        var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) throw new InvalidOperationException("Invalid HTTP request line.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText) && !int.TryParse(lengthText, out contentLength))
        {
            throw new InvalidOperationException("Invalid Content-Length.");
        }
        if (contentLength < 0 || contentLength > MaxBodyBytes) throw new InvalidOperationException("Bridge request body is too large.");

        var bodyOffset = headerEnd + 4;
        byte[] bodyBytes;
        if (headers.GetValueOrDefault("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
        {
            bodyBytes = await ReadChunkedBodyAsync(stream, bytes[bodyOffset..], cancellationToken);
        }
        else
        {
            using var body = new MemoryStream(contentLength);
            var bufferedBodyBytes = Math.Min(contentLength, bytes.Length - bodyOffset);
            if (bufferedBodyBytes > 0) body.Write(bytes, bodyOffset, bufferedBodyBytes);
            while (body.Length < contentLength)
            {
                var wanted = Math.Min(buffer.Length, contentLength - (int)body.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
                if (read == 0) throw new IOException("Unexpected end of HTTP body.");
                body.Write(buffer, 0, read);
            }
            bodyBytes = body.ToArray();
        }

        var target = new Uri($"http://127.0.0.1{requestLine[1]}");
        return new LoopbackHttpRequest(
            requestLine[0].ToUpperInvariant(),
            target.AbsolutePath.TrimEnd('/'),
            ParseQuery(target.Query),
            headers,
            bodyBytes);
    }

    public static async Task WriteResponseAsync(
        NetworkStream stream,
        LoopbackHttpResponse response,
        string? origin,
        CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "Internal Server Error"
        };
        // Only the companion extension gets CORS access; "null" would also admit sandboxed pages.
        var cors = IsExtensionOrigin(origin)
            ? $"Access-Control-Allow-Origin: {origin}\r\n" +
              "Access-Control-Allow-Headers: Content-Type, X-Link-Bridge\r\n" +
              "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
              "Access-Control-Allow-Private-Network: true\r\n"
            : string.Empty;
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.StatusCode} {reason}\r\n" +
            $"Content-Type: {response.ContentType}\r\n" +
            $"Content-Length: {response.Body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            cors + "\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        if (response.Body.Length > 0) await stream.WriteAsync(response.Body, cancellationToken);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        NetworkStream stream,
        byte[] initialBytes,
        CancellationToken cancellationToken)
    {
        var reader = new BufferedNetworkReader(stream, initialBytes);
        using var body = new MemoryStream();

        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(cancellationToken);
            var extensionSeparator = sizeLine.IndexOf(';');
            var sizeText = extensionSeparator >= 0 ? sizeLine[..extensionSeparator] : sizeLine;
            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
            {
                throw new InvalidOperationException("Invalid chunked HTTP body.");
            }

            if (chunkSize == 0)
            {
                while ((await reader.ReadLineAsync(cancellationToken)).Length > 0)
                {
                    // Consume optional trailer headers.
                }
                break;
            }

            if (body.Length + chunkSize > MaxBodyBytes) throw new InvalidOperationException("Bridge request body is too large.");
            await reader.CopyToAsync(body, chunkSize, cancellationToken);
            if ((await reader.ReadLineAsync(cancellationToken)).Length != 0)
            {
                throw new InvalidOperationException("Invalid chunk terminator.");
            }
        }

        return body.ToArray();
    }

    private static bool IsExtensionOrigin(string? origin) =>
        origin is not null &&
        (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
         origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase) ||
         origin.StartsWith("ms-browser-extension://", StringComparison.OrdinalIgnoreCase));

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var index = 3; index < length; index++)
        {
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' && bytes[index - 1] == '\r' && bytes[index] == '\n')
            {
                return index - 3;
            }
        }
        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            values[key] = value;
        }
        return values;
    }

    private sealed class BufferedNetworkReader
    {
        private readonly NetworkStream _stream;
        private byte[] _buffer;
        private int _offset;
        private int _count;

        public BufferedNetworkReader(NetworkStream stream, byte[] initialBytes)
        {
            _stream = stream;
            _buffer = initialBytes.Length == 0 ? new byte[8192] : initialBytes;
            _count = initialBytes.Length;
        }

        public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (line.Length <= 8192)
            {
                var value = await ReadByteAsync(cancellationToken);
                if (value == '\r')
                {
                    if (await ReadByteAsync(cancellationToken) != '\n') throw new InvalidOperationException("Invalid HTTP line ending.");
                    return Encoding.ASCII.GetString(line.ToArray());
                }
                line.WriteByte(value);
            }
            throw new InvalidOperationException("HTTP line is too long.");
        }

        public async Task CopyToAsync(Stream destination, int count, CancellationToken cancellationToken)
        {
            var remaining = count;
            while (remaining > 0)
            {
                await EnsureAvailableAsync(cancellationToken);
                var copied = Math.Min(remaining, _count - _offset);
                await destination.WriteAsync(_buffer.AsMemory(_offset, copied), cancellationToken);
                _offset += copied;
                remaining -= copied;
            }
        }

        private async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            await EnsureAvailableAsync(cancellationToken);
            return _buffer[_offset++];
        }

        private async ValueTask EnsureAvailableAsync(CancellationToken cancellationToken)
        {
            if (_offset < _count) return;
            if (_buffer.Length != 8192) _buffer = new byte[8192];
            _offset = 0;
            _count = await _stream.ReadAsync(_buffer, cancellationToken);
            if (_count == 0) throw new IOException("Unexpected end of HTTP body.");
        }
    }
}

internal sealed record LoopbackHttpRequest(
    string Method,
    string Path,
    Dictionary<string, string> Query,
    Dictionary<string, string> Headers,
    byte[] Body);

internal sealed record LoopbackHttpResponse(int StatusCode, string ContentType, byte[] Body);
