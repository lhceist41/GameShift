using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using GameShift.Core.Config;
using GameShift.Core.Presentation;
using Microsoft.Win32;
using Serilog;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GameShift.App.Services;

/// <summary>
/// Applies the GameShift theme and follows Windows when the setting is "Use Windows setting" or
/// high contrast is on. Swaps the token dictionary, then reloads the alias layer and the WPF-UI
/// overrides: their StaticResource entries resolve when they load, so they must load after the tokens.
/// </summary>
/// <remarks>
/// Windows changes arrive through SystemEvents rather than WPF-UI's SystemThemeWatcher, which is bound
/// to one window and switches only WPF-UI's own dictionaries.
/// </remarks>
internal static class ThemeService
{
    private const string ThemesFolder = "pack://application:,,,/GameShift.App;component/Themes/";
    private const string AliasFile = "GameShiftTheme.xaml";
    private const string OverridesFile = "WpfUiOverrides.xaml";
    private const string WpfUiThemeFolder = "/Wpf.Ui;component/Resources/Theme/";

    private static AppTheme _setting = AppTheme.System;
    private static ThemeVariant? _applied;
    private static bool _watching;
    private static ResourceDictionary? _tokens;
    private static ResourceDictionary? _aliases;
    private static ResourceDictionary? _overrides;

    /// <summary>
    /// Applies <paramref name="setting"/> now and keeps following Windows. Falls back to the dark theme
    /// if another theme cannot be loaded. Must be called on the UI thread.
    /// </summary>
    public static void Apply(AppTheme setting)
    {
        _setting = setting;
        var variant = Resolve();
        try
        {
            ApplyVariant(variant);
        }
        catch (Exception ex) when (variant != ThemeVariant.Dark)
        {
            Log.Error(ex, "Theme {Theme} could not be applied, using the dark theme", variant);
            ApplyVariant(ThemeVariant.Dark);
        }

        if (!_watching)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            _watching = true;
        }
    }

    private static ThemeVariant Resolve() =>
        ThemeChoice.Resolve(_setting, ReadAppsUseLightTheme(), SystemParameters.HighContrast);

    private static bool ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return ThemeChoice.AppsUseLightTheme(key?.GetValue("AppsUseLightTheme"));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the Windows app theme, using dark");
            return false;
        }
    }

    private static void ApplyVariant(ThemeVariant variant)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        _tokens = Replace(merged, _tokens, new ResourceDictionary { Source = ThemeUri($"GameShift.{variant}.xaml") });
        _aliases = Replace(merged, _aliases, LoadLoose(AliasFile));
        _overrides = Replace(merged, _overrides, LoadLoose(OverridesFile));

        var wpfUiTheme = variant switch
        {
            ThemeVariant.Light => ApplicationTheme.Light,
            ThemeVariant.HighContrast => ApplicationTheme.HighContrast,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(wpfUiTheme, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply((Color)Application.Current.Resources["GS.Color.Interactive"], wpfUiTheme);

        _applied = variant;
        Log.Information("Theme {Theme} applied (setting {Setting})", variant, _setting);
        LogUnknownOverrideKeys(merged);
    }

    private static void Reapply()
    {
        try
        {
            var variant = Resolve();
            // High contrast takes its colours from the system palette, which can change without the variant changing.
            if (variant == _applied && variant != ThemeVariant.HighContrast)
                return;
            ApplyVariant(variant);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not follow the Windows theme change");
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
        {
            Application.Current?.Dispatcher.InvokeAsync(Reapply);
        }
    }

    private static void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Application.Current?.Dispatcher.InvokeAsync(Reapply);
    }

    private static Uri ThemeUri(string file) => new(ThemesFolder + file, UriKind.Absolute);

    /// <summary>Reads an uncompiled dictionary (see the project file), resolving its StaticResource entries now.</summary>
    private static ResourceDictionary LoadLoose(string file)
    {
        var resource = Application.GetResourceStream(ThemeUri(file))
            ?? throw new FileNotFoundException("Theme dictionary missing from the app's resources", file);
        using var stream = resource.Stream;
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    /// <summary>Puts <paramref name="replacement"/> where <paramref name="current"/> was, or last.</summary>
    private static ResourceDictionary Replace(IList<ResourceDictionary> merged, ResourceDictionary? current, ResourceDictionary replacement)
    {
        var index = current == null ? -1 : merged.IndexOf(current);
        if (index >= 0)
            merged[index] = replacement;
        else
            merged.Add(replacement);
        return replacement;
    }

    /// <summary>
    /// Logs override keys the loaded WPF-UI theme does not define: after a WPF-UI update renames a key,
    /// the override no longer restyles anything and the stock colour returns.
    /// </summary>
    [Conditional("DEBUG")]
    private static void LogUnknownOverrideKeys(IList<ResourceDictionary> merged)
    {
        var overrides = _overrides;
        var wpfUiTheme = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains(WpfUiThemeFolder, StringComparison.OrdinalIgnoreCase) == true);
        if (overrides == null || wpfUiTheme == null)
        {
            Log.Warning("Theme check: WPF-UI theme or override dictionary not found");
            return;
        }

        foreach (var key in overrides.Keys)
        {
            if (!Defines(wpfUiTheme, key))
                Log.Warning("Theme check: WPF-UI does not define override key {Key}", key);
        }
    }

    private static bool Defines(ResourceDictionary dictionary, object key) =>
        dictionary.Contains(key) || dictionary.MergedDictionaries.Any(d => Defines(d, key));
}
