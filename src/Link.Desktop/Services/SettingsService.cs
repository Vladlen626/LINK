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
        // A damaged file is not a first start: its owner has been here.
        FirstStart = !File.Exists(paths.SettingsFile);
        Current = Load();
        _savedLaunchWithWindows = Current.LaunchWithWindows;
    }

    /// <summary>The live settings: one instance for the app's lifetime, edited in place and saved.</summary>
    public AppSettings Current { get; }

    /// <summary>This is the very first start: there was no settings file when Link began.</summary>
    public bool FirstStart { get; }

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
