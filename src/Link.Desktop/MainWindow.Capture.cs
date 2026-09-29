using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private bool _selectingRegion;
    private Guid? _regionContextId;

    // While the region selector is open another capture would photograph its dimmed overlay.
    private bool CanCapture => !_sending && !_selectingRegion;

    public async Task CaptureCurrentMonitorAsync()
    {
        if (!CanCapture) return;
        await CaptureWithOverlayHiddenAsync(() => [_capture.CaptureCurrentMonitor()]);
    }

    public async Task CaptureDesktopAsync()
    {
        if (!CanCapture) return;
        await CaptureWithOverlayHiddenAsync(() => [_capture.CaptureDesktop()]);
    }

    public async Task CaptureAllMonitorsAsync()
    {
        if (!CanCapture) return;
        await CaptureWithOverlayHiddenAsync(() => _capture.CaptureAllMonitors());
    }

    public async Task CaptureActiveWindowAsync()
    {
        if (!CanCapture) return;
        var target = _currentApplication.WorkTarget;
        await CaptureWithOverlayHiddenAsync(() => [_capture.CaptureActiveWindow(target)]);
    }

    public async Task CaptureRegionAsync()
    {
        // A second hotkey press while the selector is open is ignored instead of stacking selectors.
        if (!CanCapture) return;
        _selectingRegion = true;
        var wasVisible = IsVisible;
        if (wasVisible) Hide();
        await Task.Delay(80);

        try
        {
            var selector = new RegionSelectionWindow();
            if (selector.ShowDialog() == true && selector.SelectedRegion is { } region)
            {
                var result = _capture.CaptureRegion(region);
                RemoveMissingContextFiles();
                ReplaceRegionContext(AddScreenshot(result));
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
            PresentAfterCapture(wasVisible);
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

    private async Task CaptureWithOverlayHiddenAsync(Func<IReadOnlyList<ScreenshotResult>> capture)
    {
        var wasVisible = IsVisible;
        if (wasVisible) Hide();
        await Task.Delay(80);

        try
        {
            var results = capture();
            RemoveMissingContextFiles();
            foreach (var result in results) AddScreenshot(result);
            SetStatus(results.Count == 1 ? "Screenshot captured" : $"Captured {results.Count} monitors");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            PresentAfterCapture(wasVisible);
        }
    }

    /// <summary>A screenshot of the work window for a message's snapshot; it stays out of the context stack.</summary>
    private async Task<ContextItem> CaptureWorkWindowAsync(IntPtr windowHandle)
    {
        var wasVisible = IsVisible;
        var wasActive = IsActive;
        if (wasVisible) Hide();
        await Task.Delay(80);

        try
        {
            var screenshot = _capture.CaptureWindow(windowHandle, prune: false);
            return new ContextItem
            {
                Type = ContextItemType.Screenshot,
                Label = "Current application",
                FilePath = screenshot.FilePath,
                MimeType = "image/png"
            };
        }
        finally
        {
            // Sending leaves the user where they were: a Send hotkey from another app keeps that app in front.
            if (wasActive) ShowAndActivate();
            else if (wasVisible) ShowPassive();
        }
    }

    private void PresentAfterCapture(bool wasVisible)
    {
        if (wasVisible) ShowAndActivate();
        else ShowPassive();
    }

    private ContextItem AddScreenshot(ScreenshotResult result)
    {
        var label = result.Source switch
        {
            "desktop" => "Screen",
            "window" => "Window",
            "region" => "Region",
            "monitor" => "Monitor",
            _ when result.Source.StartsWith("monitor-", StringComparison.Ordinal) => result.Source.Replace("monitor-", "Monitor "),
            _ => "Screenshot"
        };
        var item = new ContextItem
        {
            Type = ContextItemType.Screenshot,
            Label = label,
            FilePath = result.FilePath,
            MimeType = "image/png"
        };
        AddContext(item);
        _previewContextId = item.Id;
        ScreenshotPreview.ImageSource = result.Preview;
        PreviewLabel.Text = label;
        PreviewPanel.Visibility = Visibility.Visible;
        return item;
    }
}
