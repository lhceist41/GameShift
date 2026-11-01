using GameShift.Core.Config;

namespace GameShift.Core.Presentation;

/// <summary>
/// The set of theme tokens the app loads.
/// </summary>
public enum ThemeVariant
{
    Dark,
    Light,
    HighContrast,
}

/// <summary>
/// Decides the theme from the user's choice and the Windows settings. Pure: the App reads Windows
/// and applies the result.
/// </summary>
public static class ThemeChoice
{
    public static ThemeVariant Resolve(AppTheme setting, bool windowsAppsUseLightTheme, bool highContrast)
    {
        // High contrast is the user's accessibility setting for all of Windows, so it wins over
        // any choice made in GameShift.
        if (highContrast)
            return ThemeVariant.HighContrast;

        return setting switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => windowsAppsUseLightTheme ? ThemeVariant.Light : ThemeVariant.Dark,
        };
    }

    /// <summary>
    /// Reads the AppsUseLightTheme registry value. Windows uses the light app theme while the value
    /// is absent, so null means light; any value that is not a number means dark.
    /// </summary>
    public static bool AppsUseLightTheme(object? registryValue) =>
        registryValue is int value ? value != 0 : registryValue is null;
}
