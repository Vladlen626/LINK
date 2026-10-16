using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Web Push to the paired devices' browsers, the way a site notifies an installed app: the message is encrypted for the
/// browser (RFC 8291) and signed with Link's own key (VAPID, RFC 8292), then handed to the browser's push service. Link
/// needs only the internet for it, no account; the key is kept encrypted for this Windows user.
/// </summary>
public sealed class RemotePush(ApiKeyStore key, Action<string> log)
{
    private const int RecordSize = 4096;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Lock _lock = new();
    private ECDsa? _vapid;

    /// <summary>Link's public key as the browser subscribes with it: an uncompressed P-256 point, base64url.</summary>
    public string PublicKey => Base64Url(Point(Vapid().ExportParameters(false)));

    /// <summary>Sends a notification; false when the subscription is gone (the user removed the app or blocked it).</summary>
    public async Task<bool> SendAsync(RemotePushSubscription subscription, string title, string text, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title, body = text });
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var body = Encrypt(payload, FromBase64Url(subscription.P256dh), FromBase64Url(subscription.Auth), server, RandomNumberGenerator.GetBytes(16));
        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Add("TTL", "86400");
        request.Headers.Add("Urgency", "high");
        request.Headers.TryAddWithoutValidation("Authorization", Authorization(new Uri(subscription.Endpoint)));
        try
        {
            using var response = await Http.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return false;
            if (!response.IsSuccessStatusCode) log($"[Remote] Push refused: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            log($"[Remote] Push not sent: {exception.Message}");
        }

        return true;
    }

    /// <summary>
    /// The message body for the browser (RFC 8291): the salt, the record size and Link's one-off public key, then the
    /// payload sealed with a key both sides derive from their keys and the subscription's auth secret.
    /// </summary>
    public static byte[] Encrypt(byte[] payload, byte[] browserPublic, byte[] authSecret, ECDiffieHellman server, byte[] salt)
    {
        using var browser = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = FromPoint(browserPublic) });
        var serverPublic = Point(server.ExportParameters(false));
        var shared = server.DeriveRawSecretAgreement(browser.PublicKey);
        var keyInfo = Concat("WebPush: info\0"u8.ToArray(), browserPublic, serverPublic);
        var inputKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
        var secret = HKDF.Extract(HashAlgorithmName.SHA256, inputKey, salt);
        var contentKey = HKDF.Expand(HashAlgorithmName.SHA256, secret, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, secret, 12, "Content-Encoding: nonce\0"u8.ToArray());
        // One record, the last: the payload and its delimiter.
        var plain = Concat(payload, [2]);
        var sealedText = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(contentKey, tag.Length)) gcm.Encrypt(nonce, plain, sealedText, tag);
        var size = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(size, RecordSize);
        return Concat(salt, size, [(byte)serverPublic.Length], serverPublic, sealedText, tag);
    }

    /// <summary>The VAPID header: a JWT for the push service's origin, valid 12 hours, signed with Link's key, and the key.</summary>
    private string Authorization(Uri endpoint)
    {
        var header = Base64Url("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(),
            sub = "mailto:link@localhost"
        }));
        var signature = Base64Url(Vapid().SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256));
        return $"vapid t={header}.{claims}.{signature}, k={PublicKey}";
    }

    /// <summary>Link's VAPID key: made once and kept; the devices subscribed with it stay good.</summary>
    private ECDsa Vapid()
    {
        lock (_lock)
        {
            if (_vapid is not null) return _vapid;
            var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (key.Read() is { } saved)
            {
                vapid.ImportPkcs8PrivateKey(Convert.FromBase64String(saved), out _);
            }
            else
            {
                key.Save(Convert.ToBase64String(vapid.ExportPkcs8PrivateKey()));
            }

            return _vapid = vapid;
        }
    }

    private static byte[] Point(ECParameters parameters) => Concat([4], parameters.Q.X!, parameters.Q.Y!);

    private static ECPoint FromPoint(byte[] point) =>
        point is [4, ..] && point.Length == 65 ? new ECPoint { X = point[1..33], Y = point[33..] } : throw new CryptographicException("Not an uncompressed P-256 key");

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var padded = text.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
