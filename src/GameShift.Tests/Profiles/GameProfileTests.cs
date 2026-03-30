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
    [InlineData(nameof(MemoryOptimizer))]
    [InlineData(nameof(VisualEffectReducer))]
    [InlineData(nameof(NetworkOptimizer))]
    [InlineData(nameof(HybridCpuDetector))]
    [InlineData(nameof(MpoToggle))]
    [InlineData(nameof(CompetitiveMode))]
    [InlineData(nameof(GpuDriverOptimizer))]
    [InlineData(nameof(ScheduledTaskSuppressor))]
    [InlineData(nameof(CpuParkingManager))]
    [InlineData(nameof(IoPriorityManager))]
    [InlineData(nameof(EfficiencyModeController))]
    public void IsOptimizationEnabled_AllKnownOptimizations_HaveExplicitMapping(string className)
    {
        // Every known optimization class must have an explicit case in the switch,
        // not fall through to the _ => true default. We verify this by creating a
        // profile with all toggles set to false — if it returns true, the mapping is missing.
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

        // Main optimizations are all enabled by default (except hybrid CPU and competitive)
        Assert.True(profile.IsOptimizationEnabled(ServiceSuppressor.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(PowerPlanSwitcher.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(TimerResolutionManager.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(ProcessPriorityBooster.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(MemoryOptimizer.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(VisualEffectReducer.OptimizationId));
        Assert.True(profile.IsOptimizationEnabled(NetworkOptimizer.OptimizationId));
    }

    [Fact]
    public void IsOptimizationEnabled_DefaultProfile_OptInTogglesDisabled()
    {
        var profile = GameProfile.CreateDefault();

        // Opt-in toggles default to false
        Assert.False(profile.IsOptimizationEnabled(HybridCpuDetector.OptimizationId));
        Assert.False(profile.IsOptimizationEnabled(MpoToggle.OptimizationId));
        Assert.False(profile.IsOptimizationEnabled(CompetitiveMode.OptimizationId));
    }

    // ── OptimizationId consistency ────────────────────────────────────

    [Fact]
    public void OptimizationId_Constants_AreNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ServiceSuppressor.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(PowerPlanSwitcher.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(TimerResolutionManager.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(ProcessPriorityBooster.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(MemoryOptimizer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(VisualEffectReducer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(NetworkOptimizer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(HybridCpuDetector.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(MpoToggle.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(CompetitiveMode.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(GpuDriverOptimizer.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(ScheduledTaskSuppressor.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(CpuParkingManager.OptimizationId));
        Assert.False(string.IsNullOrWhiteSpace(IoPriorityManager.OptimizationId));
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
            MemoryOptimizer.OptimizationId,
            VisualEffectReducer.OptimizationId,
            NetworkOptimizer.OptimizationId,
            HybridCpuDetector.OptimizationId,
            MpoToggle.OptimizationId,
            CompetitiveMode.OptimizationId,
            GpuDriverOptimizer.OptimizationId,
            ScheduledTaskSuppressor.OptimizationId,
            CpuParkingManager.OptimizationId,
            IoPriorityManager.OptimizationId,
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
            DisableMpo = true,
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
            original.IsOptimizationEnabled(MpoToggle.OptimizationId),
            deserialized.IsOptimizationEnabled(MpoToggle.OptimizationId));
    }

    // ── Intensity ─────────────────────────────────────────────────────

    [Fact]
    public void CreateDefault_Intensity_IsCasual()
    {
        var profile = GameProfile.CreateDefault();
        Assert.Equal(OptimizationIntensity.Casual, profile.Intensity);
    }

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

    [Fact]
    public void Intensity_DoesNotAffect_IsOptimizationEnabled()
    {
        var casual = new GameProfile { Intensity = OptimizationIntensity.Casual };
        var competitive = new GameProfile { Intensity = OptimizationIntensity.Competitive };

        // IsOptimizationEnabled should be the same regardless of intensity
        Assert.Equal(
            casual.IsOptimizationEnabled(ServiceSuppressor.OptimizationId),
            competitive.IsOptimizationEnabled(ServiceSuppressor.OptimizationId));
        Assert.Equal(
            casual.IsOptimizationEnabled(MpoToggle.OptimizationId),
            competitive.IsOptimizationEnabled(MpoToggle.OptimizationId));
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static GameProfile CreateAllDisabledProfile() => new()
    {
        SuppressServices = false,
        SwitchPowerPlan = false,
        SetTimerResolution = false,
        BoostProcessPriority = false,
        OptimizeMemory = false,
        ReduceVisualEffects = false,
        OptimizeNetwork = false,
        UsePerformanceCoresOnly = false,
        DisableMpo = false,
        EnableCompetitiveMode = false,
        EnableGpuOptimization = false,
        SuppressScheduledTasks = false,
        UnparkCpuCores = false,
        ManageIoPriority = false,
        EnableEfficiencyMode = false,
    };

    private static string GetOptimizationId(string className) => className switch
    {
        nameof(ServiceSuppressor) => ServiceSuppressor.OptimizationId,
        nameof(PowerPlanSwitcher) => PowerPlanSwitcher.OptimizationId,
        nameof(TimerResolutionManager) => TimerResolutionManager.OptimizationId,
        nameof(ProcessPriorityBooster) => ProcessPriorityBooster.OptimizationId,
        nameof(MemoryOptimizer) => MemoryOptimizer.OptimizationId,
        nameof(VisualEffectReducer) => VisualEffectReducer.OptimizationId,
        nameof(NetworkOptimizer) => NetworkOptimizer.OptimizationId,
        nameof(HybridCpuDetector) => HybridCpuDetector.OptimizationId,
        nameof(MpoToggle) => MpoToggle.OptimizationId,
        nameof(CompetitiveMode) => CompetitiveMode.OptimizationId,
        nameof(GpuDriverOptimizer) => GpuDriverOptimizer.OptimizationId,
        nameof(ScheduledTaskSuppressor) => ScheduledTaskSuppressor.OptimizationId,
        nameof(CpuParkingManager) => CpuParkingManager.OptimizationId,
        nameof(IoPriorityManager) => IoPriorityManager.OptimizationId,
        nameof(EfficiencyModeController) => EfficiencyModeController.OptimizationId,
        _ => throw new ArgumentException($"Unknown optimization class: {className}")
    };
}
