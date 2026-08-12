using System;
using System.Windows;

namespace AmongLauncher.Config;

public static class ThemeManager
{
    private const string DarkThemeUri = "Themes/DarkTheme.xaml";
    private const string LightThemeUri = "Themes/LightTheme.xaml";

    private static ResourceDictionary? _currentTheme;

    public static void ApplyTheme(string themeName)
    {
        var app = Application.Current;
        if (app == null) return;

        if (_currentTheme != null)
        {
            app.Resources.MergedDictionaries.Remove(_currentTheme);
        }

        var uri = themeName == "Light"
            ? new Uri(LightThemeUri, UriKind.Relative)
            : new Uri(DarkThemeUri, UriKind.Relative);

        _currentTheme = new ResourceDictionary { Source = uri };
        app.Resources.MergedDictionaries.Add(_currentTheme);
    }

    public static void LoadSavedTheme()
    {
        var config = LauncherConfig.Load();
        ApplyTheme(config.Theme);
    }
}
