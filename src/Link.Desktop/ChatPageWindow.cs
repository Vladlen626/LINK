using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Link.Desktop.Services;
using Link.Desktop.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Link.Desktop;

/// <summary>
/// LINK's own ChatGPT page. WebView2 cannot draw in the layered overlay, so the page has a window of its own. The window
/// is never hidden or minimized: the engine would then throttle the page, and ChatGPT would type an answer out a few
/// characters a second. Out of the user's way, it waits off every monitor instead. While LINK does not need it, the
/// page rests out of its engine's sight: drawn off screen, ChatGPT's animations would cost a tenth of a core all day.
/// </summary>
internal sealed class ChatPageWindow : Window, IChatPage
{
    // The engine draws a page it does not see at full speed, and a resting page's timers, the companion's too, still run
    // on time.
    private const string BrowserArguments =
        "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
    // Signing in may open windows of its own; they stay in the page's profile, so the session lands where LINK needs it.
    private static readonly string[] SignInHosts =
        ["auth.openai.com", "accounts.google.com", "login.microsoftonline.com", "login.live.com", "appleid.apple.com"];
    // A page whose engine failed is started again, but never in a loop.
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(1);
#if DEBUG
    private const bool DevTools = true;
#else
    private const bool DevTools = false;
#endif
    private readonly string _userDataFolder;
    private readonly string _script;
    private WebView2? _view;
    private Uri _address = ChatPageAddress.Home;
    private volatile bool _revealed;
    private bool _resting;
    private bool _closing;
    private DateTime _recoveredAt = DateTime.MinValue;

    /// <param name="userDataFolder">The page's browser profile: ChatGPT's session and cookies live there.</param>
    /// <param name="script">The companion script every new document runs before the page's own scripts.</param>
    public ChatPageWindow(string userDataFolder, string script)
    {
        _userDataFolder = userDataFolder;
        _script = script;
        Title = "ChatGPT · Link";
        // A desktop-sized page: a narrow one would switch ChatGPT to its mobile layout.
        Width = 1100;
        Height = 800;
        MinWidth = 480;
        MinHeight = 400;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 100;
        Top = SystemParameters.VirtualScreenTop;
        SourceInitialized += OnSourceInitialized;
        Activated += OnActivated;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    public event Action<string>? MessageReceived;
    public event Action? DocumentStarted;
    public event Action<string>? LoadFailed;

    public bool IsRevealed => _revealed;

    /// <summary>Shows the window off screen, starts the page's engine and loads ChatGPT.</summary>
    public async Task StartAsync()
    {
        Show();
        await StartPageAsync();
    }

    public void Post(string json)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Post(json));
            return;
        }

        try
        {
            _view?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A page between documents misses the message; LINK offers a request again.
        }
    }

    public void Navigate(Uri address)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Navigate(address));
            return;
        }

        // Before the engine has started, the address is where it starts.
        _address = address;
        try
        {
            _view?.CoreWebView2?.Navigate(address.AbsoluteUri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            LoadFailed?.Invoke(exception.Message);
        }
    }

    public void Reveal()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(Reveal);
            return;
        }

        _revealed = true;
        ShowPage();
        WindowStyleService.SetToolWindow(this, noActivate: false);
        WindowStyleService.CenterOnMonitorOf(this, Application.Current.MainWindow);
        Activate();
        _view?.Focus();
    }

    public void Rest(bool resting)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Rest(resting));
            return;
        }

        _resting = resting;
        ShowPage();
    }

    /// <summary>Closes the window for good, when LINK exits.</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _view?.Dispose();
        base.OnClosed(e);
    }

    private async Task StartPageAsync()
    {
        var view = new WebView2();
        _view = view;
        Content = view;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                null,
                _userDataFolder,
                new CoreWebView2EnvironmentOptions(BrowserArguments));
            await view.EnsureCoreWebView2Async(environment);
        }
        catch (Exception exception)
        {
            LoadFailed?.Invoke(exception is WebView2RuntimeNotFoundException
                ? "the Microsoft Edge WebView2 Runtime is not installed"
                : exception.Message);
            throw;
        }

        var core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = DevTools;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ContentLoading += (_, _) => DocumentStarted?.Invoke();
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        // LINK has its own voice: the page gets no microphone, camera, notifications or location.
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += OnProcessFailed;
        core.DocumentTitleChanged += (_, _) => Title = $"{core.DocumentTitle} · Link";
        await core.AddScriptToExecuteOnDocumentCreatedAsync(_script);
        ShowPage();
        core.Navigate(_address.AbsoluteUri);
    }

    /// <summary>Keeps the page in its engine's sight while LINK or the user needs it, and lets it rest otherwise.</summary>
    private void ShowPage()
    {
        if (_view?.CoreWebView2 is not { } core) return;
        var awake = _revealed || !_resting;
        _view.Visibility = awake ? Visibility.Visible : Visibility.Hidden;
        core.MemoryUsageTargetLevel = awake ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only ChatGPT's own page speaks for the companion.
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !ChatPageAddress.IsChatGpt(source)) return;
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        MessageReceived?.Invoke(message);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Only a page that never arrived has failed to load: one replaced by another navigation has not, and on an
        // HTTP error page of ChatGPT's own the companion still says what it sees.
        if (e.IsSuccess || e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.Unknown)
        {
            return;
        }

        LoadFailed?.Invoke(e.WebErrorStatus.ToString());
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var parsed = Uri.TryCreate(e.Uri, UriKind.Absolute, out var address);
        if (parsed && (SignInHosts.Contains(address!.Host, StringComparer.OrdinalIgnoreCase) ||
                       _revealed && address.AbsoluteUri == "about:blank"))
        {
            return;
        }

        e.Handled = true;
        // Links from answers open in the user's browser, never in a window out of sight.
        if (!parsed || address!.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp) return;
        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Without a default browser the link is simply not opened.
        }
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (DateTime.UtcNow - _recoveredAt < RecoveryInterval) return;
        try
        {
            switch (e.ProcessFailedKind)
            {
                // A crashed or hung page loads again; its companion picks a waiting request up where it stands.
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    _recoveredAt = DateTime.UtcNow;
                    _view?.CoreWebView2?.Reload();
                    break;
                // Without its browser process the page is gone and starts over, once that process has let go of the
                // profile.
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    _recoveredAt = DateTime.UtcNow;
                    _view?.Dispose();
                    _view = null;
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    if (!_closing) await StartPageAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            LoadFailed?.Invoke(exception.Message);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowStyleService.SetToolWindow(this, noActivate: true);
        WindowStyleService.MoveOffScreen(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // A monitor added or moved may reach where the page waits.
        if (message == NativeMethods.WmDisplayChange && !_revealed)
        {
            Dispatcher.InvokeAsync(() => WindowStyleService.MoveOffScreen(this));
        }

        return IntPtr.Zero;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // The page out of sight must never hold the keyboard: whatever activates it hands the focus back to the overlay,
        // so the user's typing never lands in ChatGPT.
        if (!_revealed && Application.Current.MainWindow is { IsVisible: true } overlay && overlay != this) overlay.Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        // Closing only puts the page out of the way: it stays loaded and signed in for LINK.
        e.Cancel = true;
        Dispatcher.InvokeAsync(Conceal);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A minimized page would be throttled like a hidden one; minimizing puts it out of the way instead.
        if (WindowState == WindowState.Minimized) Dispatcher.InvokeAsync(Conceal);
    }

    /// <summary>
    /// Takes the page out of the user's way. It is hidden for a moment, so that Windows hands the focus to the window
    /// behind, and shown again off screen, where it cannot be activated.
    /// </summary>
    private void Conceal()
    {
        if (_closing) return;
        _revealed = false;
        ShowPage();
        Hide();
        WindowState = WindowState.Normal;
        WindowStyleService.SetToolWindow(this, noActivate: true);
        WindowStyleService.MoveOffScreen(this);
        Show();
    }
}
