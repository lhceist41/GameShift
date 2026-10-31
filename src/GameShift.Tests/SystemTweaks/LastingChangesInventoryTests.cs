using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using GameShift.Core.SystemTweaks;
using GameShift.Core.SystemTweaks.Tweaks;
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
        system.Settings.SystemTweaks.AppliedTweaks[nameof(Core.SystemTweaks.Tweaks.DisableMpo)] =
            new TweakState { IsAppliedByGameShift = true };

        var item = Assert.Single(Inventory(system).List().Items);

        Assert.Equal(new Core.SystemTweaks.Tweaks.DisableMpo().Name, item.DisplayName);
        Assert.True(item.RequiresRestart);

        // Every tweak class stays registered, so a ledger entry for one whose card is gone still resolves.
        var tweakTypes = typeof(ISystemTweak).Assembly.GetTypes()
            .Where(t => typeof(ISystemTweak).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });
        Assert.All(tweakTypes, t => Assert.NotNull(manager.GetTweakByClassName(t.Name)));
    }

    [Fact]
    public void EveryRetiredTweak_IsListed_AndRevertedThroughItsRegisteredClass()
    {
        var manager = new SystemTweaksManager();
        var retired = manager.Tweaks
            .Where(SystemTweaksManager.IsRetired)
            .Where(t => t is not Core.SystemTweaks.Tweaks.DisableMemoryIntegrity)
            .ToList();
        var system = new FakeSystem { FindTweakOverride = manager.GetTweakByClassName };
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        foreach (var tweak in retired)
            system.Settings.SystemTweaks.AppliedTweaks[tweak.GetType().Name] = new TweakState { IsAppliedByGameShift = true };
        var inventory = Inventory(system);

        var items = inventory.List().Items;
        Assert.Equal(14, items.Count);
        Assert.All(items, i => Assert.Equal(LastingChangeKind.SystemTweak, i.Kind));
        Assert.Equal(retired.Select(t => t.Name).Order(), items.Select(i => i.DisplayName).Order());

        var results = inventory.Undo(items);

        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.Undone, r.Outcome));
        Assert.Equal(retired.Select(t => "tweak:" + t.GetType().Name).Order(), system.Calls.Order());
        Assert.Empty(system.Settings.SystemTweaks.AppliedTweaks);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MemoryIntegrity_FromEitherRecord_IsOneItem_UndoneOnce(bool toggleRecord, bool tweakRecord)
    {
        var manager = new SystemTweaksManager();
        var system = new FakeSystem { FindTweakOverride = manager.GetTweakByClassName };
        system.Settings.VbsHvciDisabledByGameShift = toggleRecord;
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        if (tweakRecord)
            system.Settings.SystemTweaks.AppliedTweaks[nameof(Core.SystemTweaks.Tweaks.DisableMemoryIntegrity)] =
                new TweakState { IsAppliedByGameShift = true, OriginalValues = "{}" };
        var inventory = Inventory(system);

        var item = Assert.Single(inventory.List().Items);
        Assert.Equal(LastingChangeKind.MemoryIntegrity, item.Kind);
        Assert.True(item.RequiresRestart);

        var result = Assert.Single(inventory.Undo([item]));

        Assert.Equal(LastingChangeOutcome.Undone, result.Outcome);
        Assert.Equal(["vbs"], system.Calls);
        Assert.False(VbsHvciToggle.IsDisabledByGameShift(system.Settings));
    }

    [Theory]
    [InlineData("disable_hags", "RegistrySet")]
    [InlineData("disable_dynamic_tick", "BcdEdit")]
    [InlineData("disable_hpet", "BcdEdit")]
    [InlineData("high_perf_power_plan", "PowerPlanSetting")]
    public void DroppedDpcDoctorFix_IsListed_AndRevertedThroughTheDpcDoctorEngine(string fixId, string actionType)
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = fixId, ActionType = actionType, Description = fixId });
        var inventory = Inventory(system);

        var item = Assert.Single(inventory.List().Items);
        Assert.Equal(LastingChangeKind.LatencyDoctorFix, item.Kind);

        var result = Assert.Single(inventory.Undo([item]));

        Assert.Equal(LastingChangeOutcome.Undone, result.Outcome);
        Assert.Equal(["dpc:" + fixId], system.Calls);
    }

    [Fact]
    public void EveryKernelSetting_StillAtGameShiftsValue_IsListed_AndRevertedThroughTheKernelReverter()
    {
        var system = new FakeSystem();
        foreach (var setting in KernelTuningManager.AllSettings)
        {
            system.Settings.AppliedDpcFixes.Add(BcdEntry(setting.Id));
            system.Bcd[setting.BcdKey] = setting.RecommendedValue == "<not set>" ? null : setting.RecommendedValue;
        }
        var inventory = Inventory(system);

        var items = inventory.List().Items;
        Assert.Equal(KernelTuningManager.AllSettings.Select(s => s.Id).Order(), items.Select(i => i.Key).Order());
        Assert.All(items, i => Assert.Equal(LastingChangeKind.KernelSetting, i.Kind));

        var results = inventory.Undo(items);

        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.Undone, r.Outcome));
        Assert.DoesNotContain(system.Calls, c => c.StartsWith("dpc:"));
        Assert.Empty(system.Settings.AppliedDpcFixes);
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

    // ── A retired tweak and the DPC Doctor fix for the same setting ─────

    private static readonly DateTime TweakTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ISystemTweak? PairTweak(string className) => className switch
    {
        nameof(DisableMpo) => new DisableMpo(),
        nameof(DisableUsbSelectiveSuspend) => new DisableUsbSelectiveSuspend(),
        nameof(OptimizeInterruptHandling) => new OptimizeInterruptHandling(),
        "FakeTweak" => new FakeTweak(),
        _ => null,
    };

    /// <summary>
    /// A ledger as 3.9.0 wrote it: the tweak's time in UTC, the fix's in local time, read back
    /// through the same JSON round trip as the settings file.
    /// </summary>
    private static AppSettings StackedLedger(string fixId, DateTime? tweakAt, DateTime? fixAt, Action<AppSettings>? more = null)
    {
        var settings = new AppSettings { SystemTweaks = new SystemTweaksSettings() };
        settings.SystemTweaks.AppliedTweaks[DpcFixEngine.RetiredTweakByFixId[fixId]] =
            new TweakState { IsAppliedByGameShift = true, AppliedAt = tweakAt ?? default };
        settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = fixId,
            Description = "Fix " + fixId,
            ActionType = "RegistrySet",
            AppliedAt = fixAt?.ToLocalTime() ?? default,
        });
        more?.Invoke(settings);
        return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    }

    private static FakeSystem PairSystem(AppSettings settings, params string[] failingKeys) =>
        new() { Settings = settings, FindTweakOverride = PairTweak, FailingKeys = failingKeys.ToHashSet() };

    [Theory]
    [InlineData("disable_mpo")]
    [InlineData("disable_usb_selective_suspend")]
    [InlineData("msi_mode_nvidia")]
    [InlineData("msi_mode_amd")]
    public void StackedPair_IsUndoneLatestFirst(string fixId)
    {
        var tweakClass = DpcFixEngine.RetiredTweakByFixId[fixId];

        // Fix applied after the tweak: the fix holds the tweak's value, so it goes first.
        var fixLater = PairSystem(StackedLedger(fixId, TweakTime, TweakTime.AddMinutes(1)));
        var results = Inventory(fixLater).Undo(Inventory(fixLater).List().Items);
        Assert.Equal(["dpc:" + fixId, "tweak:" + tweakClass], fixLater.Calls);
        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.Undone, r.Outcome));
        Assert.Empty(fixLater.Settings.AppliedDpcFixes);
        Assert.Empty(fixLater.Settings.SystemTweaks!.AppliedTweaks);

        // Fix applied 30 minutes before the tweak. In local time east of UTC it reads later, so
        // there this also fails if the two times are compared without normalizing.
        var tweakLater = PairSystem(StackedLedger(fixId, TweakTime, TweakTime.AddMinutes(-30)));
        Inventory(tweakLater).Undo(Inventory(tweakLater).List().Items);
        Assert.Equal(["tweak:" + tweakClass, "dpc:" + fixId], tweakLater.Calls);
    }

    private const string UsbPowerTarget = "2a737441-1930-4402-8d77-b2bebba308a3|48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    [Fact]
    public void PowerSettingFixWithoutRecordedValue_SaysTheWindowsDefaultIsRestored()
    {
        var system = new FakeSystem();
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "legacy", Description = "Legacy", ActionType = "PowerPlanSetting", Target = UsbPowerTarget });
        system.Settings.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "recorded", Description = "Recorded", ActionType = "PowerPlanSetting", Target = UsbPowerTarget, PreviousValue = "1" });

        var items = Inventory(system).List().Items;

        var legacy = items.Single(i => i.Key == "legacy").UndoDescription;
        Assert.Contains("did not record", legacy);
        Assert.Contains("Windows default", legacy);
        Assert.Equal("Restores the value DPC Doctor recorded before applying the fix.",
            items.Single(i => i.Key == "recorded").UndoDescription);
    }

    [Theory]
    [InlineData("""{"OriginalAc":-1,"OriginalDc":-1}""", true)]
    [InlineData("""{"OriginalAc":1,"OriginalDc":-1}""", true)]
    [InlineData("""{"OriginalAc":1,"OriginalDc":1}""", false)]
    public void RetiredUsbTweakWithoutRecordedOriginal_SaysTheWindowsDefaultIsRestored(string originals, bool unrecorded)
    {
        var system = new FakeSystem { FindTweakOverride = PairTweak };
        system.Settings.SystemTweaks = new SystemTweaksSettings();
        system.Settings.SystemTweaks.AppliedTweaks[nameof(DisableUsbSelectiveSuspend)] =
            new TweakState { IsAppliedByGameShift = true, OriginalValues = originals };

        var item = Assert.Single(Inventory(system).List().Items);

        if (unrecorded)
        {
            Assert.Contains("did not record", item.UndoDescription);
            Assert.Contains("Windows default", item.UndoDescription);
        }
        else
        {
            Assert.Equal("Restores the values it had before GameShift changed it.", item.UndoDescription);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-30, false)]
    public void StackedUsbPair_WithLegacyEntriesWithoutValues_IsStillUndoneLatestFirst(int fixMinutesAfterTweak, bool fixFirst)
    {
        const string fixId = "disable_usb_selective_suspend";
        var system = PairSystem(StackedLedger(fixId, TweakTime, TweakTime.AddMinutes(fixMinutesAfterTweak), s =>
        {
            var fix = s.AppliedDpcFixes.Single();
            fix.ActionType = "PowerPlanSetting";
            fix.Target = UsbPowerTarget;
            fix.PreviousValue = null;
            s.SystemTweaks!.AppliedTweaks[nameof(DisableUsbSelectiveSuspend)].OriginalValues = """{"OriginalAc":-1,"OriginalDc":-1}""";
        }));
        var inventory = Inventory(system);
        var items = inventory.List().Items;
        Assert.All(items, i => Assert.Contains("Windows default", i.UndoDescription));

        var results = inventory.Undo(items.Reverse());

        string[] order = ["dpc:" + fixId, "tweak:" + nameof(DisableUsbSelectiveSuspend)];
        Assert.Equal(fixFirst ? order : order.Reverse(), system.Calls);
        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.Undone, r.Outcome));
    }

    [Fact]
    public void LedgerTimes_KeepTheirKindThroughTheSettingsRoundTrip()
    {
        var utc = DateTime.UtcNow;
        var local = DateTime.Now;
        var json = JsonSerializer.Serialize(new AppSettings
        {
            SystemTweaks = new SystemTweaksSettings { AppliedTweaks = { ["T"] = new TweakState { AppliedAt = utc } } },
            AppliedDpcFixes = { new AppliedDpcFix { FixId = "f", AppliedAt = local } },
        });

        var read = JsonSerializer.Deserialize<AppSettings>(json)!;
        var tweakAt = read.SystemTweaks!.AppliedTweaks["T"].AppliedAt;
        var fixAt = read.AppliedDpcFixes[0].AppliedAt;

        Assert.Equal(DateTimeKind.Utc, tweakAt.Kind);
        Assert.Equal(DateTimeKind.Local, fixAt.Kind);
        Assert.Equal(utc, LastingChangesInventory.AppliedAtUtc(tweakAt));
        Assert.Equal(local.ToUniversalTime(), LastingChangesInventory.AppliedAtUtc(fixAt));

        var missing = JsonSerializer.Deserialize<AppliedDpcFix>("""{"FixId":"f"}""")!;
        Assert.Null(LastingChangesInventory.AppliedAtUtc(missing.AppliedAt));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StackedPair_LaterMemberFails_EarlierIsLeftAloneAndStaysRecorded(bool fixLater)
    {
        const string fixId = "disable_mpo";
        var system = PairSystem(
            StackedLedger(fixId, TweakTime, TweakTime.AddMinutes(fixLater ? 1 : -1)),
            fixLater ? fixId : nameof(DisableMpo));
        var inventory = Inventory(system);

        var results = inventory.Undo(inventory.List().Items);

        Assert.Equal([fixLater ? "dpc:" + fixId : "tweak:" + nameof(DisableMpo)], system.Calls);
        var later = Assert.Single(results, r => r.Outcome == LastingChangeOutcome.Failed);
        var earlier = Assert.Single(results, r => r.Outcome == LastingChangeOutcome.LeftAlone);
        Assert.Contains(later.Change.DisplayName, earlier.Message);
        Assert.Single(system.Settings.AppliedDpcFixes);
        Assert.True(system.Settings.SystemTweaks!.AppliedTweaks.ContainsKey(nameof(DisableMpo)));
        Assert.Equal(2, inventory.List().Recorded.Count);
    }

    [Fact]
    public void StackedPair_OnlyTheEarlierMemberSelected_IsLeftAlone()
    {
        const string fixId = "msi_mode_nvidia";
        var system = PairSystem(StackedLedger(fixId, TweakTime, TweakTime.AddMinutes(1)));
        var inventory = Inventory(system);
        var items = inventory.List().Items;
        var tweak = items.Single(i => i.Kind == LastingChangeKind.SystemTweak);
        var fix = items.Single(i => i.Kind == LastingChangeKind.LatencyDoctorFix);

        var result = Assert.Single(inventory.Undo([tweak]));

        Assert.Equal(LastingChangeOutcome.LeftAlone, result.Outcome);
        Assert.Contains(fix.DisplayName, result.Message);
        Assert.Contains("still in place", result.Message);
        Assert.Empty(system.Calls);

        // The later member alone is fine; once it is undone, the earlier one is no longer held back.
        Assert.Equal(LastingChangeOutcome.Undone, Assert.Single(inventory.Undo([fix])).Outcome);
        Assert.Equal(LastingChangeOutcome.Undone, Assert.Single(inventory.Undo([tweak])).Outcome);
        Assert.Equal(["dpc:" + fixId, "tweak:" + nameof(OptimizeInterruptHandling)], system.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StackedPair_WithoutDistinctTimes_UndoesTheFixFirst(bool tweakHasTime, bool fixHasTime)
    {
        const string fixId = "disable_usb_selective_suspend";
        // Both present means equal.
        var system = PairSystem(StackedLedger(fixId,
            tweakHasTime ? TweakTime : null,
            fixHasTime ? TweakTime : null));
        var inventory = Inventory(system);

        inventory.Undo(inventory.List().Items.Reverse());

        Assert.Equal(["dpc:" + fixId, "tweak:" + nameof(DisableUsbSelectiveSuspend)], system.Calls);
    }

    [Fact]
    public void StackedPair_DoesNotReorderTheOtherItems()
    {
        var settings = StackedLedger("disable_mpo", TweakTime.AddMinutes(-5), TweakTime.AddMinutes(-10), s =>
        {
            s.VbsHvciDisabledByGameShift = true;
            s.SystemTweaks!.AppliedTweaks["FakeTweak"] = new TweakState { IsAppliedByGameShift = true, AppliedAt = TweakTime };
            s.AppliedDpcFixes.Add(new AppliedDpcFix { FixId = "ndis_interrupt_moderation", ActionType = "SetNetAdapterProperty", AppliedAt = TweakTime.AddMinutes(-20) });
            s.AppliedDpcFixes.Add(BcdEntry("tscsyncpolicy"));
        });
        var system = PairSystem(settings);
        system.Bcd["tscsyncpolicy"] = "Enhanced";
        var inventory = Inventory(system);

        var results = inventory.Undo(inventory.List().Items);

        Assert.Equal(
            ["vbs", "tweak:DisableMpo", "dpc:disable_mpo", "tweak:FakeTweak", "dpc:ndis_interrupt_moderation",
                "kernel:tscsyncpolicy", "forget:tscsyncpolicy"],
            system.Calls);
        Assert.All(results, r => Assert.Equal(LastingChangeOutcome.Undone, r.Outcome));
    }

    [Fact]
    public void StackedPairMembers_AreNotChanged_WhenTheLedgerCannotBeReadBeforeUndoing()
    {
        var system = PairSystem(StackedLedger("disable_mpo", TweakTime, TweakTime.AddMinutes(1), s =>
            s.SystemTweaks!.AppliedTweaks["FakeTweak"] = new TweakState { IsAppliedByGameShift = true }));
        var inventory = Inventory(system);
        var items = inventory.List().Items;
        system.LoadFails = true;

        var results = inventory.Undo(items);

        Assert.Equal(["tweak:FakeTweak"], system.Calls);
        Assert.Equal(2, results.Count(r => r.Outcome == LastingChangeOutcome.Failed));
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
        public AppSettings Settings { get; init; } = new();
        public Dictionary<string, string?> Bcd { get; } = KernelTuningManager.AllSettings
            .ToDictionary(s => s.BcdKey, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        public bool BcdReadable { get; init; } = true;
        public bool CoreReservationPresent { get; set; }
        public List<RenamedStartupEntry> StartupEntries { get; } = new();
        public bool AllRevertsFail { get; init; }
        public bool ThrowOnDpcRevert { get; init; }
        public Func<string, ISystemTweak?>? FindTweakOverride { get; init; }
        public HashSet<string> FailingKeys { get; init; } = new();
        public bool LoadFails { get; set; }
        public List<string> Calls { get; } = new();

        public AppSettings LoadSettings() =>
            LoadFails ? throw new IOException("settings locked") : Settings;

        public ISystemTweak? FindTweak(string className) =>
            FindTweakOverride != null ? FindTweakOverride(className)
            : className == "FakeTweak" ? new FakeTweak() : null;

        public bool RevertTweak(ISystemTweak tweak)
        {
            var className = tweak.GetType().Name;
            Calls.Add("tweak:" + className);
            if (AllRevertsFail || FailingKeys.Contains(className)) return false;
            Settings.SystemTweaks?.AppliedTweaks.Remove(className);
            return true;
        }

        public DpcFixResult RevertDpcFix(string fixId)
        {
            Calls.Add("dpc:" + fixId);
            if (ThrowOnDpcRevert) throw new InvalidOperationException("boom");
            if (AllRevertsFail || FailingKeys.Contains(fixId)) return new DpcFixResult { Success = false, Message = "failed" };
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
            VbsHvciToggle.ForgetToggleRecord(Settings);
            VbsHvciToggle.ForgetTweakRecord(Settings);
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
