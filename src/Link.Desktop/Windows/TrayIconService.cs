using System.Drawing;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

public sealed class TrayIconService : IDisposable
{
    // NOTIFYICONDATA limits: 64 characters for the title and 256 for the text, both including the terminator.
    // The tooltip is read at a glance, so it keeps to 64 as well.
    private const int MaxTitleLength = 63;
    private const int MaxMessageLength = 255;
    private const int MaxTipLength = 63;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Dictionary<LinkState, Icon> _icons = [];
    private LinkState _state;
    private Action? _notificationClicked;

    public TrayIconService(Action show, Action settings, Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show Link", null, (_, _) => show());
        menu.Items.Add("Settings", null, (_, _) => settings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = IconFor(LinkState.Idle),
            Text = "Link",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => show();
        _icon.BalloonTipClicked += (_, _) => _notificationClicked?.Invoke();
    }

    /// <summary>Shows what Link is up to: the icon for the state and the status line as the tooltip.</summary>
    public void ShowState(LinkState state, string status)
    {
        var tip = Truncate(string.IsNullOrWhiteSpace(status) ? "Link" : $"Link · {status}", MaxTipLength);
        if (state != _state)
        {
            _state = state;
            _icon.Icon = IconFor(state);
        }

        if (_icon.Text != tip) _icon.Text = tip;
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
        foreach (var icon in _icons.Values) icon.Dispose();
        GC.SuppressFinalize(this);
    }

    // Each state's icon is drawn once, when it is first shown.
    private Icon IconFor(LinkState state)
    {
        if (!_icons.TryGetValue(state, out var icon))
        {
            icon = LinkIconFactory.CreateTrayIcon(state);
            _icons[state] = icon;
        }

        return icon;
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
