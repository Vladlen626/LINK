namespace Link.Desktop.Services;

/// <summary>
/// Remote control is the user's Telegram bot, as Settings has it. A task that needs the user notifies the paired chat
/// while the user is away from this computer; a run that opened a review window sends its link, and only then does
/// Link serve the window to the internet (<see cref="RemoteServer"/> through <see cref="RemoteTunnel"/>).
/// </summary>
public sealed class RemoteControl : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IRemoteHost _host;
    private readonly Func<TimeSpan> _idle;
    private readonly ApiKeyStore _telegramToken;
    private readonly TelegramBot _telegram;
    private readonly RemoteServer _server;
    private readonly RemoteTunnel _tunnel;

    /// <summary>How long the user has been away from this computer before a task's alert goes to Telegram.</summary>
    public static readonly TimeSpan Away = TimeSpan.FromMinutes(2);

    /// <param name="onWindow">Runs an action on the window's thread, where the settings are kept.</param>
    /// <param name="idle">How long the user has not touched this computer.</param>
    /// <param name="telegramToken">The Telegram bot's token, kept encrypted.</param>
    /// <param name="pictures">Where the pictures sent to the bot are kept.</param>
    /// <param name="telegramCards">Where the bot keeps its messages about tasks.</param>
    public RemoteControl(SettingsService settings, IRemoteHost host, ApiKeyStore telegramToken, Action<Action> onWindow,
        Func<TimeSpan> idle, Action<string> log, string pictures, string telegramCards)
    {
        _settings = settings;
        _host = host;
        _idle = idle;
        _telegramToken = telegramToken;
        _telegram = new TelegramBot(TelegramBot.Over(TelegramHttp, telegramToken.Read, log), TelegramBot.FilesOver(TelegramHttp, telegramToken.Read), host,
            () => settings.Current.Remote.TelegramChat,
            chat => onWindow(() =>
            {
                _settings.Current.Remote.TelegramChat = chat;
                _settings.Save();
                Paired?.Invoke();
            }), log, pictures, telegramCards, PageLinkAsync);
        _server = new RemoteServer(host, log);
        _tunnel = new RemoteTunnel(log);
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
        _tunnel.Stop();
        _telegramToken.Clear();
        _settings.Current.Remote.Telegram = false;
        _settings.Current.Remote.TelegramChat = null;
        _settings.Save();
    }

    /// <summary>A chat was paired: Settings shows it.</summary>
    public event Action? Paired;

    /// <summary>The bot as Settings now says.</summary>
    public void Apply()
    {
        if (_settings.Current.Remote.Telegram && _telegramToken.HasKey)
        {
            _telegram.Start();
            return;
        }

        _telegram.Stop();
        _tunnel.Stop();
    }

    /// <summary>
    /// A page of the task's last run (the model's review window) on the internet for the bot: its own ticket needs no
    /// pairing, so the link opens in any browser while Link runs.
    /// </summary>
    private async Task<(string? Url, string? Problem)> PageLinkAsync(Guid task, int page)
    {
        if (_server.Port is null) _server.Start();
        if (await _server.ViewPathAsync(task, page) is not { } path) return (null, "The page does not open any more: ask the agent to open it again.");
        var (address, problem) = await _tunnel.StartAsync(_server.Port!.Value);
        return address is null ? (null, problem) : (address + path, null);
    }

    /// <summary>A task's alert to the paired chat, while the user is away from this computer.</summary>
    private void Alert(Guid task, string title, string text)
    {
        if (_idle() >= Away) _telegram.Alert(task, title, text);
    }

    public void Dispose()
    {
        _host.RemoteAlert -= Alert;
        _telegram.Dispose();
        _tunnel.Dispose();
        _server.Dispose();
    }
}
