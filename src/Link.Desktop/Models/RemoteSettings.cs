namespace Link.Desktop.Models;

/// <summary>
/// Remote control: Link's tasks from a phone or another computer's browser, through a page Link serves on this computer
/// only (127.0.0.1). Another device reaches it through a tunnel of the user's, such as <c>tailscale serve</c>; each device is
/// paired once with a code Link shows, and only its token's hash is kept.
/// </summary>
public sealed class RemoteSettings
{
    public const int DefaultPort = 8787;

    public bool Enabled { get; set; }
    public int Port { get; set; } = DefaultPort;
    public List<RemoteDevice> Devices { get; set; } = [];

    /// <summary>The user's Telegram bot runs (its token is kept apart, encrypted); it answers only <see cref="TelegramChat"/>.</summary>
    public bool Telegram { get; set; }

    /// <summary>The chat that sent the bot Settings' code; null until then.</summary>
    public long? TelegramChat { get; set; }

    public void Normalize()
    {
        if (Port is < 1024 or > 65535) Port = DefaultPort;
        Devices = Devices is null ? [] : [.. Devices.Where(device => device is { TokenHash.Length: 64 })];
        foreach (var device in Devices)
        {
            device.Name = string.IsNullOrWhiteSpace(device.Name) ? "Device" : device.Name.Trim();
            // A push service is always reached over HTTPS.
            if (device.Push is { } push && !(Uri.TryCreate(push.Endpoint, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps)) device.Push = null;
        }
    }
}

/// <summary>A paired device: its name as the user gave it, the SHA-256 of its token (hex), and its browser's push subscription.</summary>
public sealed class RemoteDevice
{
    public string Name { get; set; } = "Device";
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Where Link's notifications go for it; null until the page asked for them.</summary>
    public RemotePushSubscription? Push { get; set; }
}

/// <summary>A browser's push subscription: its push service's address and the keys a message is encrypted with (base64url).</summary>
public sealed record RemotePushSubscription(string Endpoint, string P256dh, string Auth);
