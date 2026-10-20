namespace Link.Desktop.Services;

/// <summary>Pictures kept as files, such as those sent to the Telegram bot: their format is read from their bytes, not taken on trust.</summary>
internal static class ImageFiles
{
    /// <summary>The file extension of a PNG, JPEG, GIF or WebP picture; null for anything else.</summary>
    public static string? Extension(ReadOnlySpan<byte> data) =>
        data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? ".png"
        : data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg"
        : data.StartsWith("GIF8"u8) ? ".gif"
        : data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : null;

    /// <summary>Saves a picture into the folder under a new name: when it came, where from, its format's extension.</summary>
    public static string Save(string folder, byte[] data, string source)
    {
        var extension = Extension(data) ?? throw new InvalidDataException("This is not a picture LINK keeps.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{source}-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, data);
        return path;
    }
}
