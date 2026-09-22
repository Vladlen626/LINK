using System.Text;
using System.Text.Json;

namespace Link.Desktop.Services;

public static class AtomicJsonFile
{
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Storage path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = $"{path}.bak";

        try
        {
            var json = JsonSerializer.Serialize(value, options);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(path))
            {
                File.Move(tempPath, path);
                return;
            }

            try
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(path, backupPath, overwrite: true);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (IOException)
            {
                File.Copy(path, backupPath, overwrite: true);
                File.Move(tempPath, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        foreach (var candidate in new[] { path, $"{path}.bak" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var value = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), options);
                    if (value is not null) return value;
                }
            }
            catch (JsonException)
            {
                // Try the backup before falling back to safe defaults.
            }
            catch (IOException)
            {
                // A concurrent writer can briefly make one candidate unavailable.
            }
        }

        return default;
    }
}
