using GameShift.Core.Config;
using GameShift.Core.GameProfiles;
using GameShift.Core.Monitoring;

namespace GameShift.Core.Presentation;

/// <summary>A theme the Settings page offers, with its label.</summary>
public sealed record ThemeOption(AppTheme Value, string Label);

/// <summary>A recommended tweak on a supported game's card: its name and whether its change is in place on this PC.</summary>
public sealed record SupportedGameTweak(string Name, bool InPlace)
{
    public string Text => InPlace ? "\u2713 " + Name : Name;
}

/// <summary>A read-only card for a game GameShift recognizes.</summary>
public sealed record SupportedGameCard(
    string DisplayName,
    string ProcessesText,
    string NotesHeader,
    IReadOnlyList<string> Notes,
    IReadOnlyList<SupportedGameTweak> Tweaks)
{
    public bool HasNotes => Notes.Count > 0;
    public bool HasTweaks => Tweaks.Count > 0;
}

/// <summary>
/// Choices and text for the Settings page. Pure: the App reads the machine and binds to the results.
/// </summary>
public static class SettingsChoices
{
    public static IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.System, "Use Windows setting"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    /// <summary>
    /// The choice shown for a saved theme. A number this version does not know applies as
    /// <see cref="AppTheme.System"/>, so it shows as that.
    /// </summary>
    public static AppTheme ShownTheme(AppTheme saved) =>
        Enum.IsDefined(saved) ? saved : AppTheme.System;

    /// <summary>
    /// Feedback for the default DPC load alert box; null when the text is a valid threshold.
    /// <paramref name="keptThreshold"/> is the value Save writes while the text is rejected.
    /// </summary>
    public static string? ThresholdError(string? text, int keptThreshold) =>
        DpcLoad.TryParseThreshold(text, out _)
            ? null
            : $"Enter a percent from 0 to 100, for example 0.1. Until then Save keeps {DpcLoad.Format(keptThreshold)}.";

    /// <summary>
    /// Whether Save leaves the startup task as it is although the box was changed: a copy running
    /// outside the registered install folder leaves the task to the installed copy. Compared with
    /// what the box showed when the page loaded, which is the task itself, so a stored setting that
    /// differs from the task never turns an unrelated save into a refusal.
    /// </summary>
    public static bool StartupChangeRefused(string? installFolderKeepingStartup, bool shownAtLoad, bool chosen) =>
        installFolderKeepingStartup != null && chosen != shownAtLoad;

    /// <summary>
    /// The status after Save. <paramref name="registeredAfterSave"/> is the startup task read back
    /// after Save, which the box then shows; when it differs from <paramref name="chosen"/>, the
    /// status says why the box changed instead of only that the settings were saved. With
    /// <paramref name="settingsFolderRefused"/>, Save kept nothing, and the status says so first.
    /// </summary>
    public static string SaveStatus(
        string? installFolderKeepingStartup, bool shownAtLoad, bool chosen, bool registeredAfterSave, bool settingsFolderRefused)
    {
        var saved = settingsFolderRefused
            ? "Settings not saved: GameShift could not use its settings folder."
            : "Settings saved.";
        if (registeredAfterSave == chosen) return saved;
        if (StartupChangeRefused(installFolderKeepingStartup, shownAtLoad, chosen))
            return $"{saved} Start with Windows was not changed: GameShift is installed in {installFolderKeepingStartup}, so change it in that copy.";
        return $"{saved} Start with Windows is now {(registeredAfterSave ? "on" : "off")} in this copy.";
    }

    /// <summary>
    /// The read-only cards for the built-in games. <paramref name="detect"/> gives a tweak's display
    /// name and whether its change is in place, or null for a name it does not know; it runs once per
    /// distinct tweak, since many games share the same tweaks and each check reads the machine.
    /// </summary>
    public static IReadOnlyList<SupportedGameCard> SupportedGames(
        IEnumerable<BuiltInGame> games, Func<string, (string Name, bool InPlace)?> detect)
    {
        var checkedTweaks = new Dictionary<string, SupportedGameTweak>(StringComparer.Ordinal);
        var cards = new List<SupportedGameCard>();
        foreach (var game in games)
        {
            var tweaks = new List<SupportedGameTweak>();
            foreach (var className in game.RecommendedTweaks)
            {
                if (!checkedTweaks.TryGetValue(className, out var tweak))
                {
                    var found = detect(className);
                    tweak = found is { } f ? new SupportedGameTweak(f.Name, f.InPlace) : new SupportedGameTweak(className, false);
                    checkedTweaks[className] = tweak;
                }
                tweaks.Add(tweak);
            }

            cards.Add(new SupportedGameCard(
                game.DisplayName,
                "Processes: " + string.Join(", ", game.ProcessNames),
                game.Notes.Length == 1 ? "1 optimization note" : $"{game.Notes.Length} optimization notes",
                game.Notes,
                tweaks));
        }
        return cards;
    }
}
