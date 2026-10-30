using System.Text.Json;
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
    public void IsOptimizationEnabled_DefaultProfile_AllMainTogglesEnabled()
    {
        var profile = GameProfile.CreateDefault();

        Assert.True(profile.IsOptimizationEnabled(ServiceSuppressor.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(PowerPlanSwitcher.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(TimerResolutionManager.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(ProcessPriorityBooster.OptimizationId));
    }

    [Fact]
    public void IsOptimizationEnabled_DefaultProfile_EnablesHybridCpuDetector()
    {
        var profile = GameProfile.CreateDefault();

        // HybridCpuDetector is enabled by default because V-Cache CCD pinning (PinToVCacheCcd)
        // defaults on; the module no-ops on non-X3D / non-hybrid topologies.
        Assert.True(profile.IsOptimizationEnabled(HybridCpuDetector.OptimizationId));
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
