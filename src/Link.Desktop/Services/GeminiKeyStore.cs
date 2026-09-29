using System.Security.Cryptography;
using System.Text;

namespace Link.Desktop.Services;

/// <summary>
/// The Gemini API key, encrypted for this Windows user with DPAPI in its own file: never in settings.json,
/// the log or a request other than Gemini's own.
/// </summary>
public sealed class GeminiKeyStore
{
    private static readonly byte[] Entropy = "Link Gemini TTS"u8.ToArray();
    private readonly string _path;
    private readonly Lock _lock = new();
    private string? _key;
    private bool _loaded;

    public GeminiKeyStore(string path) => _path = path;

    /// <summary>Changes with every save or removal, so that a rejected key is tried again once replaced.</summary>
    public int Version { get; private set; }

    public bool HasKey => Read() is not null;

    public string? Read()
    {
        lock (_lock)
        {
            if (_loaded) return _key;
            _loaded = true;
            try
            {
                if (File.Exists(_path))
                {
                    var key = Encoding.UTF8.GetString(
                        ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser));
                    _key = key.Length > 0 ? key : null;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // A key saved by another Windows user or a damaged file reads as no key; saving a new one replaces it.
                _key = null;
            }

            return _key;
        }
    }

    public void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("The key is empty.", nameof(key));
        var protectedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        lock (_lock)
        {
            var temporaryPath = _path + ".tmp";
            File.WriteAllBytes(temporaryPath, protectedKey);
            File.Move(temporaryPath, _path, overwrite: true);
            _key = key;
            _loaded = true;
            Version++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path)) File.Delete(_path);
            _key = null;
            _loaded = true;
            Version++;
        }
    }
}
