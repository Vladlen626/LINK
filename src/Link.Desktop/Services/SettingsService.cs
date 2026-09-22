using System.Text.Json;
using Link.Desktop.Models;
using Microsoft.Win32;

namespace Link.Desktop.Services;

public sealed class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Link";
    private readonly StoragePaths _paths;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private bool _savedLaunchWithWindows;

    public SettingsService(StoragePaths paths)
    {
        _paths = paths;
        Current = Load();
        _savedLaunchWithWindows = Current.LaunchWithWindows;
    }

    public AppSettings Current { get; private set; }

    public void Save()
    {
        Current.Normalize();
        if (Current.LaunchWithWindows != _savedLaunchWithWindows)
        {
            ApplyLaunchWithWindows(Current.LaunchWithWindows);
            _savedLaunchWithWindows = Current.LaunchWithWindows;
        }
        File.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(Current, _json));
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile));
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (JsonException)
        {
            // Invalid settings are replaced with safe defaults.
        }

        return new AppSettings();
    }

    private static void ApplyLaunchWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, false);
        }
    }
}
