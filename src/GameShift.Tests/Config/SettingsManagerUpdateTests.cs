using System.IO;
using System.Linq;
using GameShift.Core.Config;
using GameShift.Tests.TestHelpers;
using Xunit;

namespace GameShift.Tests.Config;

/// <summary>
/// Tests for the transactional <see cref="SettingsManager.Update"/> path that the DPC settings
/// migration (DpcFixEngine / DpcDoctorViewModel) relies on: a per-field load-mutate-save under the
/// shared lock that (a) never clobbers fields it did not touch and (b) lets a check-then-add run
/// idempotently. These run against an isolated settings.json via the internal path override and are
/// serialized with other config-state tests.
/// </summary>
[Collection("ConfigState")]
public class SettingsManagerUpdateTests
{
    [Fact]
    public void Update_PersistsMutation_WithoutClobberingOtherFields()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            // Seed an unrelated field that the Update below must NOT touch.
            SettingsManager.Save(new AppSettings { QuickSwitchProfileId = "profile-A" });

            SettingsManager.Update(s => s.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "f1" }));

            var reloaded = SettingsManager.Load();
            Assert.Equal("profile-A", reloaded.QuickSwitchProfileId);            // unrelated field preserved
            Assert.Single(reloaded.AppliedDpcFixes);
            Assert.Equal("f1", reloaded.AppliedDpcFixes[0].FixId);
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }

    [Fact]
    public void Update_CheckThenAdd_IsIdempotent()
    {
        using var temp = new TempPath();
        SettingsManager.SettingsFilePathOverride = temp.GetFile("settings.json");
        try
        {
            SettingsManager.Save(new AppSettings());

            // The exact dedup shape DpcFixEngine.ApplyFix / ToggleKernelTuningSetting use: a check
            // inside the Update lambda. Running it twice must yield exactly one entry.
            for (int i = 0; i < 2; i++)
            {
                SettingsManager.Update(s =>
                {
                    if (!s.AppliedDpcFixes.Any(f => f.FixId == "x"))
                        s.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "x" });
                });
            }

            var reloaded = SettingsManager.Load();
            Assert.Single(reloaded.AppliedDpcFixes, f => f.FixId == "x");
        }
        finally
        {
            SettingsManager.SettingsFilePathOverride = null;
        }
    }
}
