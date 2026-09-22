using System.Runtime.InteropServices;
using System.Windows;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

public sealed class ClipboardContextService
{
    private readonly ScreenshotHistoryService _history;

    public ClipboardContextService(ScreenshotHistoryService history) => _history = history;

    public ContextItem Read()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                return new ContextItem
                {
                    Type = ContextItemType.Clipboard,
                    Label = "Clipboard",
                    Text = text.Length > 50_000 ? text[..50_000] : text,
                    MimeType = "text/plain"
                };
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage() ?? throw new InvalidOperationException("Clipboard image is unavailable.");
                return new ContextItem
                {
                    Type = ContextItemType.Clipboard,
                    Label = "Clipboard image",
                    FilePath = _history.SaveClipboardImage(image),
                    MimeType = "image/png"
                };
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Take(20);
                return new ContextItem
                {
                    Type = ContextItemType.Clipboard,
                    Label = "Clipboard files",
                    Text = string.Join(Environment.NewLine, files),
                    MimeType = "text/uri-list"
                };
            }
        }
        catch (ExternalException exception)
        {
            throw new InvalidOperationException("Clipboard is currently busy. Try again.", exception);
        }

        throw new InvalidOperationException("Clipboard does not contain supported text, image, or files.");
    }
}
