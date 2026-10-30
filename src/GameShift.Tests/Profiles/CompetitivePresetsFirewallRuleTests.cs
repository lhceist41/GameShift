using GameShift.Core.Profiles;

namespace GameShift.Tests.Profiles;

/// <summary>
/// GameShift removes firewall rules that earlier versions left behind by matching each preset rule's
/// display name and program. Every release so far has created exactly these rules; renaming one, or
/// changing its program, would leave the old rules in place for good.
/// </summary>
public class CompetitivePresetsFirewallRuleTests
{
    [Fact]
    public void GetFirewallRules_KeepsTheNamesAndProgramsEarlierVersionsCreated()
    {
        var rules = CompetitivePresets.GetFirewallRules()
            .Select(rule => (rule.RuleName, rule.Program))
            .OrderBy(rule => rule.RuleName, StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                ("GameShift_Deadlock_In", @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64\project8.exe"),
                ("GameShift_LoL_In", @"C:\Riot Games\League of Legends\Game\League of Legends.exe"),
                ("GameShift_OW2_In", @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe"),
                ("GameShift_Valorant_In", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"),
            },
            rules);
    }
}
