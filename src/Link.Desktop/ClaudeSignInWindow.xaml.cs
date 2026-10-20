using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// Claude Code's sign-in without a console: the browser does it, and a code the page may show instead is pasted here.
/// Closes by itself once Claude Code is signed in; when it ends without, says why and can start again.
/// </summary>
public partial class ClaudeSignInWindow : Window
{
    private readonly string _executable;
    private readonly string? _account;
    private ClaudeSignIn _signIn;
    private bool _closed;

    private ClaudeSignInWindow(string executable, string? account)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        _executable = executable;
        _account = account;
        _signIn = ClaudeSignIn.Start(executable, account);
        Loaded += (_, _) => _ = FollowAsync(_signIn);
        Closed += (_, _) =>
        {
            _closed = true;
            _signIn.Dispose();
        };
    }

    /// <summary>Signs an account in (null is the user's own); true when Claude Code says it did.</summary>
    /// <exception cref="Win32Exception">Claude Code could not be started.</exception>
    public static bool Run(Window? owner, string executable, string? account)
    {
        var window = new ClaudeSignInWindow(executable, account);
        // A window not shown yet cannot own another.
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    /// <summary>Closes the window once this sign-in is done; when it ends without, says why and offers another.</summary>
    private async Task FollowAsync(ClaudeSignIn signIn)
    {
        var signedIn = await signIn.Ended;
        if (_closed || signIn != _signIn) return;
        if (signedIn)
        {
            DialogResult = true;
            return;
        }

        StatusText.Text = $"Claude Code ended without signing in{(signIn.Said is { } said ? $": {said}" : ".")}";
        ShowWaiting(false);
    }

    private void ShowWaiting(bool waiting)
    {
        CodeBox.IsEnabled = waiting;
        SendButton.IsEnabled = waiting;
        TryAgainButton.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = waiting ? "Cancel" : "Close";
    }

    private void TryAgain_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var signIn = ClaudeSignIn.Start(_executable, _account);
            _signIn.Dispose();
            _signIn = signIn;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Could not start the sign-in: {exception.Message}";
            return;
        }

        StatusText.Text = "Waiting for the browser…";
        ShowWaiting(true);
        _ = FollowAsync(_signIn);
    }

    private void Send_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) return;
        // Ended meanwhile: the code stays in the box, and the window says why it ended.
        if (!_signIn.SendCode(CodeBox.Text)) return;
        CodeBox.Clear();
        StatusText.Text = "Code sent: Claude Code is checking it…";
    }

    private void CodeBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Send_OnClick(sender, e);
    }

    private void OpenPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_signIn.Page is not { } page)
        {
            StatusText.Text = "Claude Code has not given the page yet: try again in a moment.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception exception)
        {
            StatusText.Text = $"The page could not be opened: {exception.Message}";
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
