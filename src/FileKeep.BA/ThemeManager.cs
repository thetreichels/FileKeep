using Microsoft.Win32;
using System.Windows;

namespace FileKeep.BA;

/// <summary>
/// Detects the Windows system theme (light/dark) and applies the
/// corresponding resource dictionary. Follows the user's
/// Settings > Personalization > Colors > "Choose your mode" setting.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>True if the system is in dark mode for apps.</summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(AppsUseLightThemeValue) is int v)
                return v == 0;
        }
        catch
        {
            // Fall back to light on any error.
        }
        return false;
    }

    /// <summary>
    /// Applies the theme matching the current system setting.
    /// Call on startup and on WM_SETTINGCHANGE.
    /// </summary>
    public static void ApplySystemTheme()
    {
        bool dark = IsDarkMode();
        var dict = new ResourceDictionary
        {
            Source = new Uri(
                dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
                UriKind.Relative)
        };

        // Replace the theme dictionary (first one) while keeping others.
        var appDicts = Application.Current.Resources.MergedDictionaries;
        if (appDicts.Count > 0 &&
            appDicts[0].Source?.OriginalString.Contains("Theme.xaml") == true)
        {
            appDicts[0] = dict;
        }
        else
        {
            appDicts.Insert(0, dict);
        }
    }
}
