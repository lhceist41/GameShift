using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.GameProfiles;
using GameShift.Core.Presentation;
using GameShift.Core.SystemTweaks;
using Xunit;

namespace GameShift.Tests.Presentation;

public class SettingsChoicesTests
{
    [Fact]
    public void Themes_OfferEveryThemeOnce_WithALabel()
    {
        Assert.Equal(Enum.GetValues<AppTheme>().OrderBy(t => t), SettingsChoices.Themes.Select(o => o.Value).OrderBy(t => t));
        Assert.All(SettingsChoices.Themes, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
        Assert.Equal(AppTheme.System, SettingsChoices.Themes[0].Value);
    }

    [Theory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ShownTheme_KeepsAKnownChoice(AppTheme saved)
    {
        Assert.Equal(saved, SettingsChoices.ShownTheme(saved));
    }

    [Fact]
    public void ShownTheme_ShowsAnUnknownSavedNumberAsUseWindowsSetting()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"AppTheme\":7}")!;

        Assert.Equal((AppTheme)7, settings.AppTheme);
        Assert.Equal(AppTheme.System, SettingsChoices.ShownTheme(settings.AppTheme));
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("0,25")]
    [InlineData("100")]
    [InlineData("0")]
    public void ThresholdError_IsNullForAValidThreshold(string text)
    {
        Assert.Null(SettingsChoices.ThresholdError(text, 1000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("100.5")]
    [InlineData("-1")]
    public void ThresholdError_NamesTheValueSaveKeeps(string text)
    {
        var error = SettingsChoices.ThresholdError(text, 1000);

        Assert.NotNull(error);
        Assert.Contains("0 to 100", error);
        Assert.Contains("0.10%", error);
    }

    [Fact]
    public void StartupChangeRefused_NeverInTheCopyThatOwnsTheTask()
    {
        Assert.False(SettingsChoices.StartupChangeRefused(null, shownAtLoad: false, chosen: true));
        Assert.False(SettingsChoices.StartupChangeRefused(null, shownAtLoad: true, chosen: false));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StartupChangeRefused_WhenAnotherCopyOwnsTheTaskAndTheBoxChanged(bool shownAtLoad, bool chosen)
    {
        Assert.True(SettingsChoices.StartupChangeRefused(@"C:\Program Files\GameShift", shownAtLoad, chosen));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupChangeRefused_NotForAnUnchangedBox_WhateverTheStoredSettingSays(bool shownAtLoad)
    {
        // The box loads from the task; the stored setting is not an input, so an unrelated save in a
        // copy outside the install folder never reports a refusal.
        Assert.False(SettingsChoices.StartupChangeRefused(@"C:\Program Files\GameShift", shownAtLoad, chosen: shownAtLoad));
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData(@"C:\Program Files\GameShift", true, false)]
    [InlineData(@"C:\Program Files\GameShift", false, false)]
    public void SaveStatus_IsPlainWhenTheTaskMatchesTheChoice(string? installFolder, bool shownAtLoad, bool chosen)
    {
        Assert.Equal("Settings saved.", SettingsChoices.SaveStatus(installFolder, shownAtLoad, chosen, registeredAfterSave: chosen, settingsFolderRefused: false));
    }

    [Fact]
    public void SaveStatus_NamesTheInstalledCopyWhenAChangeWasRefused()
    {
        var status = SettingsChoices.SaveStatus(@"C:\Program Files\GameShift", shownAtLoad: false, chosen: true, registeredAfterSave: false, settingsFolderRefused: false);

        Assert.Equal(@"Settings saved. Start with Windows was not changed: GameShift is installed in C:\Program Files\GameShift, so change it in that copy.", status);
    }

    [Fact]
    public void SaveStatus_SaysTheBoxTurnedOff_WhenSaveRemovedATaskTheBoxStillShowed()
    {
        // A copy outside the install folder whose stored setting is off removes a leftover task for this exe.
        var status = SettingsChoices.SaveStatus(@"C:\Program Files\GameShift", shownAtLoad: true, chosen: true, registeredAfterSave: false, settingsFolderRefused: false);

        Assert.Equal("Settings saved. Start with Windows is now off in this copy.", status);
    }

    [Theory]
    [InlineData(true, "off")]
    [InlineData(false, "on")]
    public void SaveStatus_SaysWhatTheTaskIs_WhenRegistrationDidNotFollowTheChoice(bool chosen, string nowWord)
    {
        var status = SettingsChoices.SaveStatus(null, shownAtLoad: !chosen, chosen, registeredAfterSave: !chosen, settingsFolderRefused: false);

        Assert.Equal($"Settings saved. Start with Windows is now {nowWord} in this copy.", status);
    }

    private const string NotSaved =
        "Settings not saved: GameShift could not use its settings folder.";

    [Fact]
    public void SaveStatus_UnderARefusedFolder_SaysNothingWasSaved_InEachBranch()
    {
        const string installFolder = @"C:\Program Files\GameShift";

        Assert.Equal(NotSaved,
            SettingsChoices.SaveStatus(null, shownAtLoad: true, chosen: true, registeredAfterSave: true, settingsFolderRefused: true));
        Assert.Equal($"{NotSaved} Start with Windows was not changed: GameShift is installed in {installFolder}, so change it in that copy.",
            SettingsChoices.SaveStatus(installFolder, shownAtLoad: false, chosen: true, registeredAfterSave: false, settingsFolderRefused: true));
        Assert.Equal($"{NotSaved} Start with Windows is now off in this copy.",
            SettingsChoices.SaveStatus(null, shownAtLoad: false, chosen: true, registeredAfterSave: false, settingsFolderRefused: true));
    }

    [Fact]
    public void SupportedGames_ChecksEachDistinctTweakOnce()
    {
        var calls = new Dictionary<string, int>();
        var games = BuiltInProfiles.GetAll();

        var cards = SettingsChoices.SupportedGames(games, name =>
        {
            calls[name] = calls.GetValueOrDefault(name) + 1;
            return (name, false);
        });

        var offered = games.SelectMany(g => g.RecommendedTweaks).Where(n => !SystemTweaksManager.IsRetired(n)).ToList();
        var references = offered.Count;
        var distinct = offered.Distinct().Count();
        Assert.True(references > distinct);
        Assert.Equal(distinct, calls.Count);
        Assert.All(calls.Values, count => Assert.Equal(1, count));
        Assert.Equal(games.Count, cards.Count);
        Assert.Equal(references, cards.Sum(c => c.Tweaks.Count));
    }

    [Fact]
    public void SupportedGames_NeverShowsOrDetectsARetiredTweak()
    {
        var retired = new[] { "DisableGameDvr", "DisableMpo", "OptimizeMmcss", "OptimizeWin32PrioritySeparation", "DisablePowerThrottling" };
        Assert.All(retired, n => Assert.True(SystemTweaksManager.IsRetired(n)));
        var games = new[]
        {
            new BuiltInGame { DisplayName = "Game A", ProcessNames = ["a.exe"], RecommendedTweaks = [.. retired, "DisableHags"] },
            new BuiltInGame { DisplayName = "Game B", ProcessNames = ["b.exe"], RecommendedTweaks = retired },
        };
        var asked = new List<string>();

        var cards = SettingsChoices.SupportedGames(games, name =>
        {
            asked.Add(name);
            return (name, false);
        });

        Assert.Equal(new[] { "DisableHags" }, asked);
        Assert.Equal(new[] { new SupportedGameTweak("DisableHags", false) }, cards[0].Tweaks);
        Assert.False(cards[1].HasTweaks);

        // The real built-in data reaches no retired tweak either, also not through the class-name fallback.
        var builtInCards = SettingsChoices.SupportedGames(BuiltInProfiles.GetAll(), _ => null);
        Assert.DoesNotContain(builtInCards.SelectMany(c => c.Tweaks), t => SystemTweaksManager.IsRetired(t.Name));
    }

    [Fact]
    public void SupportedGames_ShowsWhatDetectionFound_AndAnUnknownTweakAsNotInPlace()
    {
        var games = new[]
        {
            new BuiltInGame
            {
                DisplayName = "Game A",
                ProcessNames = ["a.exe", "a-launcher.exe"],
                Notes = ["Only note"],
                RecommendedTweaks = ["InPlaceTweak", "MissingTweak", "OffTweak"],
            },
            new BuiltInGame { DisplayName = "Game B", ProcessNames = ["b.exe"], Notes = ["One", "Two"] },
        };

        var cards = SettingsChoices.SupportedGames(games, name => name switch
        {
            "InPlaceTweak" => ("In place tweak", true),
            "OffTweak" => ("Off tweak", false),
            _ => null,
        });

        var a = cards[0];
        Assert.Equal("Game A", a.DisplayName);
        Assert.Equal("Processes: a.exe, a-launcher.exe", a.ProcessesText);
        Assert.Equal("1 optimization note", a.NotesHeader);
        Assert.True(a.HasNotes);
        Assert.True(a.HasTweaks);
        Assert.Equal(
            new[] { new SupportedGameTweak("In place tweak", true), new SupportedGameTweak("MissingTweak", false), new SupportedGameTweak("Off tweak", false) },
            a.Tweaks);
        Assert.Equal("\u2713 In place tweak", a.Tweaks[0].Text);
        Assert.Equal("Off tweak", a.Tweaks[2].Text);

        var b = cards[1];
        Assert.Equal("2 optimization notes", b.NotesHeader);
        Assert.False(b.HasTweaks);
        Assert.True(b.HasNotes);
    }

    [Fact]
    public void SupportedGames_HidesTheNotesOfAGameWithout()
    {
        var cards = SettingsChoices.SupportedGames([new BuiltInGame { DisplayName = "C" }], _ => null);

        Assert.False(cards[0].HasNotes);
        Assert.False(cards[0].HasTweaks);
    }
}
