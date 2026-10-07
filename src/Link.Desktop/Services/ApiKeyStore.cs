using System.Security.Cryptography;
using System.Text;

namespace Link.Desktop.Services;

/// <summary>
/// An API key (Gemini's, the custom model's), encrypted for this Windows user with DPAPI in its own file: never in
/// settings.json, the log or a request other than its own service's. Each key has its own entropy, so that one file
/// cannot be read as another's.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly byte[] _entropy;
    private readonly string _path;
    private readonly Lock _lock = new();
    private string? _key;
    private bool _loaded;

    public ApiKeyStore(string path, string entropy)
    {
        _path = path;
        _entropy = Encoding.UTF8.GetBytes(entropy);
    }

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
                        ProtectedData.Unprotect(File.ReadAllBytes(_path), _entropy, DataProtectionScope.CurrentUser));
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
        var protectedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), _entropy, DataProtectionScope.CurrentUser);
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
