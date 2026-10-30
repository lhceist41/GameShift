using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
using Xunit;

namespace GameShift.Tests.SystemTweaks;

/// <summary>
/// Restore All over every ledger: what gets listed, which reverter each item goes to, and that
/// results are reported truthfully. Runs against a fake system, so no registry, BCD, service or
/// settings file is touched.
/// </summary>
public class LastingChangesInventoryTests
{
    private static KernelTuningSetting Kernel(string id) => KernelTuningManager.AllSettings.Single(s => s.Id == id);

    private static AppliedDpcFix BcdEntry(string id) => new()
    {
        FixId = "bcd_" + id,
        ActionType = "BcdEdit",
        Description = Kernel(id).DisplayName,
        PreviousValue = Kernel(id).RevertArgs,
        Target = Kernel(id).ApplyArgs,
        RequiresReboot = true,
    };

    private static LastingChangesInventory Inventory(FakeSystem system) => new(system, Serilog.Core.Logger.None);

    [Fact]
    public void EveryLedgerKind_YieldsAnItem()
    {
        var system = new FakeSystem();
        system.Settings.VbsHvciDisabledByGameShift = true;
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks["FakeTweak"] = new TweakState { IsAppliedByGameShift = true };
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "ndis_interrupt_moderation", ActionType = "SetNetAdapterProperty" });
        system.Settings.AppliedDpcFixes.Add(BcdEntry("tscsyncpolicy"));
        system.Bcd["tscsyncpolicy"] = "Enhanced";
        system.CoreReservationPresent = true;
        system.StartupEntries.Add(new RenamedStartupEntry(true, "~Discord"));

        var list = Inventory(system).List();

        Assert.Equal(Enum.GetValues<LastingChangeKind>().ToHashSet(), list.Items.Select(i => i.Kind).ToHashSet());
        Assert.Empty(list.Warnings);
        Assert.Equal(
            new[] { LastingChangeKind.CpuCoreReservation, LastingChangeKind.RenamedStartupEntry },
            list.NeedingOwnConfirmation.Select(i => i.Kind).OrderBy(k => k));
    }

    [Fact]
    public void TweakNotAppliedByGameShift_IsNotListed()
    {
        var system = new FakeSystem();
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks["FakeTweak"] = new TweakState { IsAppliedByGameShift = false };

        Assert.Empty(Inventory(system).List().Items);
    }

    [Fact]
    public void NetworkAdapterFix_IsListed_AndRevertedThroughTheDpcDoctorEngine()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = "ndis_interrupt_moderation",
            ActionType = "SetNetAdapterProperty",
            Description = "Disable interrupt moderation",
        });
        var inventory = Inventory(system);

        var item = Assert.Single(inventory.List().Items);
        Assert.Equal(LastingChangeKind.LatencyDoctorFix, item.Kind);
        Assert.Equal("Disable interrupt moderation", item.DisplayName);

        var result = Assert.Single(inventory.Undo([item]));
        Assert.Equal(LastingChangeOutcome.Undone, result.Outcome);
        Assert.Equal(["dpc:ndis_interrupt_moderation"], system.Calls);
    }

    [Fact]
    public void KernelSettingEntries_RouteToTheKernelReverter_NeverTheDpcDoctorEngine()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(BcdEntry("disabledynamictick"));
        system.Settings.PendingRebootFixes.Add("bcd_disabledynamictick");
        system.Bcd["disabledynamictick"] = "Yes";
        var inventory = Inventory(system);

        var item = Assert.Single(inventory.List().Items);
        Assert.Equal(LastingChangeKind.KernelSetting, item.Kind);

        var result = Assert.Single(inventory.Undo([item]));

        Assert.Equal(LastingChangeOutcome.Undone, result.Outcome);
        Assert.DoesNotContain(system.Calls, c => c.StartsWith("dpc:"));
        Assert.Equal(["kernel:disabledynamictick", "forget:disabledynamictick"], system.Calls);
        Assert.Empty(system.Settings.AppliedDpcFixes);
        Assert.Empty(system.Settings.PendingRebootFixes);
    }

    [Fact]
    public void BootValueWithoutLedgerEntry_IsNotListed()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(BcdEntry("x2apicpolicy"));
        system.Bcd["tscsyncpolicy"] = "Enhanced";
        system.Bcd["hypervisorlaunchtype"] = "Off";

        Assert.Empty(Inventory(system).List().Items);
    }

    [Fact]
    public void LedgerEntry_WhoseBootValueNoLongerMatches_IsNotListed()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(BcdEntry("hypervisorlaunchtype"));
        system.Bcd["hypervisorlaunchtype"] = "Auto";

        Assert.Empty(Inventory(system).List().Items);
    }

    [Fact]
    public void UnreadableBootConfiguration_ListsNoKernelSetting_AndSaysSo()
    {
        // useplatformclock's GameShift value is "not set", which an unread value would look like.
        var system = new FakeSystem { BcdReadable = false };
        system.Settings.AppliedDpcFixes.Add(BcdEntry("useplatformclock"));

        var list = Inventory(system).List();

        Assert.Empty(list.Items);
        Assert.Single(list.Warnings);
    }

    [Fact]
    public void RemovedValue_CountsAsGameShiftsValue_OnlyWhenItWasRead()
    {
        var setting = Kernel("useplatformclock");

        Assert.True(LastingChangesInventory.IsAtGameShiftValue(setting, new Dictionary<string, string?> { ["useplatformclock"] = null }));
        Assert.False(LastingChangesInventory.IsAtGameShiftValue(setting, new Dictionary<string, string?>()));
        Assert.False(LastingChangesInventory.IsAtGameShiftValue(setting, new Dictionary<string, string?> { ["useplatformclock"] = "Yes" }));
    }

    [Fact]
    public void FixedValueKernelRevert_SaysWhatItWrites_NotThatItRestores()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(BcdEntry("useplatformclock"));
        system.Settings.AppliedDpcFixes.Add(BcdEntry("tscsyncpolicy"));
        system.Bcd["tscsyncpolicy"] = "Enhanced";
        var inventory = Inventory(system);
        var items = inventory.List().Items;

        Assert.Equal(
            "Sets the boot value useplatformclock to true. GameShift did not record the value it had before.",
            items.Single(i => i.Key == "useplatformclock").UndoDescription);
        Assert.Equal(
            "Removes the boot value so Windows uses its default again.",
            items.Single(i => i.Key == "tscsyncpolicy").UndoDescription);
        Assert.All(KernelTuningManager.AllSettings,
            s => Assert.DoesNotContain("back to", LastingChangesInventory.DescribeKernelRevert(s, done: false)));

        var text = LastingChangesInventory.DescribeResults(inventory.Undo(items));

        Assert.Contains(
            "• Remove Platform Clock Override (boot setting): Set the boot value useplatformclock to true. GameShift did not record the value it had before.",
            text);
    }

    [Fact]
    public void KernelSetting_ChangedAfterListing_IsLeftAlone()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(BcdEntry("x2apicpolicy"));
        system.Bcd["x2apicpolicy"] = "Enable";
        var inventory = Inventory(system);
        var item = Assert.Single(inventory.List().Items);

        system.Bcd["x2apicpolicy"] = "Disable";
        var result = Assert.Single(inventory.Undo([item]));

        Assert.Equal(LastingChangeOutcome.LeftAlone, result.Outcome);
        Assert.Empty(system.Calls);
        Assert.Single(system.Settings.AppliedDpcFixes);
    }

    [Fact]
    public void FailedItems_AreReportedFailed_AndKeepTheirLedgerEntries()
    {
        var system = new FakeSystem { AllRevertsFail = true, CoreReservationPresent = true };
        system.Settings.VbsHvciDisabledByGameShift = true;
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks["FakeTweak"] = new TweakState { IsAppliedByGameShift = true };
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "msi_mode_gpu", ActionType = "RegistrySet" });
        system.Settings.AppliedDpcFixes.Add(BcdEntry("tscsyncpolicy"));
        system.Settings.PendingRebootFixes.Add("bcd_tscsyncpolicy");
        system.Bcd["tscsyncpolicy"] = "Enhanced";
        system.StartupEntries.Add(new RenamedStartupEntry(false, "~Steam"));
        var inventory = Inventory(system);
        var list = inventory.List();

        var results = inventory.Undo(list.Items, list.NeedingOwnConfirmation);

        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.Failed, r.Outcome));
        Assert.DoesNotContain(system.Calls, c => c.StartsWith("forget:"));
        Assert.True(system.Settings.VbsHvciDisabledByGameShift);
        Assert.True(system.Settings.SystemTweaks.AppliedTweaks.ContainsKey("FakeTweak"));
        Assert.Equal(2, system.Settings.AppliedDpcFixes.Count);
        Assert.Equal(["bcd_tscsyncpolicy"], system.Settings.PendingRebootFixes);

        // Still listed, so the user can try again.
        Assert.Equal(6, inventory.List().Items.Count);
    }

    [Fact]
    public void ReverterThatThrows_IsReportedFailed_AndTheRestStillRun()
    {
        var system = new FakeSystem { ThrowOnDpcRevert = true };
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "power_plan", ActionType = "PowerPlanSetting" });
        system.Settings.VbsHvciDisabledByGameShift = true;
        var inventory = Inventory(system);

        var results = inventory.Undo(inventory.List().Items);

        Assert.Equal(LastingChangeOutcome.Undone, results.Single(r => r.Change.Kind == LastingChangeKind.MemoryIntegrity).Outcome);
        Assert.Equal(LastingChangeOutcome.Failed, results.Single(r => r.Change.Kind == LastingChangeKind.LatencyDoctorFix).Outcome);
    }

    [Fact]
    public void RecordedTweakClass_ResolvesThroughTheRegisteredTweaks()
    {
        var manager = new SystemTweaksManager();
        var system = new FakeSystem { FindTweakOverride = manager.GetTweakByClassName };
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks[nameof(Core.SystemTweaks.Tweaks.DisableMemoryIntegrity)] =
            new TweakState { IsAppliedByGameShift = true };

        var item = Assert.Single(Inventory(system).List().Items);

        Assert.Equal(new Core.SystemTweaks.Tweaks.DisableMemoryIntegrity().Name, item.DisplayName);
        Assert.True(item.RequiresRestart);

        // Every tweak class stays registered, so a ledger entry for one whose card is gone still resolves.
        var tweakTypes = typeof(ISystemTweak).Assembly.GetTypes()
            .Where(t => typeof(ISystemTweak).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });
        Assert.All(tweakTypes, t => Assert.NotNull(manager.GetTweakByClassName(t.Name)));
    }

    [Fact]
    public void UnknownTweakClass_IsListed_AndReportedFailedWithoutLosingItsEntry()
    {
        var system = new FakeSystem();
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks["TweakFromAnotherVersion"] = new TweakState { IsAppliedByGameShift = true };
        var inventory = Inventory(system);

        var item = Assert.Single(inventory.List().Items);
        var result = Assert.Single(inventory.Undo([item]));

        Assert.Equal(LastingChangeOutcome.Failed, result.Outcome);
        Assert.Empty(system.Calls);
        Assert.True(system.Settings.SystemTweaks.AppliedTweaks.ContainsKey("TweakFromAnotherVersion"));
    }

    [Fact]
    public void StartupEntriesAndCoreReservation_AreNeverUndoneWithoutTheirOwnConfirmation()
    {
        var system = new FakeSystem { CoreReservationPresent = true };
        system.StartupEntries.Add(new RenamedStartupEntry(true, "~Discord"));
        var inventory = Inventory(system);
        var list = inventory.List();

        var startup = list.Items.Single(i => i.Kind == LastingChangeKind.RenamedStartupEntry);
        Assert.Equal("Startup app \"Discord\" (this user)", startup.DisplayName);

        // Everything passed as if it were recorded: nothing unrecorded is touched.
        var results = inventory.Undo(list.Items);
        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.LeftAlone, r.Outcome));
        Assert.Empty(system.Calls);

        results = inventory.Undo(list.Items, [startup]);
        Assert.Equal(["startup:~Discord"], system.Calls);
        Assert.Equal(LastingChangeOutcome.Undone, results.Single(r => r.Change == startup).Outcome);
    }

    [Fact]
    public void MemoryIntegrity_IsUndoneBeforeTheTweaks_WhateverTheInputOrder()
    {
        var system = new FakeSystem();
        system.Settings.VbsHvciDisabledByGameShift = true;
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks["FakeTweak"] = new TweakState { IsAppliedByGameShift = true };
        var inventory = Inventory(system);

        inventory.Undo(inventory.List().Items.Reverse());

        Assert.Equal(["vbs", "tweak:FakeTweak"], system.Calls);
    }

    [Fact]
    public void RemoveLedgerEntries_DropsOnlyThatSettingsEntries()
    {
        var settings = new AppSettings();
        settings.AppliedDpcFixes.Add(BcdEntry("tscsyncpolicy"));
        settings.AppliedDpcFixes.Add(BcdEntry("x2apicpolicy"));
        settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "msi_mode_gpu" });
        settings.PendingRebootFixes.AddRange(["bcd_tscsyncpolicy", "bcd_x2apicpolicy", "msi_mode_gpu"]);

        KernelTuningManager.RemoveLedgerEntries(settings, Kernel("tscsyncpolicy"));

        Assert.Equal(["bcd_x2apicpolicy", "msi_mode_gpu"], settings.AppliedDpcFixes.Select(f => f.FixId));
        Assert.Equal(["bcd_x2apicpolicy", "msi_mode_gpu"], settings.PendingRebootFixes);
    }

    [Fact]
    public void SelectRestorableNames_SkipsEntriesWhoseOriginalNameIsTaken()
    {
        var names = StartupAppManager.SelectRestorableNames(["~Discord", "discord", "~Steam", "~", "Spotify"]);

        Assert.Equal(["~Steam"], names);
    }

    [Fact]
    public void DescribeResults_ListsFailuresWithTheirReason_AndAsksForARestart()
    {
        var restart = new LastingChange { Kind = LastingChangeKind.KernelSetting, Key = "a", DisplayName = "A", UndoDescription = "", RequiresRestart = true };
        var other = new LastingChange { Kind = LastingChangeKind.SystemTweak, Key = "b", DisplayName = "B", UndoDescription = "" };

        var text = LastingChangesInventory.DescribeResults(
        [
            new(restart, LastingChangeOutcome.Undone, "ok"),
            new(other, LastingChangeOutcome.Failed, "access denied"),
        ]);

        Assert.Contains("• B: access denied", text);
        Assert.Contains("Restart your PC", text);
    }

    private sealed class FakeTweak : ISystemTweak
    {
        public string Name => "Fake tweak";
        public string Description => "";
        public string Category => "";
        public bool RequiresReboot => false;
        public bool DetectIsApplied() => true;
        public string? Apply() => null;
        public bool Revert(string? originalValuesJson) => true;
    }

    /// <summary>
    /// Models the real reverters' ledger behavior: each drops its own entry only on success.
    /// </summary>
    private sealed class FakeSystem : ILastingChangeSystem
    {
        public AppSettings Settings { get; } = new();
        public Dictionary<string, string?> Bcd { get; } = KernelTuningManager.AllSettings
            .ToDictionary(s => s.BcdKey, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        public bool BcdReadable { get; init; } = true;
        public bool CoreReservationPresent { get; set; }
        public List<RenamedStartupEntry> StartupEntries { get; } = new();
        public bool AllRevertsFail { get; init; }
        public bool ThrowOnDpcRevert { get; init; }
        public Func<string, ISystemTweak?>? FindTweakOverride { get; init; }
        public List<string> Calls { get; } = new();

        public AppSettings LoadSettings() => Settings;

        public ISystemTweak? FindTweak(string className) =>
            FindTweakOverride != null ? FindTweakOverride(className)
            : className == "FakeTweak" ? new FakeTweak() : null;

        public bool RevertTweak(ISystemTweak tweak)
        {
            var className = tweak.GetType().Name;
            Calls.Add("tweak:" + className);
            if (AllRevertsFail) return false;
            Settings.SystemTweaks?.AppliedTweaks.Remove(className);
            return true;
        }

        public DpcFixResult RevertDpcFix(string fixId)
        {
            Calls.Add("dpc:" + fixId);
            if (ThrowOnDpcRevert) throw new InvalidOperationException("boom");
            if (AllRevertsFail) return new DpcFixResult { Success = false, Message = "failed" };
            Settings.AppliedDpcFixes.RemoveAll(f => f.FixId == fixId);
            Settings.PendingRebootFixes.Remove(fixId);
            return new DpcFixResult { Success = true, Message = "reverted" };
        }

        public bool TryReadBcdValues(out IReadOnlyDictionary<string, string?> values)
        {
            values = new Dictionary<string, string?>(Bcd, StringComparer.OrdinalIgnoreCase);
            return BcdReadable;
        }

        public (bool Success, string Message) RevertKernelSetting(KernelTuningSetting setting)
        {
            Calls.Add("kernel:" + setting.Id);
            return AllRevertsFail ? (false, "bcdedit failed") : (true, "reverted");
        }

        public void ForgetKernelSetting(KernelTuningSetting setting)
        {
            Calls.Add("forget:" + setting.Id);
            KernelTuningManager.RemoveLedgerEntries(Settings, setting);
        }

        public bool IsCoreReservationPresent() => CoreReservationPresent;

        public string? RemoveCoreReservation()
        {
            Calls.Add("core");
            return AllRevertsFail ? "Registry delete failed" : null;
        }

        public bool ReEnableMemoryIntegrity()
        {
            Calls.Add("vbs");
            if (AllRevertsFail) return false;
            Settings.VbsHvciDisabledByGameShift = false;
            return true;
        }

        public IReadOnlyList<RenamedStartupEntry> ReadRenamedStartupEntries() => StartupEntries;

        public string? RestoreStartupEntry(RenamedStartupEntry entry)
        {
            Calls.Add("startup:" + entry.ValueName);
            return AllRevertsFail ? "Access denied" : null;
        }
    }
}
