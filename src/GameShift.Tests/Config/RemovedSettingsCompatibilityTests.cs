using System.Text.Json;
using GameShift.Core.Config;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// Settings files that still carry the removed timer resolution, GPU vendor, memory threshold and
/// quick-switch values keep loading, and a save keeps every field that is still in use. Uses the
/// serializer options <see cref="SettingsManager"/> uses (defaults to read, indented to write)
/// without touching it.
/// </summary>
public class RemovedSettingsCompatibilityTests
{
    // Shape written by 3.9.0's settings save, including a custom game profile.
    private const string SettingsWithRemovedFields = """
        {
          "StartWithWindows": false,
          "StartMinimized": false,
          "LogLevel": "Debug",
          "MemoryThresholdMB": 2048,
          "TimerResolution100ns": 10000,
          "GpuVendorOverride": "NVIDIA",
          "QuickSwitchProfileId": "profile-A",
          "DefaultDpcThresholdMicroseconds": 2500,
          "DismissedTips": [ "example_tip" ],
          "GlobalHotkeyBinding": "Ctrl+Alt+G",
          "PingTarget": "1.1.1.1",
          "AdvancedMode": true,
          "BackgroundModeRetired": true,
          "GameProfiles": {
            "Enabled": false,
            "CustomProfiles": [
              {
                "Id": "custom_example",
                "DisplayName": "Example Game",
                "ProcessNames": [ "example.exe" ],
                "GamingStandbyThresholdMB": 1024,
                "Notes": [ "kept" ]
              }
            ]
          },
          "SkippedUpdateVersion": "3.8.8"
        }
        """;

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    private static void AssertKeptFields(AppSettings settings)
    {
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.StartMinimized);
        Assert.Equal("Debug", settings.LogLevel);
        Assert.Equal(2500, settings.DefaultDpcThresholdMicroseconds);
        Assert.Contains("example_tip", settings.DismissedTips);
        Assert.Equal("Ctrl+Alt+G", settings.GlobalHotkeyBinding);
        Assert.Equal("1.1.1.1", settings.PingTarget);
        Assert.True(settings.AdvancedMode);
        Assert.True(settings.BackgroundModeRetired);
        Assert.Equal("3.8.8", settings.SkippedUpdateVersion);

        Assert.False(settings.GameProfiles!.Enabled);
        var profile = Assert.Single(settings.GameProfiles.CustomProfiles);
        Assert.Equal("custom_example", profile.Id);
        Assert.Equal(new[] { "example.exe" }, profile.ProcessNames);
        Assert.Equal(1024, profile.GamingStandbyThresholdMB);
        Assert.Equal(new[] { "kept" }, profile.Notes);
    }

    [Fact]
    public void SettingsWithRemovedFields_Load_AndKeepEveryOtherField()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(SettingsWithRemovedFields)!;

        AssertKeptFields(loaded);
    }

    [Fact]
    public void SettingsWithRemovedFields_Save_KeepsEveryOtherField_AndDropsTheRemovedOnes()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(SettingsWithRemovedFields)!;

        var saved = JsonSerializer.Serialize(loaded, SaveOptions);
        AssertKeptFields(JsonSerializer.Deserialize<AppSettings>(saved)!);

        using var doc = JsonDocument.Parse(saved);
        foreach (var removed in new[] { "MemoryThresholdMB", "TimerResolution100ns", "GpuVendorOverride", "QuickSwitchProfileId" })
            Assert.False(doc.RootElement.TryGetProperty(removed, out _), removed);
    }
}
