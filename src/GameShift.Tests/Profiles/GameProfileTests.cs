using System.Text.Json;
using GameShift.Core.GameProfiles;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using Xunit;

namespace GameShift.Tests.Profiles;

/// <summary>
/// Tests for GameProfile.IsOptimizationEnabled mapping and serialization round-trips.
/// </summary>
public class GameProfileTests
{
    // ── IsOptimizationEnabled ─────────────────────────────────────────

    [Theory]
    [InlineData(nameof(ServiceSuppressor))]
    [InlineData(nameof(PowerPlanSwitcher))]
    [InlineData(nameof(TimerResolutionManager))]
    [InlineData(nameof(ProcessPriorityBooster))]
    [InlineData(nameof(HybridCpuDetector))]
    [InlineData(nameof(EfficiencyModeController))]
    public void IsOptimizationEnabled_AllKnownOptimizations_HaveExplicitMapping(string className)
    {
        // Every known optimization class must have an explicit case in the switch,
        // not fall through to the _ => true default. We verify this by creating a
        // profile with all toggles set to false - if it returns true, the mapping is missing.
        var profile = CreateAllDisabledProfile();
        var id = GetOptimizationId(className);

        bool result = profile.IsOptimizationEnabled(id);

        Assert.False(result,
            $"IsOptimizationEnabled(\"{id}\") returned true with all toggles disabled. " +
            $"This means the {className} OptimizationId is not mapped in the switch expression.");
    }

    [Fact]
    public void IsOptimizationEnabled_UnknownOptimization_ReturnsTrue()
    {
        var profile = CreateAllDisabledProfile();

        bool result = profile.IsOptimizationEnabled("Some Unknown Optimization");

        Assert.True(result, "Unknown optimizations should default to enabled.");
    }

    [Fact]
    public void IsOptimizationEnabled_DefaultProfile_EnablesTheFourMainOptimizations()
    {
        var profile = GameProfile.CreateDefault();

        Assert.True(profile.IsOptimizationEnabled(ServiceSuppressor.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(PowerPlanSwitcher.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(ProcessPriorityBooster.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(EfficiencyModeController.OptimizationId));
    }

    [Fact]
    public void IsOptimizationEnabled_DefaultProfile_LeavesTheAdvancedOptimizationsOff()
    {
        var profile = GameProfile.CreateDefault();

        Assert.False(profile.IsOptimizationEnabled(TimerResolutionManager.OptimizationId));
        Assert.False(profile.IsOptimizationEnabled(HybridCpuDetector.OptimizationId));
    }

    // ── Core pinning switch ───────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PinGameCores_SetsBothPinningFlags(bool on)
    {
        var profile = new GameProfile { UsePerformanceCoresOnly = !on, PinToVCacheCcd = !on };

        profile.PinGameCores = on;

        Assert.Equal(on, profile.UsePerformanceCoresOnly);
        Assert.Equal(on, profile.PinToVCacheCcd);
        Assert.Equal(on, profile.IsOptimizationEnabled(HybridCpuDetector.OptimizationId));
    }

    [Fact]
    public void PinGameCores_ReadsOnWhenEitherFlagIsOn()
    {
        Assert.True(new GameProfile { PinToVCacheCcd = true }.PinGameCores);
        Assert.True(new GameProfile { UsePerformanceCoresOnly = true }.PinGameCores);
        Assert.False(new GameProfile().PinGameCores);
    }

    // ── Schema version and migration ──────────────────────────────────

    [Fact]
    public void SchemaVersion_IsCurrentInCode_AndZeroWhenTheJsonHasNone()
    {
        Assert.Equal(GameProfile.CurrentSchemaVersion, new GameProfile().SchemaVersion);
        Assert.Equal(0, JsonSerializer.Deserialize<GameProfile>("""{"Id":"old"}""")!.SchemaVersion);
        Assert.Equal(GameProfile.CurrentSchemaVersion, new GameProfile().Clone().SchemaVersion);
    }

    [Fact]
    public void UpgradeSchema_Version0_TurnsTimerAndPinningOff()
    {
        var profile = JsonSerializer.Deserialize<GameProfile>(
            """{"Id":"old","SetTimerResolution":true,"UsePerformanceCoresOnly":true,"PinToVCacheCcd":true,"EnableEfficiencyMode":true}""")!;

        Assert.True(profile.UpgradeSchema());

        Assert.False(profile.SetTimerResolution);
        Assert.False(profile.UsePerformanceCoresOnly);
        Assert.False(profile.PinToVCacheCcd);
        Assert.True(profile.EnableEfficiencyMode);
        Assert.Equal(GameProfile.CurrentSchemaVersion, profile.SchemaVersion);
    }

    [Fact]
    public void UpgradeSchema_IsIdempotent()
    {
        var profile = JsonSerializer.Deserialize<GameProfile>("""{"Id":"old","SetTimerResolution":true}""")!;
        profile.UpgradeSchema();
        profile.SetTimerResolution = true; // turned on again by the user after the migration

        Assert.False(profile.UpgradeSchema());
        Assert.True(profile.SetTimerResolution);
    }

    [Fact]
    public void UpgradeSchema_Version1_KeepsTimerAndPinningOn()
    {
        var profile = JsonSerializer.Deserialize<GameProfile>(
            """{"Id":"new","SchemaVersion":1,"SetTimerResolution":true,"UsePerformanceCoresOnly":true,"PinToVCacheCcd":true}""")!;

        Assert.False(profile.UpgradeSchema());

        Assert.True(profile.SetTimerResolution);
        Assert.True(profile.PinGameCores);
    }

    [Fact]
    public void PresetProfiles_AreCurrent_WithTheAdvancedOptimizationsOff()
    {
        foreach (var exe in new[]
                 {
                     "Overwatch.exe", "VALORANT-Win64-Shipping.exe", "League of Legends.exe", "project8.exe", "osu!.exe",
                 })
        {
            var preset = BuiltInProfiles.GetPresetProfile(exe)!;

            Assert.Equal(GameProfile.CurrentSchemaVersion, preset.SchemaVersion);
            Assert.False(preset.SetTimerResolution, exe);
            Assert.False(preset.PinGameCores, exe);
        }
    }

    // ── OptimizationId consistency ────────────────────────────────────

    [Fact]
    public void OptimizationId_Constants_AreNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ServiceSuppressor.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(PowerPlanSwitcher.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(TimerResolutionManager.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(ProcessPriorityBooster.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(VisualEffectReducer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(NetworkOptimizer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(HybridCpuDetector.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(MpoToggle.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(CompetitiveMode.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(GpuDriverOptimizer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(ScheduledTaskSuppressor.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(CpuParkingManager.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(EfficiencyModeController.OptimizationId));
    }

    [Fact]
    public void OptimizationId_Constants_AreAllUnique()
    {
        var ids = new[]
        {
            ServiceSuppressor.OptimizationId,
            PowerPlanSwitcher.OptimizationId,
            TimerResolutionManager.OptimizationId,
            ProcessPriorityBooster.OptimizationId,
            VisualEffectReducer.OptimizationId,
            NetworkOptimizer.OptimizationId,
            HybridCpuDetector.OptimizationId,
            MpoToggle.OptimizationId,
            CompetitiveMode.OptimizationId,
            GpuDriverOptimizer.OptimizationId,
            ScheduledTaskSuppressor.OptimizationId,
            CpuParkingManager.OptimizationId,
            EfficiencyModeController.OptimizationId,
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    // ── Profile serialization round-trip ──────────────────────────────

    [Fact]
    public void GameProfile_SerializationRoundTrip_PreservesAllToggles()
    {
        var original = new GameProfile
        {
            Id = "test_game",
            GameName = "Test Game",
            ExecutableName = "test.exe",
            SuppressServices = false,
            SwitchPowerPlan = true,
            SetTimerResolution = false,
            BoostProcessPriority = true,
            OptimizeMemory = false,
            ReduceVisualEffects = true,
            OptimizeNetwork = false,
            UsePerformanceCoresOnly = true,
            DisableMpo = true,
            EnableCompetitiveMode = false,
            EnableGpuOptimization = true,
            SuppressScheduledTasks = false,
            UnparkCpuCores = true,
            ManageIoPriority = false,
            EnableEfficiencyMode = true,
            ManageMemoryPriority = true,
            PinToVCacheCcd = false,
            SuppressTier2Services = true,
            SuppressDefenderScheduledScan = true,
            DisableProcessorIdle = false,
            EnableDpcMonitoring = false,
            DpcThresholdMicroseconds = 500,
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<GameProfile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.GameName, deserialized.GameName);
        Assert.Equal(original.ExecutableName, deserialized.ExecutableName);
        Assert.Equal(original.SuppressServices, deserialized.SuppressServices);
        Assert.Equal(original.SwitchPowerPlan, deserialized.SwitchPowerPlan);
        Assert.Equal(original.SetTimerResolution, deserialized.SetTimerResolution);
        Assert.Equal(original.BoostProcessPriority, deserialized.BoostProcessPriority);
        Assert.Equal(original.OptimizeMemory, deserialized.OptimizeMemory);
        Assert.Equal(original.ReduceVisualEffects, deserialized.ReduceVisualEffects);
        Assert.Equal(original.OptimizeNetwork, deserialized.OptimizeNetwork);
        Assert.Equal(original.UsePerformanceCoresOnly, deserialized.UsePerformanceCoresOnly);
        Assert.Equal(original.DisableMpo, deserialized.DisableMpo);
        Assert.Equal(original.EnableCompetitiveMode, deserialized.EnableCompetitiveMode);
        Assert.Equal(original.EnableGpuOptimization, deserialized.EnableGpuOptimization);
        Assert.Equal(original.SuppressScheduledTasks, deserialized.SuppressScheduledTasks);
        Assert.Equal(original.UnparkCpuCores, deserialized.UnparkCpuCores);
        Assert.Equal(original.ManageIoPriority, deserialized.ManageIoPriority);
        Assert.Equal(original.EnableEfficiencyMode, deserialized.EnableEfficiencyMode);
        Assert.Equal(original.ManageMemoryPriority, deserialized.ManageMemoryPriority);
        Assert.Equal(original.PinToVCacheCcd, deserialized.PinToVCacheCcd);
        Assert.Equal(original.SuppressTier2Services, deserialized.SuppressTier2Services);
        Assert.Equal(original.SuppressDefenderScheduledScan, deserialized.SuppressDefenderScheduledScan);
        Assert.Equal(original.DisableProcessorIdle, deserialized.DisableProcessorIdle);
        Assert.Equal(original.EnableDpcMonitoring, deserialized.EnableDpcMonitoring);
        Assert.Equal(original.DpcThresholdMicroseconds, deserialized.DpcThresholdMicroseconds);
    }

    [Fact]
    public void GameProfile_SerializationRoundTrip_IsOptimizationEnabled_MatchesOriginal()
    {
        var original = new GameProfile
        {
            Id = "roundtrip",
            SuppressServices = false,
            UsePerformanceCoresOnly = true,
            SetTimerResolution = false,
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<GameProfile>(json)!;

        // Verify IsOptimizationEnabled behaves identically after round-trip
        Assert.Equal(
            original.IsOptimizationEnabled(ServiceSuppressor.OptimizationId),
            deserialized.IsOptimizationEnabled(ServiceSuppressor.OptimizationId));
        Assert.Equal(
            original.IsOptimizationEnabled(HybridCpuDetector.OptimizationId),
            deserialized.IsOptimizationEnabled(HybridCpuDetector.OptimizationId));
        Assert.Equal(
            original.IsOptimizationEnabled(TimerResolutionManager.OptimizationId),
            deserialized.IsOptimizationEnabled(TimerResolutionManager.OptimizationId));
    }

    // ── Intensity ─────────────────────────────────────────────────────

    [Fact]
    public void Intensity_SerializationRoundTrip_PreservesValue()
    {
        var original = new GameProfile
        {
            Id = "intensity_test",
            Intensity = OptimizationIntensity.Competitive
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<GameProfile>(json)!;

        Assert.Equal(OptimizationIntensity.Competitive, deserialized.Intensity);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static GameProfile CreateAllDisabledProfile() => new()
    {
        SuppressServices = false,
        SwitchPowerPlan = false,
        SetTimerResolution = false,
        BoostProcessPriority = false,
        UsePerformanceCoresOnly = false,
        PinToVCacheCcd = false,
        EnableEfficiencyMode = false,
    };

    private static string GetOptimizationId(string className) => className switch
    {
        nameof(ServiceSuppressor) => ServiceSuppressor.OptimizationId,
        nameof(PowerPlanSwitcher) => PowerPlanSwitcher.OptimizationId,
        nameof(TimerResolutionManager) => TimerResolutionManager.OptimizationId,
        nameof(ProcessPriorityBooster) => ProcessPriorityBooster.OptimizationId,
        nameof(HybridCpuDetector) => HybridCpuDetector.OptimizationId,
        nameof(EfficiencyModeController) => EfficiencyModeController.OptimizationId,
        _ => throw new ArgumentException($"Unknown optimization class: {className}")
    };
}
