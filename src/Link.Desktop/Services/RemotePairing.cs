using System.Security.Cryptography;
using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Which devices may control Link: a device pairs once with a six-digit code that Settings shows for ten minutes, and gets
/// a long random token; Link keeps only the token's hash. Wrong codes end the code after a few tries. Thread-safe: the
/// server asks from its own threads.
/// </summary>
public sealed class RemotePairing(
    IEnumerable<RemoteDevice> devices, Action<RemoteDevice> added, Func<DateTimeOffset>? now = null, Action<RemoteDevice, RemotePushSubscription>? subscribed = null)
{
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10);
    public const int Tries = 5;

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private List<RemoteDevice> _devices = [.. devices];
    private string? _code;
    private DateTimeOffset _codeUntil;
    private int _misses;

    /// <summary>A new code for the next device; the one before is no longer good.</summary>
    public string NewCode()
    {
        lock (_lock)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            _codeUntil = _now() + CodeLife;
            _misses = 0;
            return _code;
        }
    }

    /// <summary>The device's token for a good code, used up by it; null for a wrong or old one.</summary>
    public string? Pair(string code, string name)
    {
        lock (_lock)
        {
            if (_code is null || _now() > _codeUntil) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code.Trim()), Encoding.ASCII.GetBytes(_code)))
            {
                if (++_misses >= Tries) _code = null;
                return null;
            }

            _code = null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var device = new RemoteDevice
            {
                Name = name.Trim() is { Length: > 0 } given ? given[..Math.Min(given.Length, 60)] : "Device",
                TokenHash = Hash(token),
                AddedAt = _now()
            };
            _devices.Add(device);
            added(device);
            return token;
        }
    }

    /// <summary>The name of the device a token belongs to; null for none.</summary>
    public string? DeviceOf(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        lock (_lock) return _devices.FirstOrDefault(device => device.TokenHash == hash)?.Name;
    }

    /// <summary>The device of a token asks for notifications at its browser's subscription; false for no device.</summary>
    public bool Subscribe(string? token, RemotePushSubscription subscription)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = Hash(token);
        RemoteDevice? device;
        lock (_lock) device = _devices.FirstOrDefault(each => each.TokenHash == hash);
        if (device is null) return false;
        subscribed?.Invoke(device, subscription);
        return true;
    }

    /// <summary>The devices as Settings now keeps them: a removed one is out at once.</summary>
    public void Reset(IEnumerable<RemoteDevice> devices)
    {
        lock (_lock) _devices = [.. devices];
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
