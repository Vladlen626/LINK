namespace Link.Desktop.Models;

/// <summary>
/// Remote control is the user's own Telegram bot: its token is kept apart, encrypted, and it answers only
/// <see cref="TelegramChat"/>. A page a run opens here (a model's review window) reaches the user by a link it sends.
/// </summary>
public sealed class RemoteSettings
{
    /// <summary>The bot runs.</summary>
    public bool Telegram { get; set; }

    /// <summary>The chat that sent the bot Settings' code; null until then.</summary>
    public long? TelegramChat { get; set; }
}
