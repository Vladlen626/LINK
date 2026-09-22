using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    public async Task CaptureCurrentMonitorAsync() =>
        await CaptureWithOverlayHiddenAsync(() => [_capture.CaptureCurrentMonitor()]);

    public async Task CaptureDesktopAsync() =>
        await CaptureWithOverlayHiddenAsync(() => [_capture.CaptureDesktop()]);

    public async Task CaptureAllMonitorsAsync() =>
        await CaptureWithOverlayHiddenAsync(() => _capture.CaptureAllMonitors());

    public async Task CaptureActiveWindowAsync()
    {
        var fallback = IsActive ? _lastExternalWindow : IntPtr.Zero;
        await CaptureWithOverlayHiddenAsync(() => [_capture.CaptureActiveWindow(fallback)]);
    }

    public async Task CaptureRegionAsync()
    {
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
                AddScreenshot(result);
                StatusText.Text = "Region captured";
            }
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
            StatusText.Text = results.Count == 1 ? "Screenshot captured" : $"Captured {results.Count} monitors";
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

    private void PresentAfterCapture(bool wasVisible)
    {
        if (wasVisible) ShowAndActivate();
        else ShowPassive();
    }

    private void AddScreenshot(ScreenshotResult result)
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
        _context.Add(item);
        _previewContextId = item.Id;
        ScreenshotPreview.Source = result.Preview;
        PreviewLabel.Text = label;
        PreviewPanel.Visibility = Visibility.Visible;
    }
}
