using System.Drawing;

namespace Link.Desktop.Windows;

public sealed class TrayIconService : IDisposable
{
    // NOTIFYICONDATA limits: 64 characters for the title and 256 for the text, both including the terminator.
    private const int MaxTitleLength = 63;
    private const int MaxMessageLength = 255;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Icon _linkIcon;
    private Action? _notificationClicked;

    public TrayIconService(Action show, Action settings, Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show Link", null, (_, _) => show());
        menu.Items.Add("Settings", null, (_, _) => settings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _linkIcon = LinkIconFactory.CreateTrayIcon();
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _linkIcon,
            Text = "Link",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => show();
        _icon.BalloonTipClicked += (_, _) => _notificationClicked?.Invoke();
    }

    /// <summary>Shows a Windows notification; <paramref name="clicked"/> runs if the user clicks it.</summary>
    public void ShowNotification(string title, string message, Action? clicked = null)
    {
        _notificationClicked = clicked;
        _icon.BalloonTipTitle = Truncate(title, MaxTitleLength);
        // Windows refuses an empty balloon text.
        _icon.BalloonTipText = Truncate(string.IsNullOrWhiteSpace(message) ? title : message, MaxMessageLength);
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _linkIcon.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var length = maxLength - 1;
        // Never split an emoji's surrogate pair.
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return $"{text[..length].TrimEnd()}…";
    }
}
