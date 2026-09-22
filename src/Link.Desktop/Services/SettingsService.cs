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
        AtomicJsonFile.Write(_paths.SettingsFile, Current, _json);
    }

    private AppSettings Load()
    {
        var settings = AtomicJsonFile.Read<AppSettings>(_paths.SettingsFile, _json);
        if (settings is not null)
        {
            settings.Normalize();
            return settings;
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
