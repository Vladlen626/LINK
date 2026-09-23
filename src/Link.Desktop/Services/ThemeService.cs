using System.Windows;
using Link.Desktop.Models;
using Microsoft.Win32;

namespace Link.Desktop.Services;

public static class ThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }

    // Windows' "Transparency effects" switch; when it is off Link draws solid glass.
    public static bool IsTransparencyEnabled => ReadPersonalizeFlag("EnableTransparency", fallback: true);

    // Must run before the first window is created: every window resolves theme brushes as StaticResource.
    public static void Apply(Application application, AppSettings settings)
    {
        IsDark = settings.Theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => !ReadPersonalizeFlag("AppsUseLightTheme", fallback: true)
        };

        var dictionaries = application.Resources.MergedDictionaries;
        dictionaries.Add(Load(IsDark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml"));
        dictionaries.Add(Load("Themes/Controls.xaml"));
    }

    private static ResourceDictionary Load(string path) => new() { Source = new Uri(path, UriKind.Relative) };

    private static bool ReadPersonalizeFlag(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
        return key?.GetValue(name) is int value ? value != 0 : fallback;
    }
}
