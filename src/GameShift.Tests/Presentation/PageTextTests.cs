using System.Globalization;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>
/// Profiles switches by module id with the During games names, System numbers in one culture,
/// and Activity labels that never show a type's code name.
/// </summary>
public class PageTextTests
{
    [Fact]
    public void ProfileModules_HasOneRowPerSwitchedModule()
    {
        var ids = ProfileModules.Rows.Select(r => r.ModuleName).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(ModuleSwitch.HasSwitch(id)));
        Assert.Equal(
            new[]
            {
                ServiceSuppressor.OptimizationId, PowerPlanSwitcher.OptimizationId,
                TimerResolutionManager.OptimizationId, ProcessPriorityBooster.OptimizationId,
                HybridCpuDetector.OptimizationId, EfficiencyModeController.OptimizationId,
            }.Order(),
            ids.Order());
    }

    [Fact]
    public void ProfileModules_UseTheDuringGamesTitleAndEffect()
    {
        foreach (var row in ProfileModules.Rows)
        {
            var text = SessionModuleText.For(row.ModuleName, "");
            Assert.Equal(text.Title, row.Title);
            Assert.Equal(text.Effect, row.Effect);
            Assert.NotEqual(row.ModuleName, row.Title);
        }
    }

    [Fact]
    public void ProfileModules_TimerAndPinningAreAdvanced()
    {
        var advanced = ProfileModules.Rows.Where(r => r.IsAdvanced).Select(r => r.ModuleName).Order();

        Assert.Equal(new[] { HybridCpuDetector.OptimizationId, TimerResolutionManager.OptimizationId }.Order(), advanced);
    }

    [Fact]
    public void ProfileModules_EachRowWritesItsOwnSwitch()
    {
        foreach (var row in ProfileModules.Rows)
        {
            var profile = new GameProfile();
            foreach (var other in ProfileModules.Rows)
                ModuleSwitch.TrySet(profile, other.ModuleName, false);

            Assert.True(ModuleSwitch.TrySet(profile, row.ModuleName, true));

            foreach (var other in ProfileModules.Rows)
                Assert.Equal(other.ModuleName == row.ModuleName, profile.IsOptimizationEnabled(other.ModuleName));
        }
    }

    [Fact]
    public void SystemText_UsesTheInvariantCultureOnAGermanPc()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal("15.9 GB", SystemText.RamTotal(new SystemInfoGatherer.RamInfo { TotalBytes = 17_070_000_000 }));
            Assert.Equal(
                "PID 4242 | CPU 1234.5 s | 2048 MB",
                SystemText.ProcessDetails(new SystemInfoGatherer.ProcessInfo { Pid = 4242, CpuSeconds = 1234.54, MemoryMB = 2048 }));
            Assert.Equal(
                "Driver 32.0.15.6094 | 8.0 GB VRAM",
                SystemText.GpuDetails(new SystemInfoGatherer.GpuInfo { DriverVersion = "32.0.15.6094", AdapterRamBytes = 8L * 1024 * 1024 * 1024 }));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void SystemText_LeavesOutValuesWindowsDidNotReport()
    {
        Assert.Equal("Driver 1.2", SystemText.GpuDetails(new SystemInfoGatherer.GpuInfo { DriverVersion = "1.2", AdapterRamBytes = 0 }));
        Assert.Equal("2 module(s)", SystemText.RamDetails(new SystemInfoGatherer.RamInfo { ModuleCount = 2, SpeedMHz = 0 }));
        Assert.Equal("2 module(s) @ 6000 MHz", SystemText.RamDetails(new SystemInfoGatherer.RamInfo { ModuleCount = 2, SpeedMHz = 6000 }));

        var gb = 1024L * 1024 * 1024;
        Assert.Equal(
            "954 GB total | 120 GB free",
            SystemText.StorageDetails(new SystemInfoGatherer.DriveInfo2 { SizeBytes = 954 * gb, FreeSpaceBytes = 120 * gb, MediaType = "SSD" }));
        Assert.Equal(
            "954 GB | SSD",
            SystemText.StorageDetails(new SystemInfoGatherer.DriveInfo2 { SizeBytes = 954 * gb, FreeSpaceBytes = 0, MediaType = "SSD" }));
    }

    [Theory]
    [InlineData("GameStarted", ActivityKind.Game, "Game started")]
    [InlineData("GameStopped", ActivityKind.Game, "Game closed")]
    [InlineData("SessionEnded", ActivityKind.Session, "Session ended")]
    [InlineData("OptimizationApplied", ActivityKind.Applied, "Change applied")]
    [InlineData("OptimizationFailed", ActivityKind.Failed, "Change failed")]
    [InlineData("OptimizationReverted", ActivityKind.Reverted, "Change undone")]
    [InlineData("SomethingNew", ActivityKind.Other, "Event")]
    public void ActivityText_GivesEachTypeAPlainLabel(string type, ActivityKind kind, string label)
    {
        Assert.Equal(kind, ActivityText.Kind(type));
        Assert.Equal(label, ActivityText.Label(type));
    }
}
