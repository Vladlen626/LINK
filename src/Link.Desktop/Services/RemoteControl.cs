using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Remote control as Settings has it: the server runs only while it is on, devices pair with a code from Settings and are
/// removed there. Paired devices are saved at once, apart from Settings' Save, so an open Settings window loses none.
/// A task that needs the user notifies the devices that asked for it, while the user is away from this computer.
/// </summary>
public sealed class RemoteControl : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IRemoteHost _host;
    private readonly Action<string> _log;
    private readonly RemotePairing _pairing;
    private readonly RemotePush _push;
    private readonly Action<Action> _onWindow;
    private readonly Func<TimeSpan> _idle;
    private readonly ApiKeyStore _telegramToken;
    private readonly TelegramBot _telegram;
    private RemoteServer? _server;

    /// <summary>How long the user has been away from this computer before a task's alert goes to the devices.</summary>
    public static readonly TimeSpan Away = TimeSpan.FromMinutes(2);

    /// <param name="onWindow">Runs an action on the window's thread, where the settings are kept.</param>
    /// <param name="idle">How long the user has not touched this computer.</param>
    /// <param name="telegramToken">The Telegram bot's token, kept encrypted.</param>
    /// <param name="pictures">Where the pictures sent to the bot are kept.</param>
    /// <param name="telegramCards">Where the bot keeps its messages about tasks.</param>
    public RemoteControl(SettingsService settings, IRemoteHost host, RemotePush push, ApiKeyStore telegramToken, Action<Action> onWindow,
        Func<TimeSpan> idle, Action<string> log, string pictures, string telegramCards)
    {
        _settings = settings;
        _host = host;
        _push = push;
        _onWindow = onWindow;
        _idle = idle;
        _log = log;
        _telegramToken = telegramToken;
        _telegram = new TelegramBot(TelegramBot.Over(TelegramHttp, telegramToken.Read, log), TelegramBot.FilesOver(TelegramHttp, telegramToken.Read), host,
            () => settings.Current.Remote.TelegramChat,
            chat => onWindow(() =>
            {
                _settings.Current.Remote.TelegramChat = chat;
                _settings.Save();
                Paired?.Invoke();
            }), log, pictures, telegramCards);
        _pairing = new RemotePairing(settings.Current.Remote.Devices, device => onWindow(() =>
            {
                _settings.Current.Remote.Devices.Add(device);
                _settings.Save();
                Paired?.Invoke();
            }),
            subscribed: (device, subscription) => onWindow(() =>
            {
                device.Push = subscription;
                _settings.Save();
                _log($"[Remote] {device.Name} gets notifications");
            }));
        _host.RemoteAlert += Alert;
    }

    // Telegram holds a long poll up to 50 s.
    private static readonly System.Net.Http.HttpClient TelegramHttp = new() { Timeout = TimeSpan.FromSeconds(75) };

    /// <summary>Whether the Telegram bot runs, and whether a chat is paired with it.</summary>
    public (bool Running, bool Paired) Telegram => (_telegram.Running, _settings.Current.Remote.TelegramChat is not null);

    /// <summary>
    /// Pairing in Telegram: the token typed in Settings (or the one kept) starts the bot at once, and the code it shows
    /// pairs the chat that sends it. Null and why, when there is no token.
    /// </summary>
    public (string? Code, string? Problem) PairTelegram(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _telegramToken.Save(token.Trim());
        if (!_telegramToken.HasKey) return (null, "Paste the bot's token from @BotFather first.");
        _settings.Current.Remote.Telegram = true;
        _settings.Save();
        _telegram.Start();
        return (_telegram.NewCode(), null);
    }

    /// <summary>The bot stops and forgets its chat and token.</summary>
    public void UnlinkTelegram()
    {
        _telegram.Stop();
        _telegramToken.Clear();
        _settings.Current.Remote.Telegram = false;
        _settings.Current.Remote.TelegramChat = null;
        _settings.Save();
    }

    /// <summary>A device was paired: Settings shows it.</summary>
    public event Action? Paired;

    /// <summary>The address the page is served at on this computer; null while remote control is off.</summary>
    public string? Address => _server?.Port is { } port ? $"http://127.0.0.1:{port}/" : null;

    /// <summary>Why the server could not start, as Apply last found; null when it runs or is off.</summary>
    public string? Problem { get; private set; }

    public IReadOnlyList<RemoteDevice> Devices => _settings.Current.Remote.Devices;

    /// <summary>The server as Settings now says: started on its port, moved to another, or stopped.</summary>
    public void Apply()
    {
        var remote = _settings.Current.Remote;
        Problem = null;
        if (remote.Telegram && _telegramToken.HasKey) _telegram.Start();
        else _telegram.Stop();
        if (!remote.Enabled)
        {
            _server?.Dispose();
            _server = null;
            return;
        }

        if (_server?.Port == remote.Port) return;
        _server ??= new RemoteServer(_host, _pairing, _log, () => _push.PublicKey);
        Problem = _server.Start(remote.Port);
        if (Problem is not null) _log($"[Remote] {Problem}");
    }

    /// <summary>A code for the next device, good for <see cref="RemotePairing.CodeLife"/>.</summary>
    public string NewCode() => _pairing.NewCode();

    public void Remove(RemoteDevice device)
    {
        _settings.Current.Remote.Devices.Remove(device);
        _settings.Save();
        _pairing.Reset(_settings.Current.Remote.Devices);
        _log($"[Remote] Removed device {device.Name}");
    }

    /// <summary>
    /// A task's alert to every device that asked for notifications, while remote control is on and the user is away; a
    /// device whose subscription is gone stops getting them.
    /// </summary>
    private void Alert(Guid task, string title, string text)
    {
        if (_idle() < Away) return;
        _telegram.Alert(task, title, text);
        if (_server is null) return;
        foreach (var device in Devices.Where(device => device.Push is not null).ToArray())
        {
            var subscription = device.Push!;
            _ = Task.Run(async () =>
            {
                if (await _push.SendAsync(subscription, title, text, CancellationToken.None)) return;
                _onWindow(() =>
                {
                    if (device.Push != subscription) return;
                    device.Push = null;
                    _settings.Save();
                    _log($"[Remote] {device.Name}'s notifications are gone");
                });
            });
        }
    }

    public void Dispose()
    {
        _host.RemoteAlert -= Alert;
        _telegram.Dispose();
        _server?.Dispose();
        _server = null;
    }
}
