using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace Link.Desktop.Services;

public sealed class ScreenshotHistoryService
{
    private readonly StoragePaths _paths;
    private readonly SettingsService _settings;
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public ScreenshotHistoryService(StoragePaths paths, SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
        Prune();
    }

    public ScreenshotResult Save(Bitmap bitmap, string source, bool prune = true)
    {
        var path = CreatePath(source);
        bitmap.Save(path, ImageFormat.Png);
        if (prune) Prune();
        return new ScreenshotResult(path, LoadPreview(path), source);
    }

    public string SaveClipboardImage(BitmapSource bitmap)
    {
        var path = CreatePath("clipboard");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        Prune();
        return path;
    }

    public void Pin(string path)
    {
        lock (_sync) _pinned.Add(Path.GetFullPath(path));
    }

    public void Unpin(string path)
    {
        lock (_sync) _pinned.Remove(Path.GetFullPath(path));
    }

    public void Prune(IEnumerable<string>? additionallyProtected = null)
    {
        HashSet<string> protectedPaths;
        lock (_sync)
        {
            protectedPaths = new HashSet<string>(_pinned, StringComparer.OrdinalIgnoreCase);
        }

        if (additionallyProtected is not null)
        {
            protectedPaths.UnionWith(additionallyProtected.Select(Path.GetFullPath));
        }

        var files = new DirectoryInfo(_paths.Screenshots)
            .EnumerateFiles("*.png")
            .OrderByDescending(file => file.CreationTimeUtc)
            .Where(file => !protectedPaths.Contains(file.FullName))
            .Skip(_settings.Current.ScreenshotHistorySize)
            .ToArray();

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // A preview can briefly hold a file; it will be pruned next time.
            }
        }
    }

    private string CreatePath(string source) =>
        Path.Combine(
            _paths.Screenshots,
            $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{source}-{Guid.NewGuid():N}.png");

    private static BitmapImage LoadPreview(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 640;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public sealed record ScreenshotResult(string FilePath, BitmapImage Preview, string Source);
