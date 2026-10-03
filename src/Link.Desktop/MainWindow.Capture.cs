using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class MainWindow
{
    // The work window's picture in the Sees line is taken again for another window or after this long.
    private static readonly TimeSpan PreviewAge = TimeSpan.FromSeconds(5);
    private const int PreviewWidth = 160;
    private bool _selectingRegion;
    private Guid? _regionContextId;
    // The work window's last picture for the Sees line: in memory only, never saved or sent.
    private (IntPtr Window, DateTime TakenAt, ImageSource Picture)? _workPreview;
    private bool _previewing;
    // The latest attached screenshot's picture, so the Sees line does not decode the file again.
    private (string Path, ImageSource Picture)? _shotPicture;
    // The frame the answer on screen was asked with, opened from its "saw" line.
    private string? _sawFrame;

    // While the region selector is open another capture would photograph its dimmed overlay.
    private bool CanCapture => !_sending && !_selectingRegion;

    // Auto active window: a fresh frame of the work window goes with each question; "only my text" sends none.
    private bool AutoFrameOn => _settings.Current.AutoCaptureActiveWindowOnSend && !_onlyMyText;

    public async Task CaptureCurrentMonitorAsync()
    {
        if (!CanCapture) return;
        await CaptureWithOverlayHiddenAsync(async () => [await _capture.CaptureCurrentMonitorAsync()]);
    }

    public async Task CaptureDesktopAsync()
    {
        if (!CanCapture) return;
        await CaptureWithOverlayHiddenAsync(async () => [await _capture.CaptureDesktopAsync()]);
    }

    public async Task CaptureAllMonitorsAsync()
    {
        if (!CanCapture) return;
        await CaptureWithOverlayHiddenAsync(_capture.CaptureAllMonitorsAsync);
    }

    /// <summary>
    /// The window the user works in, as it draws itself: Link stays where it is, the focus stays where it was, and the
    /// window gets a short outline. Hidden, Link comes up without taking the focus to show what it took.
    /// </summary>
    public async Task CaptureActiveWindowAsync()
    {
        if (!CanCapture) return;
        var target = _currentApplication.WorkTarget;
        try
        {
            if (!_currentApplication.IsExternalWindow(target))
            {
                throw new InvalidOperationException("No window to capture yet: press the hotkey in the window you mean.");
            }

            var result = await _capture.CaptureWindowAsync(target);
            RemoveMissingContextFiles();
            AddScreenshot(result, WindowLabel(target));
            Outline(target);
            SetStatus("Window captured");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }

        ShowPassive();
    }

    public async Task CaptureRegionAsync()
    {
        // A second hotkey press while the selector is open is ignored instead of stacking selectors.
        if (!CanCapture) return;
        _selectingRegion = true;
        var wasVisible = IsVisible;
        var wasActive = IsActive;
        if (wasVisible) Hide();
        await Task.Delay(80);

        try
        {
            var selector = new RegionSelectionWindow();
            if (selector.ShowDialog() == true && selector.SelectedRegion is { } region)
            {
                // The selector's dimmed overlay leaves the screen with the next frame.
                await Task.Delay(50);
                var result = await _capture.CaptureRegionAsync(region);
                RemoveMissingContextFiles();
                ReplaceRegionContext(AddScreenshot(result, "Region"));
                SetStatus("Region captured");
            }
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            _selectingRegion = false;
            PresentAfterCapture(wasActive);
        }
    }

    private void ReplaceRegionContext(ContextItem region)
    {
        // Only one selected region is ever sent: a new selection replaces the previous one.
        if (_context.FirstOrDefault(item => item.Id == _regionContextId) is { } previous)
        {
            ReleaseContext(previous);
            _context.Remove(previous);
            _history.Prune();
        }

        _regionContextId = region.Id;
    }

    /// <summary>A monitor, the desktop or all monitors: Link would be in the picture, so it hides for a moment.</summary>
    private async Task CaptureWithOverlayHiddenAsync(Func<Task<IReadOnlyList<ScreenshotResult>>> capture)
    {
        var wasVisible = IsVisible;
        var wasActive = IsActive;
        if (wasVisible) Hide();
        await Task.Delay(80);

        try
        {
            var results = await capture();
            RemoveMissingContextFiles();
            foreach (var result in results) AddScreenshot(result, ScreenLabel(result.Source));
            SetStatus(results.Count == 1 ? "Screenshot captured" : $"Captured {results.Count} monitors");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            PresentAfterCapture(wasActive);
        }
    }

    /// <summary>A screenshot of the work window for a message's snapshot; it stays out of the context stack.</summary>
    private async Task<ContextItem> CaptureWorkWindowAsync(IntPtr windowHandle)
    {
        var screenshot = await _capture.CaptureWindowAsync(windowHandle, prune: false);
        Outline(windowHandle);
        return new ContextItem
        {
            Type = ContextItemType.Screenshot,
            Label = WindowLabel(windowHandle),
            FilePath = screenshot.FilePath,
            MimeType = "image/png"
        };
    }

    /// <summary>After a capture Link is back as it was: active only if it was active, so the app in front keeps its focus.</summary>
    private void PresentAfterCapture(bool wasActive)
    {
        if (wasActive) ShowAndActivate();
        else ShowPassive();
    }

    /// <summary>A short outline around the captured window, unless the user turned it off or the window fills its screen.</summary>
    private void Outline(IntPtr window)
    {
        if (!_settings.Current.OutlineCapturedWindow || Fullscreen.Of(window) != FullscreenKind.None) return;
        CaptureOutline.Flash(window, (Brush)FindResource("AccentBrush"));
    }

    private string WindowLabel(IntPtr window) =>
        _currentApplication.GetContext(window)?.ProcessName is { Length: > 0 } name ? $"{name} window" : "Window";

    private static string ScreenLabel(string source) => source switch
    {
        "desktop" => "Screen",
        "monitor" => "Monitor",
        _ when source.StartsWith("monitor-", StringComparison.Ordinal) => source.Replace("monitor-", "Monitor "),
        _ => "Screenshot"
    };

    private ContextItem AddScreenshot(ScreenshotResult result, string label)
    {
        var item = new ContextItem
        {
            Type = ContextItemType.Screenshot,
            Label = label,
            FilePath = result.FilePath,
            MimeType = "image/png"
        };
        AddContext(item);
        _shotPicture = (result.FilePath, result.Preview);
        UpdateSees();
        return item;
    }

    /// <summary>
    /// The Sees line says what the model will see of the screen with the next question: the screenshots attached, the
    /// work window's fresh frame that Auto takes on Send, or nothing. Screenshots show here, not among the chips.
    /// </summary>
    private void UpdateSees()
    {
        if (!IsInitialized) return;
        var shots = _context.Where(item => item.Type == ContextItemType.Screenshot && item.FilePath is not null).ToArray();
        var window = _currentApplication.WorkTarget;
        var app = _currentApplication.GetContext(window)?.ProcessName;
        RemoveScreensButton.Visibility = shots.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shots.Length > 0)
        {
            var latest = shots[^1];
            SeesTitle.Text = shots.Length == 1 ? latest.Label : $"{shots.Length} screenshots · {latest.Label} last";
            SeesDetail.Text = AutoFrameOn && app is not null ? $"and a fresh frame of {app} on send" : "go with the question";
            ShowSeesPicture(ShotPicture(latest.FilePath!));
        }
        else if (AutoFrameOn && app is not null)
        {
            SeesTitle.Text = $"{app} · window";
            SeesDetail.Text = "a fresh frame on send";
            ShowSeesPicture(_workPreview is { } preview && preview.Window == window ? preview.Picture : null);
        }
        else
        {
            SeesTitle.Text = AutoFrameOn ? "The window you work in" : "Sees nothing";
            SeesDetail.Text = AutoFrameOn ? "a fresh frame on send, once Link knows the window" : "a window, a monitor or a region";
            ShowSeesPicture(null);
        }

        var hotkeys = _settings.Current.Hotkeys;
        SeesButton.ToolTip = $"What the model sees of the screen · window {hotkeys.CaptureActiveWindow}, desktop " +
                             $"{hotkeys.CaptureDesktop}, region {hotkeys.CaptureRegion}, ask about it {hotkeys.AskAboutScreen}";
    }

    private void ShowSeesPicture(ImageSource? picture)
    {
        SeesImage.ImageSource = picture;
        SeesPicture.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private ImageSource? ShotPicture(string path)
    {
        if (_shotPicture is { } kept && kept.Path == path) return kept.Picture;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = PreviewWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _shotPicture = (path, image);
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// With Auto on, the Sees line shows a small picture of the work window, taken when Link comes up: what the fresh
    /// frame on Send will be like. In memory only, neither saved nor sent; taken again only for another window or after
    /// a few seconds. A game in exclusive fullscreen is left alone.
    /// </summary>
    private async void RefreshWorkPreview()
    {
        var window = _currentApplication.WorkTarget;
        if (!AutoFrameOn || _previewing || !CanCapture || !_currentApplication.IsExternalWindow(window)) return;
        if (_workPreview is { } last && last.Window == window && DateTime.UtcNow - last.TakenAt < PreviewAge) return;
        if (Fullscreen.Of(window) == FullscreenKind.Exclusive) return;
        _previewing = true;
        try
        {
            var picture = await _capture.PreviewWindowAsync(window, PreviewWidth);
            _workPreview = picture is null ? null : (window, DateTime.UtcNow, picture);
        }
        finally
        {
            _previewing = false;
        }

        UpdateSees();
    }

    private void Sees_OnClick(object sender, RoutedEventArgs e)
    {
        var hotkeys = _settings.Current.Hotkeys;
        var menu = new ContextMenu { PlacementTarget = SeesButton, Placement = PlacementMode.Bottom };
        AddCapture(menu, "This window now", hotkeys.CaptureActiveWindow, CaptureActiveWindowAsync);
        var auto = new MenuItem
        {
            Header = "A fresh frame of the window on every send",
            IsCheckable = true,
            IsChecked = _settings.Current.AutoCaptureActiveWindowOnSend
        };
        auto.Click += (_, _) => SetAutoFrame(auto.IsChecked);
        menu.Items.Add(auto);
        menu.Items.Add(new Separator());
        AddCapture(menu, "Current monitor", null, CaptureCurrentMonitorAsync);
        AddCapture(menu, "All monitors", null, CaptureAllMonitorsAsync);
        AddCapture(menu, "Full desktop", hotkeys.CaptureDesktop, CaptureDesktopAsync);
        AddCapture(menu, "Select region", hotkeys.CaptureRegion, CaptureRegionAsync);
        menu.IsOpen = true;
    }

    private static void AddCapture(ContextMenu menu, string header, string? hotkey, Func<Task> capture)
    {
        var item = new MenuItem { Header = header, InputGestureText = hotkey ?? string.Empty };
        item.Click += async (_, _) => await capture();
        menu.Items.Add(item);
    }

    private void SetAutoFrame(bool on)
    {
        _settings.Current.AutoCaptureActiveWindowOnSend = on;
        _settings.Save();
        UpdateSees();
        RefreshWorkPreview();
        SetStatus(on ? "A fresh frame of the window goes with every question" : "No frame goes along unless you attach one");
    }

    private void RemoveScreens_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _context.Where(item => item.Type == ContextItemType.Screenshot).ToArray())
        {
            ReleaseContext(item);
            _context.Remove(item);
        }

        _shotPicture = null;
        UpdateSees();
        _history.Prune();
    }

    /// <summary>
    /// The app the user works in stands in the context stack as a chip: its name and window title go with a Chat
    /// question. Removed, they stay out of the next question only: the chip is back once it is answered or Link means
    /// another window. "Only my text" does not send them, and an agent's task does not carry them.
    /// </summary>
    private void ShowApplicationContext()
    {
        foreach (var old in _context.Where(item => item.Type == ContextItemType.Application).ToArray()) _context.Remove(old);
        if (_currentApplication.GetContext(_currentApplication.WorkTarget) is not { } target ||
            _onlyMyText || _settings.Current.Route == RequestRoute.Agent)
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(target.WindowTitle) ? target.ProcessName : $"{target.ProcessName} · {target.WindowTitle}";
        // First, before what the user attached.
        _context.Insert(0, new ContextItem { Type = ContextItemType.Application, Label = label });
    }

    /// <summary>Link means another window now: its chip, and its picture in the Sees line.</summary>
    private void OnWorkTargetChanged()
    {
        ShowApplicationContext();
        UpdateSees();
        if (IsVisible) RefreshWorkPreview();
    }

    /// <summary>A game in exclusive fullscreen is in front: a window over it would throw it out of full screen.</summary>
    private static bool OverExclusiveFullscreen() =>
        Fullscreen.Of(NativeMethods.GetForegroundWindow()) == FullscreenKind.Exclusive;

    /// <summary>What went with the question, from its request as sent: the frame it carried and Link's chips.</summary>
    private void ShowSaw(PromptRequest? request)
    {
        _sawFrame = request?.Context.LastOrDefault(item => item.Type == ContextItemType.Screenshot && item.FilePath is not null)?.FilePath;
        if (request is null)
        {
            SawRow.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = request.Context.Select(item => item.Label).ToList();
        if (request.CurrentApplication is { } app) parts.Add($"App: {app.ProcessName}");
        SawText.Text = parts.Count == 0 ? "Saw only your text" : "Saw: " + string.Join(" · ", parts);
        SawText.ToolTip = SawText.Text;
        SawImage.ImageSource = _sawFrame is null ? null : ShotPicture(_sawFrame);
        SawPictureButton.Visibility = SawImage.ImageSource is null ? Visibility.Collapsed : Visibility.Visible;
        SawRow.Visibility = Visibility.Visible;
    }

    private void SawPicture_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sawFrame is null || !File.Exists(_sawFrame))
        {
            SetStatus("That frame is no longer kept");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_sawFrame) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }
}
