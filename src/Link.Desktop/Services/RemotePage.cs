using System.Collections.Concurrent;

namespace Link.Desktop.Services;

/// <summary>
/// The remote page's files, built into Link from <c>remote/</c> so that they always match its API; the icon is the 256 px
/// PNG of Link's own icon.
/// </summary>
internal static class RemotePage
{
    private static readonly IReadOnlyDictionary<string, (string Name, string Type)> Files = new Dictionary<string, (string, string)>
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
        ["/app.css"] = ("app.css", "text/css; charset=utf-8"),
        ["/sw.js"] = ("sw.js", "text/javascript; charset=utf-8"),
        ["/manifest.webmanifest"] = ("manifest.webmanifest", "application/manifest+json"),
        ["/icon.png"] = ("Link.ico", "image/png")
    };

    private static readonly ConcurrentDictionary<string, byte[]> Loaded = new();

    /// <summary>The file at a path with its type; null for none.</summary>
    public static (string Type, byte[] Bytes)? File(string path) =>
        Files.TryGetValue(path, out var file) ? (file.Type, Loaded.GetOrAdd(file.Name, Load)) : null;

    private static byte[] Load(string name)
    {
        using var stream = typeof(RemotePage).Assembly.GetManifestResourceStream($"Remote.{name}") ??
                           throw new InvalidOperationException($"The remote page's {name} is not built in");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        return name.EndsWith(".ico", StringComparison.Ordinal) ? LargestPng(bytes) : bytes;
    }

    /// <summary>The PNG frame of an icon file: only its 256 px one is stored as PNG.</summary>
    private static byte[] LargestPng(byte[] icon)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47];
        var png = Enumerable.Range(0, BitConverter.ToUInt16(icon, 4)).Select(index => 6 + 16 * index)
            .Select(entry => (Size: BitConverter.ToInt32(icon, entry + 8), Offset: BitConverter.ToInt32(icon, entry + 12)))
            .First(frame => icon.AsSpan(frame.Offset, 4).SequenceEqual(signature));
        return icon[png.Offset..(png.Offset + png.Size)];
    }
}
