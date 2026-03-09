using GameShift.Core.GameProfiles;
using Xunit;

namespace GameShift.Tests.GameProfiles;

/// <summary>
/// Tests for game detection matching logic in BuiltInProfiles.
/// Verifies that all built-in profiles have valid structure and that
/// process name matching works correctly.
/// </summary>
public class GameProfileMatchingTests
{
    [Fact]
    public void BuiltInProfiles_GetAll_Returns19Profiles()
    {
        var profiles = BuiltInProfiles.GetAll();

        Assert.Equal(19, profiles.Count);
    }

    [Fact]
    public void BuiltInProfiles_AllHaveUniqueIds()
    {
        var profiles = BuiltInProfiles.GetAll();
        var ids = profiles.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void BuiltInProfiles_AllHaveNonEmptyProcessNames()
    {
        var profiles = BuiltInProfiles.GetAll();

        foreach (var profile in profiles)
        {
            Assert.True(profile.ProcessNames.Length > 0,
                $"Profile '{profile.Id}' has no process names.");
            Assert.All(profile.ProcessNames, name =>
                Assert.False(string.IsNullOrWhiteSpace(name),
                    $"Profile '{profile.Id}' has an empty process name."));
        }
    }

    [Fact]
    public void BuiltInProfiles_AllHaveNonEmptyDisplayNames()
    {
        var profiles = BuiltInProfiles.GetAll();

        foreach (var profile in profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName),
                $"Profile '{profile.Id}' has an empty display name.");
        }
    }

    [Fact]
    public void BuiltInProfiles_NoProcessNameDuplicatesAcrossProfiles()
    {
        var profiles = BuiltInProfiles.GetAll();
        var allProcessNames = profiles
            .SelectMany(p => p.ProcessNames.Select(n => (ProfileId: p.Id, ProcessName: n)))
            .ToList();

        var duplicates = allProcessNames
            .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Duplicate process names found: {string.Join(", ", duplicates.Select(g => $"'{g.Key}' in [{string.Join(", ", g.Select(x => x.ProfileId))}]"))}");
    }

    [Theory]
    [InlineData("Overwatch.exe", "overwatch2")]
    [InlineData("VALORANT-Win64-Shipping.exe", "valorant")]
    [InlineData("League of Legends.exe", "leagueoflegends")]
    [InlineData("deadlock.exe", "deadlock")]
    [InlineData("osu!.exe", "osu")]
    [InlineData("cs2.exe", "counter-strike-2")]
    [InlineData("FortniteClient-Win64-Shipping.exe", "fortnite")]
    [InlineData("r5apex.exe", "apex-legends")]
    public void BuiltInProfiles_KnownGame_MatchesExpectedProfile(string processName, string expectedProfileId)
    {
        var profiles = BuiltInProfiles.GetAll();

        var match = profiles.FirstOrDefault(p =>
            p.ProcessNames.Any(n =>
                string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)));

        Assert.NotNull(match);
        Assert.Equal(expectedProfileId, match.Id);
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("explorer.exe")]
    [InlineData("notepad.exe")]
    [InlineData("")]
    public void BuiltInProfiles_NonGameProcess_NoMatch(string processName)
    {
        var profiles = BuiltInProfiles.GetAll();

        var match = profiles.FirstOrDefault(p =>
            p.ProcessNames.Any(n =>
                string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)));

        Assert.Null(match);
    }
}
