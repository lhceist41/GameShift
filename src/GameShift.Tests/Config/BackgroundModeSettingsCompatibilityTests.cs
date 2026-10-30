using System.Text.Json;
using GameShift.Core.Config;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// Settings files from versions with Background Mode keep loading after its removal, and the
/// cleanup flag round-trips without losing other fields. Uses the serializer options
/// <see cref="SettingsManager"/> uses (defaults to read, indented to write) without touching it.
/// </summary>
public class BackgroundModeSettingsCompatibilityTests
{
    // Shape written by 3.9.0's settings save with Background Mode turned on.
    private const string SettingsWithBackgroundMode = """
        {
          "StartMinimized": false,
          "DefaultDpcThresholdMicroseconds": 2500,
          "QuickSwitchProfileId": "profile-A",
          "PingTarget": "1.1.1.1",
          "BackgroundMode": {
            "Enabled": true,
            "StandbyListCleanerEnabled": true,
            "StandbyListStandbyThresholdMB": 0,
            "StandbyListFreeMemoryMinMB": 0,
            "StandbyListPollIntervalMs": 1000,
            "StandbyListOnlyDuringGaming": true,
            "TimerResolutionEnabled": true,
            "TimerResolution100ns": 5000,
            "PowerPlanEnabled": true,
            "IdleTimeoutMinutes": 15,
            "TaskDeferralEnabled": true,
            "ProBalanceEnabled": true,
            "ProcessPriorityEnabled": false,
            "ProcessPriorityRules": { "example.exe": "BelowNormal" }
          }
        }
        """;

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    [Fact]
    public void SettingsWithBackgroundMode_Load_AndKeepEveryField()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(SettingsWithBackgroundMode)!;

        Assert.False(loaded.BackgroundModeRetired);
        Assert.NotNull(loaded.BackgroundMode);
        Assert.True(loaded.BackgroundMode!.Enabled);
        Assert.True(loaded.BackgroundMode.TaskDeferralEnabled);
        Assert.False(loaded.StartMinimized);
        Assert.Equal(2500, loaded.DefaultDpcThresholdMicroseconds);
        Assert.Equal("profile-A", loaded.QuickSwitchProfileId);
        Assert.Equal("1.1.1.1", loaded.PingTarget);
    }

    [Fact]
    public void SettingTheRetiredFlag_AndSaving_KeepsOtherFieldsAndBackgroundModeData()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(SettingsWithBackgroundMode)!;
        loaded.BackgroundModeRetired = true;

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(loaded, SaveOptions))!;

        Assert.True(reloaded.BackgroundModeRetired);
        Assert.True(reloaded.BackgroundMode!.Enabled);
        Assert.Equal("BelowNormal", reloaded.BackgroundMode.ProcessPriorityRules["example.exe"]);
        Assert.False(reloaded.StartMinimized);
        Assert.Equal(2500, reloaded.DefaultDpcThresholdMicroseconds);
        Assert.Equal("profile-A", reloaded.QuickSwitchProfileId);
        Assert.Equal("1.1.1.1", reloaded.PingTarget);
    }

    [Fact]
    public void SettingsWithBackgroundModeOff_Load()
    {
        var json = SettingsWithBackgroundMode.Replace("\"Enabled\": true", "\"Enabled\": false");

        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.False(loaded.BackgroundMode!.Enabled);
        Assert.Equal("profile-A", loaded.QuickSwitchProfileId);
    }
}
