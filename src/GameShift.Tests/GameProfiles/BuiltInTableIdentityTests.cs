using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GameShift.Core.GameProfiles;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles.GameActions;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.GameProfiles;

/// <summary>
/// The built-in game table replaced two tables (the built-in profiles and the competitive presets).
/// Detection identity must not move with it: every process name either table knew resolves to the
/// same built-in ID, anti-cheat and name-only fallback flag, selects a preset exactly when it did,
/// and the presets keep their profiles, tips and firewall leftover names. The expected values were
/// captured from the build before the merge, not read from the new table.
/// </summary>
public class BuiltInTableIdentityTests
{
    // Captured from the build before the merge: id, process names, anti-cheat, name-only fallback, in table order.
    private static readonly (string Id, string[] ProcessNames, AntiCheatType AntiCheat, bool NameOnly)[] BaseBuiltIns =
    {
        ("overwatch2", new[] { "Overwatch.exe" }, AntiCheatType.None, false),
        ("valorant", new[] { "VALORANT-Win64-Shipping.exe" }, AntiCheatType.RiotVanguard, false),
        ("leagueoflegends", new[] { "League of Legends.exe" }, AntiCheatType.RiotVanguard, false),
        ("deadlock", new[] { "project8.exe", "deadlock.exe" }, AntiCheatType.ValveAntiCheat, false),
        ("osu", new[] { "osu!.exe" }, AntiCheatType.None, false),
        ("arknights-endfield", new[] { "Endfield.exe" }, AntiCheatType.BattlEye, false),
        ("wuthering-waves", new[] { "Client-Win64-Shipping.exe" }, AntiCheatType.TencentACE, false),
        ("genshin-impact", new[] { "GenshinImpact.exe", "YuanShen.exe" }, AntiCheatType.Proprietary, false),
        ("soulframe", new[] { "Soulframe.x64.exe", "Soulframe.exe" }, AntiCheatType.None, false),
        ("counter-strike-2", new[] { "cs2.exe" }, AntiCheatType.ValveAntiCheat, false),
        ("fortnite", new[] { "FortniteClient-Win64-Shipping.exe" }, AntiCheatType.EasyAntiCheat, false),
        ("apex-legends", new[] { "r5apex_dx12.exe", "r5apex.exe" }, AntiCheatType.EasyAntiCheat, true),
        ("rust", new[] { "RustClient.exe" }, AntiCheatType.EasyAntiCheat, false),
        ("elden-ring", new[] { "eldenring.exe" }, AntiCheatType.EasyAntiCheat, false),
        ("elden-ring-nightreign", new[] { "nightreign.exe" }, AntiCheatType.EasyAntiCheat, false),
        ("call-of-duty", new[] { "cod.exe" }, AntiCheatType.Ricochet, false),
        ("cyberpunk-2077", new[] { "Cyberpunk2077.exe" }, AntiCheatType.None, false),
        ("minecraft-java", new[] { "javaw.exe" }, AntiCheatType.None, false),
        ("final-fantasy-xiv", new[] { "ffxiv_dx11.exe" }, AntiCheatType.None, false),
    };

    // Captured from the build before the merge: every built-in and preset process name, the built-in it resolves to,
    // and whether it selects a preset.
    private static readonly (string ProcessName, string BuiltInId, bool SelectsPreset)[] BaseNames =
    {
        ("Overwatch.exe", "overwatch2", true),
        ("VALORANT-Win64-Shipping.exe", "valorant", true),
        ("League of Legends.exe", "leagueoflegends", true),
        ("project8.exe", "deadlock", true),
        ("deadlock.exe", "deadlock", false),
        ("osu!.exe", "osu", true),
        ("Endfield.exe", "arknights-endfield", false),
        ("Client-Win64-Shipping.exe", "wuthering-waves", false),
        ("GenshinImpact.exe", "genshin-impact", false),
        ("YuanShen.exe", "genshin-impact", false),
        ("Soulframe.x64.exe", "soulframe", false),
        ("Soulframe.exe", "soulframe", false),
        ("cs2.exe", "counter-strike-2", false),
        ("FortniteClient-Win64-Shipping.exe", "fortnite", false),
        ("r5apex_dx12.exe", "apex-legends", false),
        ("r5apex.exe", "apex-legends", false),
        ("RustClient.exe", "rust", false),
        ("eldenring.exe", "elden-ring", false),
        ("nightreign.exe", "elden-ring-nightreign", false),
        ("cod.exe", "call-of-duty", false),
        ("Cyberpunk2077.exe", "cyberpunk-2077", false),
        ("javaw.exe", "minecraft-java", false),
        ("ffxiv_dx11.exe", "final-fantasy-xiv", false),
    };

    // Captured from the build before the merge: preset executable, anti-cheat badge, VBS safe to disable, VBS reason.
    private static readonly (string Exe, string AntiCheatName, bool VbsSafe, string VbsReason)[] BasePresetMetadata =
    {
        ("Overwatch.exe", "", true, ""),
        ("VALORANT-Win64-Shipping.exe", "Riot Vanguard", false, "Riot Vanguard enforces VBS/HVCI (Memory Integrity) as a hard requirement. Disabling it causes VAN:RESTRICTION errors and prevents Valorant from launching."),
        ("League of Legends.exe", "Riot Vanguard", false, "Riot Vanguard enforces VBS/HVCI (Memory Integrity) as a hard requirement. Disabling it causes VAN:RESTRICTION errors and prevents League of Legends from launching."),
        ("project8.exe", "VAC", true, ""),
        ("osu!.exe", "", true, ""),
    };

    // Captured from the build before the merge: the profile each preset created, serialized, with the removed
    // MemoryThresholdMB field taken out.
    private static readonly (string Exe, string Json)[] BasePresetProfiles =
    {
        ("Overwatch.exe", "{\"SchemaVersion\":1,\"Id\":\"\",\"GameName\":\"Overwatch 2\",\"ProcessId\":0,\"SuppressServices\":true,\"SwitchPowerPlan\":true,\"SetTimerResolution\":false,\"BoostProcessPriority\":true,\"UsePerformanceCoresOnly\":false,\"EnableEfficiencyMode\":true,\"PinToVCacheCcd\":false,\"EnableDpcMonitoring\":true,\"DpcThresholdMicroseconds\":0,\"AntiCheat\":0,\"ExecutableName\":\"Overwatch.exe\",\"ExecutablePath\":\"\",\"LauncherSource\":\"\",\"CustomServicesToPause\":null,\"Intensity\":1,\"OptimizeMemory\":true,\"ReduceVisualEffects\":true,\"OptimizeNetwork\":true,\"ManageIoPriority\":true,\"ManageMemoryPriority\":true,\"EmptyWorkingSets\":true,\"HardMinWorkingSetMB\":2048,\"SuppressTier2Services\":true,\"SuppressScheduledTasks\":true,\"SuppressDefenderScheduledScan\":false,\"UnparkCpuCores\":true,\"DisableProcessorIdle\":false,\"DisableMpo\":false,\"EnableCompetitiveMode\":false,\"SuspendDiscordOverlay\":true,\"SuspendSteamOverlay\":true,\"SuspendNvidiaOverlay\":true,\"KillWidgets\":true,\"EnableGpuOptimization\":true,\"ForceMaxPerformancePowerMode\":true,\"OptimizeShaderCache\":true,\"EnableLowLatencyMode\":true}"),
        ("VALORANT-Win64-Shipping.exe", "{\"SchemaVersion\":1,\"Id\":\"\",\"GameName\":\"Valorant\",\"ProcessId\":0,\"SuppressServices\":true,\"SwitchPowerPlan\":true,\"SetTimerResolution\":false,\"BoostProcessPriority\":true,\"UsePerformanceCoresOnly\":false,\"EnableEfficiencyMode\":true,\"PinToVCacheCcd\":false,\"EnableDpcMonitoring\":true,\"DpcThresholdMicroseconds\":0,\"AntiCheat\":1,\"ExecutableName\":\"VALORANT-Win64-Shipping.exe\",\"ExecutablePath\":\"\",\"LauncherSource\":\"\",\"CustomServicesToPause\":null,\"Intensity\":1,\"OptimizeMemory\":true,\"ReduceVisualEffects\":true,\"OptimizeNetwork\":true,\"ManageIoPriority\":true,\"ManageMemoryPriority\":true,\"EmptyWorkingSets\":true,\"HardMinWorkingSetMB\":2048,\"SuppressTier2Services\":true,\"SuppressScheduledTasks\":true,\"SuppressDefenderScheduledScan\":false,\"UnparkCpuCores\":true,\"DisableProcessorIdle\":false,\"DisableMpo\":false,\"EnableCompetitiveMode\":false,\"SuspendDiscordOverlay\":true,\"SuspendSteamOverlay\":true,\"SuspendNvidiaOverlay\":true,\"KillWidgets\":true,\"EnableGpuOptimization\":true,\"ForceMaxPerformancePowerMode\":true,\"OptimizeShaderCache\":true,\"EnableLowLatencyMode\":true}"),
        ("League of Legends.exe", "{\"SchemaVersion\":1,\"Id\":\"\",\"GameName\":\"League of Legends\",\"ProcessId\":0,\"SuppressServices\":true,\"SwitchPowerPlan\":true,\"SetTimerResolution\":false,\"BoostProcessPriority\":true,\"UsePerformanceCoresOnly\":false,\"EnableEfficiencyMode\":true,\"PinToVCacheCcd\":false,\"EnableDpcMonitoring\":true,\"DpcThresholdMicroseconds\":0,\"AntiCheat\":1,\"ExecutableName\":\"League of Legends.exe\",\"ExecutablePath\":\"\",\"LauncherSource\":\"\",\"CustomServicesToPause\":null,\"Intensity\":1,\"OptimizeMemory\":true,\"ReduceVisualEffects\":true,\"OptimizeNetwork\":true,\"ManageIoPriority\":true,\"ManageMemoryPriority\":true,\"EmptyWorkingSets\":true,\"HardMinWorkingSetMB\":2048,\"SuppressTier2Services\":true,\"SuppressScheduledTasks\":true,\"SuppressDefenderScheduledScan\":false,\"UnparkCpuCores\":true,\"DisableProcessorIdle\":false,\"DisableMpo\":false,\"EnableCompetitiveMode\":false,\"SuspendDiscordOverlay\":true,\"SuspendSteamOverlay\":true,\"SuspendNvidiaOverlay\":true,\"KillWidgets\":true,\"EnableGpuOptimization\":true,\"ForceMaxPerformancePowerMode\":true,\"OptimizeShaderCache\":true,\"EnableLowLatencyMode\":true}"),
        ("project8.exe", "{\"SchemaVersion\":1,\"Id\":\"\",\"GameName\":\"Deadlock\",\"ProcessId\":0,\"SuppressServices\":true,\"SwitchPowerPlan\":true,\"SetTimerResolution\":false,\"BoostProcessPriority\":true,\"UsePerformanceCoresOnly\":false,\"EnableEfficiencyMode\":true,\"PinToVCacheCcd\":false,\"EnableDpcMonitoring\":true,\"DpcThresholdMicroseconds\":0,\"AntiCheat\":5,\"ExecutableName\":\"project8.exe\",\"ExecutablePath\":\"\",\"LauncherSource\":\"\",\"CustomServicesToPause\":null,\"Intensity\":1,\"OptimizeMemory\":true,\"ReduceVisualEffects\":true,\"OptimizeNetwork\":true,\"ManageIoPriority\":true,\"ManageMemoryPriority\":true,\"EmptyWorkingSets\":true,\"HardMinWorkingSetMB\":2048,\"SuppressTier2Services\":true,\"SuppressScheduledTasks\":true,\"SuppressDefenderScheduledScan\":false,\"UnparkCpuCores\":true,\"DisableProcessorIdle\":false,\"DisableMpo\":false,\"EnableCompetitiveMode\":false,\"SuspendDiscordOverlay\":true,\"SuspendSteamOverlay\":true,\"SuspendNvidiaOverlay\":true,\"KillWidgets\":true,\"EnableGpuOptimization\":true,\"ForceMaxPerformancePowerMode\":true,\"OptimizeShaderCache\":true,\"EnableLowLatencyMode\":true}"),
        ("osu!.exe", "{\"SchemaVersion\":1,\"Id\":\"\",\"GameName\":\"osu!\",\"ProcessId\":0,\"SuppressServices\":true,\"SwitchPowerPlan\":true,\"SetTimerResolution\":false,\"BoostProcessPriority\":false,\"UsePerformanceCoresOnly\":false,\"EnableEfficiencyMode\":true,\"PinToVCacheCcd\":false,\"EnableDpcMonitoring\":true,\"DpcThresholdMicroseconds\":0,\"AntiCheat\":0,\"ExecutableName\":\"osu!.exe\",\"ExecutablePath\":\"\",\"LauncherSource\":\"\",\"CustomServicesToPause\":null,\"Intensity\":1,\"OptimizeMemory\":true,\"ReduceVisualEffects\":true,\"OptimizeNetwork\":true,\"ManageIoPriority\":true,\"ManageMemoryPriority\":true,\"EmptyWorkingSets\":true,\"HardMinWorkingSetMB\":2048,\"SuppressTier2Services\":true,\"SuppressScheduledTasks\":true,\"SuppressDefenderScheduledScan\":false,\"UnparkCpuCores\":true,\"DisableProcessorIdle\":false,\"DisableMpo\":false,\"EnableCompetitiveMode\":false,\"SuspendDiscordOverlay\":true,\"SuspendSteamOverlay\":true,\"SuspendNvidiaOverlay\":true,\"KillWidgets\":true,\"EnableGpuOptimization\":true,\"ForceMaxPerformancePowerMode\":true,\"OptimizeShaderCache\":true,\"EnableLowLatencyMode\":true}"),
    };

    // Captured from the build before the merge: each preset tip, in order, with its hardware condition text.
    private static readonly (string Exe, string TipName, string Condition)[] BaseTips =
    {
        ("Overwatch.exe", "Tip: ow2_reflex", "NVIDIA GPU only"),
        ("Overwatch.exe", "Tip: ow2_antilag", "AMD GPU only"),
        ("Overwatch.exe", "Tip: ow2_fullscreen", ""),
        ("Overwatch.exe", "Tip: ow2_render_scale", ""),
        ("Overwatch.exe", "Tip: ow2_memory_leak", ""),
        ("Overwatch.exe", "Tip: ow2_polling_rate", ""),
        ("Overwatch.exe", "Tip: ow2_hags", "HAGS enabled"),
        ("Overwatch.exe", "Tip: ow2_hybrid_cpu", "Intel hybrid CPU detected"),
        ("Overwatch.exe", "Tip: ow2_alt_tab", ""),
        ("Overwatch.exe", "Tip: ow2_nvidia_filters", "NVIDIA GPU only"),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_xmp", ""),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_sharpening", ""),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_reflex", "NVIDIA GPU only"),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_hags", "HAGS enabled"),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_multithread", ""),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_vbs", ""),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_nvidia_filters", "NVIDIA GPU only"),
        ("VALORANT-Win64-Shipping.exe", "Tip: valorant_antilag2", "AMD GPU only"),
        ("League of Legends.exe", "Tip: lol_dx11", ""),
        ("League of Legends.exe", "Tip: lol_client_priority", ""),
        ("League of Legends.exe", "Tip: lol_vbs", ""),
        ("League of Legends.exe", "Tip: lol_nvidia_filters", "NVIDIA GPU only"),
        ("project8.exe", "Tip: deadlock_reflex", ""),
        ("project8.exe", "Tip: deadlock_ssao", ""),
        ("project8.exe", "Tip: deadlock_launch", ""),
        ("project8.exe", "Tip: deadlock_pcores", "Hybrid CPU detected"),
        ("project8.exe", "Tip: deadlock_vram", "Less than 6 GB VRAM"),
        ("project8.exe", "Tip: deadlock_vulkan", "AMD GPU only"),
        ("project8.exe", "Tip: deadlock_autoexec", ""),
        ("project8.exe", "Tip: deadlock_nvidia_filters", "NVIDIA GPU only"),
        ("project8.exe", "Tip: deadlock_antilag2", "AMD GPU only"),
        ("osu!.exe", "Tip: osu_raw_input", ""),
        ("osu!.exe", "Tip: osu_tablet", ""),
        ("osu!.exe", "Tip: osu_timer", ""),
        ("osu!.exe", "Tip: osu_amd_opengl", "AMD GPU only"),
        ("osu!.exe", "Tip: osu_optimus", "NVIDIA laptop only"),
    };

    // Captured from the build before the merge: the firewall rules the presets created.
    private static readonly (string RuleName, string Direction, string Program)[] BaseFirewallRules =
    {
        ("GameShift_OW2_In", "Inbound", @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe"),
        ("GameShift_Valorant_In", "Inbound", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"),
        ("GameShift_LoL_In", "Inbound", @"C:\Riot Games\League of Legends\Game\League of Legends.exe"),
        ("GameShift_Deadlock_In", "Inbound", @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64\project8.exe"),
    };


    private static BuiltInGame? FirstMatch(string processName) =>
        BuiltInProfiles.GetAll().FirstOrDefault(game =>
            game.ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void Table_KeepsTheIdsOrderProcessNamesAntiCheatAndFallback()
    {
        var actual = BuiltInProfiles.GetAll()
            .Select(game => (game.Id, string.Join(",", game.ProcessNames), game.AntiCheat, game.AllowNameOnlyFallback));
        var expected = BaseBuiltIns
            .Select(game => (game.Id, string.Join(",", game.ProcessNames), game.AntiCheat, game.NameOnly));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryProcessName_ResolvesToTheSameBuiltInAndPreset_InAnyCase()
    {
        foreach (var (processName, builtInId, selectsPreset) in BaseNames)
        {
            var expected = BaseBuiltIns.Single(game => game.Id == builtInId);
            foreach (var spelling in new[] { processName, processName.ToUpperInvariant(), processName.ToLowerInvariant() })
            {
                var game = FirstMatch(spelling);
                Assert.NotNull(game);
                Assert.Equal(builtInId, game!.Id);
                Assert.Equal(expected.AntiCheat, game.AntiCheat);
                Assert.Equal(expected.NameOnly, game.AllowNameOnlyFallback);
                Assert.Equal(selectsPreset, BuiltInProfiles.FindPresetGame(spelling) != null);
            }
        }

        var known = BaseNames.Select(n => n.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(BuiltInProfiles.GetAll().SelectMany(game => game.ProcessNames), name => Assert.Contains(name, known));
    }

    [Fact]
    public void EachPreset_IsSelectedOnlyByItsExecutable_AndBelongsToTheBuiltInOfThatName()
    {
        foreach (var (exe, antiCheatName, vbsSafe, vbsReason) in BasePresetMetadata)
        {
            var game = BuiltInProfiles.FindPresetGame(exe);
            Assert.NotNull(game);
            Assert.Equal(FirstMatch(exe)!.Id, game!.Id);
            Assert.Equal(exe, game.Preset!.ExecutableName);
            Assert.Equal(antiCheatName, game.Preset.AntiCheatName);
            Assert.Equal(vbsSafe, game.Preset.VbsSafeToDisable);
            Assert.Equal(vbsReason, game.Preset.VbsSafetyReason);
        }

        Assert.Equal(
            BasePresetMetadata.Select(p => p.Exe).OrderBy(e => e, StringComparer.Ordinal),
            BuiltInProfiles.GetAll().Where(g => g.Preset != null).Select(g => g.Preset!.ExecutableName).OrderBy(e => e, StringComparer.Ordinal));
    }

    [Fact]
    public void PresetProfiles_AreTheSameProfilesAsBefore()
    {
        foreach (var (exe, json) in BasePresetProfiles)
            Assert.Equal(json, JsonSerializer.Serialize(BuiltInProfiles.GetPresetProfile(exe)));

        Assert.Null(BuiltInProfiles.GetPresetProfile("deadlock.exe"));
        Assert.Null(BuiltInProfiles.GetPresetProfile("cs2.exe"));
    }

    [Fact]
    public void PresetTips_AreTheSameTipsInTheSameOrder_WithoutTheLeagueClientPriorityTip()
    {
        // That tip described the League client priority change GameShift no longer makes.
        var expected = BaseTips.Where(t => t.TipName != "Tip: lol_client_priority").ToList();

        var actual = BaseNames.Where(n => n.SelectsPreset)
            .SelectMany(n => BuiltInProfiles.GetPresetTips(n.ProcessName).Select(tip => (n.ProcessName, tip.Name, tip.Condition, tip.Tier)))
            .ToList();

        Assert.Equal(expected.Select(t => (t.Exe, t.TipName, t.Condition, 3)), actual);
        Assert.All(BuiltInProfiles.GetAll().Where(g => g.Preset != null), g => Assert.All(g.Preset!.Tips, tip => Assert.IsType<OneTimeTipAction>(tip)));
        Assert.Empty(BuiltInProfiles.GetPresetTips("cs2.exe"));
    }

    [Fact]
    public void FirewallLeftovers_AreTheRulesThePresetsCreated()
    {
        Assert.Equal(
            BaseFirewallRules,
            BuiltInProfiles.GetFirewallLeftovers().Select(rule => (rule.RuleName, rule.Direction, rule.Program)));
    }

    [Fact]
    public void StartupLeftoverSweep_ReceivesTheFourRuleNamesWithTheirDirectionAndProgram()
    {
        var runner = new FakePowerShellRunner();

        // The same composition the orchestrator's startup sweep uses, through the process seam.
        FirewallRuleAction.RemoveLeftovers(BuiltInProfiles.GetFirewallLeftovers(), runner.Run);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(BaseFirewallRules.SelectMany(rule => new[] { rule.RuleName, rule.Direction, rule.Program }), call.Values);
        Assert.Equal(
            new[] { "GameShift_OW2_In", "GameShift_Valorant_In", "GameShift_LoL_In", "GameShift_Deadlock_In" },
            call.Values.Where((_, i) => i % 3 == 0));
    }
}
