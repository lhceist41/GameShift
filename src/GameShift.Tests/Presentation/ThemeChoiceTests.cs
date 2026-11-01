using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

public class ThemeChoiceTests
{
    [Theory]
    [InlineData(false, ThemeVariant.Dark)]
    [InlineData(true, ThemeVariant.Light)]
    public void UseWindowsSetting_FollowsTheWindowsAppTheme(bool windowsLight, ThemeVariant expected)
    {
        Assert.Equal(expected, ThemeChoice.Resolve(AppTheme.System, windowsLight, highContrast: false));
    }

    [Theory]
    [InlineData(AppTheme.Dark, true, ThemeVariant.Dark)]
    [InlineData(AppTheme.Light, false, ThemeVariant.Light)]
    [InlineData(AppTheme.Dark, false, ThemeVariant.Dark)]
    [InlineData(AppTheme.Light, true, ThemeVariant.Light)]
    public void ExplicitChoice_WinsOverWindows(AppTheme setting, bool windowsLight, ThemeVariant expected)
    {
        Assert.Equal(expected, ThemeChoice.Resolve(setting, windowsLight, highContrast: false));
    }

    [Theory]
    [InlineData(AppTheme.System, false)]
    [InlineData(AppTheme.System, true)]
    [InlineData(AppTheme.Dark, false)]
    [InlineData(AppTheme.Light, true)]
    public void HighContrast_WinsOverEveryChoice(AppTheme setting, bool windowsLight)
    {
        Assert.Equal(ThemeVariant.HighContrast, ThemeChoice.Resolve(setting, windowsLight, highContrast: true));
    }

    [Theory]
    [InlineData(false, ThemeVariant.Dark)]
    [InlineData(true, ThemeVariant.Light)]
    public void UnknownSetting_ActsAsUseWindowsSetting(bool windowsLight, ThemeVariant expected)
    {
        Assert.Equal(expected, ThemeChoice.Resolve((AppTheme)7, windowsLight, highContrast: false));
    }

    [Fact]
    public void AppsUseLightTheme_ReadsTheRegistryValue()
    {
        Assert.True(ThemeChoice.AppsUseLightTheme(1));
        Assert.False(ThemeChoice.AppsUseLightTheme(0));
        Assert.True(ThemeChoice.AppsUseLightTheme(null)); // absent: Windows uses the light app theme
        Assert.False(ThemeChoice.AppsUseLightTheme("1")); // not a DWORD
    }
}

public class AppThemeSettingTests
{
    [Fact]
    public void SettingsWithoutAppTheme_LoadAsUseWindowsSetting()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "StartWithWindows": false }""")!;

        Assert.Equal(AppTheme.System, settings.AppTheme);
        Assert.Equal(AppTheme.System, new AppSettings().AppTheme);
    }

    [Theory]
    [InlineData(AppTheme.System, 0)]
    [InlineData(AppTheme.Dark, 1)]
    [InlineData(AppTheme.Light, 2)]
    public void AppTheme_IsSavedAsItsStableNumber(AppTheme theme, int stored)
    {
        var json = JsonSerializer.Serialize(new AppSettings { AppTheme = theme });

        Assert.Equal(stored, JsonDocument.Parse(json).RootElement.GetProperty("AppTheme").GetInt32());
        Assert.Equal(theme, JsonSerializer.Deserialize<AppSettings>(json)!.AppTheme);
    }
}
