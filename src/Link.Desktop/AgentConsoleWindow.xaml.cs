using System.Windows;
using System.Windows.Threading;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// The agent's console: what Link sent its CLI and what the CLI writes back, live. The agents run without a window,
/// so this is where their stream can be watched; the window only reads <see cref="AgentConsole"/>.
/// </summary>
public partial class AgentConsoleWindow : Window
{
    private readonly AgentConsole _console;
    private readonly DispatcherTimer _timer;
    private long _read;
    // The box's own Text copies the whole document on every read.
    private long _shown;

    public AgentConsoleWindow(AgentConsole console)
    {
        _console = console;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        // A few times a second is live enough, and the text box lays out a batch at once rather than every line.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var (text, end, whole) = _console.After(_read);
        if (!whole && text.Length == 0) return;
        var atEnd = OutputBox.VerticalOffset + OutputBox.ViewportHeight >= OutputBox.ExtentHeight - 4;
        if (whole) OutputBox.Text = text;
        else OutputBox.AppendText(text);
        _read = end;
        _shown = whole ? text.Length : _shown + text.Length;
        SizeText.Text = _shown == 0 ? "Nothing yet: it fills when a task starts" : $"{(_shown + 1023) / 1024:N0} KB";
        if (atEnd) OutputBox.ScrollToEnd();
    }

    private void Wrap_OnChanged(object sender, RoutedEventArgs e) =>
        OutputBox.TextWrapping = WrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length > 0) Clipboard.SetText(OutputBox.Text);
    }
}
