using System.Text;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.SystemTweaks;

/// <summary>
/// Kinds of lasting change, in the order they are undone.
/// </summary>
public enum LastingChangeKind
{
    // Covers both records of a Memory Integrity change, the toggle's and the retired tweak's,
    // which VbsHvciToggle.ReEnableVbsHvci undoes in the right order.
    MemoryIntegrity,
    SystemTweak,
    LatencyDoctorFix,
    KernelSetting,
    CpuCoreReservation,
    RenamedStartupEntry,
}

/// <summary>One lasting change GameShift can identify, and what undoing it does.</summary>
public sealed class LastingChange
{
    public required LastingChangeKind Kind { get; init; }

    /// <summary>Tweak class name, DPC Doctor fix id, kernel setting id, or startup value name.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }
    public required string UndoDescription { get; init; }
    public bool RequiresRestart { get; init; }

    /// <summary>
    /// GameShift did not record making this change, so it is undone only when the user confirms
    /// it on its own, never as part of "undo everything GameShift recorded".
    /// </summary>
    public bool NeedsOwnConfirmation { get; init; }

    internal RenamedStartupEntry? StartupEntry { get; init; }
}

public enum LastingChangeOutcome
{
    Undone,
    Failed,
    LeftAlone,
}

public sealed record LastingChangeResult(LastingChange Change, LastingChangeOutcome Outcome, string Message);

/// <summary>The lasting changes found, plus the sources that could not be read.</summary>
public sealed class LastingChangesList
{
    public required IReadOnlyList<LastingChange> Items { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public IReadOnlyList<LastingChange> Recorded => Items.Where(i => !i.NeedsOwnConfirmation).ToList();
    public IReadOnlyList<LastingChange> NeedingOwnConfirmation => Items.Where(i => i.NeedsOwnConfirmation).ToList();
}

/// <summary>
/// The system calls behind each ledger, so the inventory can be tested without touching the
/// registry, BCD or the settings file.
/// </summary>
public interface ILastingChangeSystem
{
    AppSettings LoadSettings();

    ISystemTweak? FindTweak(string className);

    /// <summary>Reverts the tweak and drops its ledger entry only on success.</summary>
    bool RevertTweak(ISystemTweak tweak);

    /// <summary>Reverts the fix and drops its ledger entry only on success.</summary>
    DpcFixResult RevertDpcFix(string fixId);

    /// <summary>False when the boot configuration could not be read.</summary>
    bool TryReadBcdValues(out IReadOnlyDictionary<string, string?> values);

    (bool Success, string Message) RevertKernelSetting(KernelTuningSetting setting);

    void ForgetKernelSetting(KernelTuningSetting setting);

    bool IsCoreReservationPresent();

    /// <summary>Null on success, otherwise the reason.</summary>
    string? RemoveCoreReservation();

    /// <summary>Undoes the Memory Integrity change from both records and clears each only on success.</summary>
    bool ReEnableMemoryIntegrity();

    IReadOnlyList<RenamedStartupEntry> ReadRenamedStartupEntries();

    /// <summary>Null on success, otherwise the reason.</summary>
    string? RestoreStartupEntry(RenamedStartupEntry entry);
}

/// <summary>
/// Lists every lasting change GameShift can identify across its ledgers and undoes the ones the
/// user confirms, reporting each result. An item is reported undone only when its reverter
/// succeeded; a failed item keeps its ledger entry so it is listed again next time.
/// </summary>
public sealed class LastingChangesInventory
{
    private const string NotSetValue = "<not set>";

    private readonly ILastingChangeSystem _system;
    private readonly ILogger _logger;

    public LastingChangesInventory(ILastingChangeSystem system, ILogger logger)
    {
        _system = system;
        _logger = logger;
    }

    public static LastingChangesInventory CreateForThisMachine(
        SystemTweaksManager tweaks, DpcFixEngine dpcFixes, VbsHvciToggle vbs) =>
        new(new WindowsLastingChangeSystem(tweaks, dpcFixes, vbs), SettingsManager.Logger);

    public LastingChangesList List()
    {
        var items = new List<LastingChange>();
        var warnings = new List<string>();

        AppSettings? settings = null;
        try
        {
            settings = _system.LoadSettings();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[LastingChanges] Failed to load settings");
            warnings.Add("GameShift's settings could not be read, so the changes it recorded are not listed.");
        }

        if (settings != null)
        {
            TryAdd(items, warnings, "Memory Integrity", () => ListMemoryIntegrity(settings));
            TryAdd(items, warnings, "System tweaks", () => ListTweaks(settings));
            TryAdd(items, warnings, "DPC Doctor fixes", () => ListDpcFixes(settings));
            TryAdd(items, warnings, "Kernel (boot) settings", () => ListKernelSettings(settings, warnings));
        }
        TryAdd(items, warnings, "CPU core reservation", ListCoreReservation);
        TryAdd(items, warnings, "Startup apps", ListRenamedStartupEntries);

        return new LastingChangesList { Items = items, Warnings = warnings };
    }

    /// <summary>
    /// Undoes <paramref name="items"/>. An item that <see cref="LastingChange.NeedsOwnConfirmation"/>
    /// is undone only when it is also in <paramref name="separatelyConfirmed"/>.
    /// </summary>
    public IReadOnlyList<LastingChangeResult> Undo(
        IEnumerable<LastingChange> items, IEnumerable<LastingChange>? separatelyConfirmed = null)
    {
        var confirmed = new HashSet<LastingChange>(separatelyConfirmed ?? []);
        var results = new List<LastingChangeResult>();
        IReadOnlyDictionary<string, string?>? bcdNow = null;
        bool bcdRead = false;

        foreach (var item in items.Distinct().OrderBy(i => i.Kind))
        {
            if (item.NeedsOwnConfirmation && !confirmed.Contains(item))
            {
                results.Add(new(item, LastingChangeOutcome.LeftAlone, "Not confirmed, so it was left as it is."));
                continue;
            }

            if (item.Kind == LastingChangeKind.KernelSetting && !bcdRead)
            {
                bcdRead = true;
                bcdNow = _system.TryReadBcdValues(out var values) ? values : null;
            }

            LastingChangeResult result;
            try
            {
                result = UndoOne(item, bcdNow);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LastingChanges] Undo of {Kind} {Key} failed", item.Kind, item.Key);
                result = new(item, LastingChangeOutcome.Failed, ex.Message);
            }

            _logger.Information("[LastingChanges] {Kind} {Key}: {Outcome} ({Message})",
                item.Kind, item.Key, result.Outcome, result.Message);
            results.Add(result);
        }

        return results;
    }

    // ── Listing ─────────────────────────────────────────────────────────

    private IEnumerable<LastingChange> ListMemoryIntegrity(AppSettings settings)
    {
        if (!VbsHvciToggle.IsDisabledByGameShift(settings)) yield break;

        yield return new LastingChange
        {
            Kind = LastingChangeKind.MemoryIntegrity,
            Key = "VbsHvci",
            DisplayName = "Memory Integrity (VBS/HVCI) turned off",
            UndoDescription = "Turns Memory Integrity and virtualization-based security back on.",
            RequiresRestart = true,
        };
    }

    private IEnumerable<LastingChange> ListTweaks(AppSettings settings)
    {
        var applied = settings.SystemTweaks?.AppliedTweaks;
        if (applied == null) yield break;

        foreach (var (className, state) in applied)
        {
            if (!state.IsAppliedByGameShift) continue;
            if (className == nameof(Tweaks.DisableMemoryIntegrity)) continue; // listed as Memory Integrity

            var tweak = _system.FindTweak(className);
            yield return new LastingChange
            {
                Kind = LastingChangeKind.SystemTweak,
                Key = className,
                DisplayName = tweak?.Name ?? className,
                UndoDescription = tweak != null
                    ? "Restores the values it had before GameShift changed it."
                    : "This version of GameShift cannot undo it.",
                RequiresRestart = tweak?.RequiresReboot ?? false,
            };
        }
    }

    private static IEnumerable<LastingChange> ListDpcFixes(AppSettings settings) =>
        settings.AppliedDpcFixes
            .Where(f => !IsKernelSettingEntry(f.FixId))
            .GroupBy(f => f.FixId)
            .Select(g => g.First())
            .Select(f => new LastingChange
            {
                Kind = LastingChangeKind.LatencyDoctorFix,
                Key = f.FixId,
                DisplayName = string.IsNullOrWhiteSpace(f.Description) ? f.FixId : f.Description,
                UndoDescription = "Restores the value DPC Doctor recorded before applying the fix.",
                RequiresRestart = f.RequiresReboot,
            });

    private IEnumerable<LastingChange> ListKernelSettings(AppSettings settings, List<string> warnings)
    {
        var recordedIds = settings.AppliedDpcFixes
            .Where(f => IsKernelSettingEntry(f.FixId))
            .Select(f => f.FixId)
            .ToHashSet(StringComparer.Ordinal);
        if (recordedIds.Count == 0) return [];

        if (!_system.TryReadBcdValues(out var current))
        {
            warnings.Add("The kernel (boot) settings could not be read, so none of them are listed.");
            return [];
        }

        return KernelTuningManager.AllSettings
            .Where(s => recordedIds.Contains(KernelTuningManager.LedgerFixId(s)) && IsAtGameShiftValue(s, current))
            .Select(s => new LastingChange
            {
                Kind = LastingChangeKind.KernelSetting,
                Key = s.Id,
                DisplayName = $"{s.DisplayName} (boot setting)",
                UndoDescription = DescribeKernelRevert(s, done: false),
                RequiresRestart = true,
            })
            .ToList();
    }

    private IEnumerable<LastingChange> ListCoreReservation()
    {
        if (!_system.IsCoreReservationPresent()) yield break;

        yield return new LastingChange
        {
            Kind = LastingChangeKind.CpuCoreReservation,
            Key = "ReservedCpuSets",
            DisplayName = "CPU core reservation (set by an earlier version of GameShift's DPC Doctor or another tool)",
            UndoDescription = "Removes the reservation so Windows schedules work on every core again.",
            RequiresRestart = true,
            NeedsOwnConfirmation = true,
        };
    }

    private IEnumerable<LastingChange> ListRenamedStartupEntries() =>
        _system.ReadRenamedStartupEntries().Select(e => new LastingChange
        {
            Kind = LastingChangeKind.RenamedStartupEntry,
            Key = e.ValueName,
            DisplayName = $"Startup app \"{e.OriginalName}\" ({(e.CurrentUser ? "this user" : "all users")})",
            UndoDescription = "Renames it back so it starts at sign-in again.",
            NeedsOwnConfirmation = true,
            StartupEntry = e,
        });

    private void TryAdd(List<LastingChange> items, List<string> warnings, string source,
        Func<IEnumerable<LastingChange>> list)
    {
        try
        {
            items.AddRange(list().ToList());
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[LastingChanges] Failed to list {Source}", source);
            warnings.Add($"{source} could not be read, so they are not listed.");
        }
    }

    // ── Undo ────────────────────────────────────────────────────────────

    private LastingChangeResult UndoOne(LastingChange item, IReadOnlyDictionary<string, string?>? bcdNow)
    {
        switch (item.Kind)
        {
            case LastingChangeKind.MemoryIntegrity:
                return _system.ReEnableMemoryIntegrity()
                    ? Done(item, "Memory Integrity turned back on.")
                    : Failed(item, "Memory Integrity could not be fully turned back on.");

            case LastingChangeKind.SystemTweak:
                var tweak = _system.FindTweak(item.Key);
                if (tweak == null) return Failed(item, "This version of GameShift cannot undo this tweak.");
                return _system.RevertTweak(tweak)
                    ? Done(item, "Restored.")
                    : Failed(item, "The original values could not be restored.");

            case LastingChangeKind.LatencyDoctorFix:
                var fix = _system.RevertDpcFix(item.Key);
                return fix.Success ? Done(item, fix.Message) : Failed(item, fix.Message);

            case LastingChangeKind.KernelSetting:
                return UndoKernelSetting(item, bcdNow);

            case LastingChangeKind.CpuCoreReservation:
                var reservationError = _system.RemoveCoreReservation();
                return reservationError == null ? Done(item, "Reservation removed.") : Failed(item, reservationError);

            case LastingChangeKind.RenamedStartupEntry:
                if (item.StartupEntry == null) return Failed(item, "The startup entry is unknown.");
                var startupError = _system.RestoreStartupEntry(item.StartupEntry);
                return startupError == null ? Done(item, "Turned back on.") : Failed(item, startupError);

            default:
                return Failed(item, "Unknown kind of change.");
        }
    }

    private LastingChangeResult UndoKernelSetting(LastingChange item, IReadOnlyDictionary<string, string?>? bcdNow)
    {
        var setting = KernelTuningManager.AllSettings.FirstOrDefault(s => s.Id == item.Key);
        if (setting == null) return Failed(item, "Unknown kernel setting.");

        // Re-checked at undo time: a value changed since the list was shown is no longer
        // provably GameShift's and is not touched.
        if (bcdNow == null)
            return Failed(item, "The boot configuration could not be read, so it was not changed.");
        if (!IsAtGameShiftValue(setting, bcdNow))
            return new(item, LastingChangeOutcome.LeftAlone, "It no longer has the value GameShift set, so it was left as it is.");

        var (success, message) = _system.RevertKernelSetting(setting);
        if (!success) return Failed(item, message);

        try
        {
            _system.ForgetKernelSetting(setting);
        }
        catch (Exception ex)
        {
            // The boot value is already reverted; a stale ledger entry is only listed again while
            // the value still matches, which it no longer does.
            _logger.Warning(ex, "[LastingChanges] Reverted {Setting} but could not clear its ledger entry", setting.Id);
        }
        return Done(item, DescribeKernelRevert(setting, done: true));
    }

    private static LastingChangeResult Done(LastingChange item, string message) =>
        new(item, LastingChangeOutcome.Undone, message);

    private static LastingChangeResult Failed(LastingChange item, string message) =>
        new(item, LastingChangeOutcome.Failed, message);

    // ── Rules ───────────────────────────────────────────────────────────

    /// <summary>Kernel tuning writes its ledger entries as <c>bcd_&lt;id&gt;</c>.</summary>
    internal static bool IsKernelSettingEntry(string fixId) =>
        fixId.StartsWith("bcd_", StringComparison.Ordinal);

    /// <summary>
    /// True when the boot value equals the one GameShift sets. A key missing from
    /// <paramref name="current"/> counts as unknown, not as "not set".
    /// </summary>
    internal static bool IsAtGameShiftValue(KernelTuningSetting setting, IReadOnlyDictionary<string, string?> current)
    {
        if (!current.TryGetValue(setting.BcdKey, out var value)) return false;
        if (setting.RecommendedValue == NotSetValue) return value == null;
        return value != null && string.Equals(value.Trim(), setting.RecommendedValue, StringComparison.OrdinalIgnoreCase);
    }

    // The ledger holds no earlier value, so a "/set" revert writes a fixed value the setting may
    // never have had; the text says what is written instead of claiming to restore it.
    internal static string DescribeKernelRevert(KernelTuningSetting setting, bool done)
    {
        var parts = setting.RevertArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[0] == "/set")
            return $"{(done ? "Set" : "Sets")} the boot value {parts[1]} to {parts[2]}. GameShift did not record the value it had before.";
        return $"{(done ? "Removed" : "Removes")} the boot value so Windows uses its default again.";
    }

    // ── Text for the confirm and result dialogs ─────────────────────────

    public static string DescribeItems(IEnumerable<LastingChange> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append("• ").Append(item.DisplayName).Append(": ").Append(item.UndoDescription);
            if (item.RequiresRestart) sb.Append(" Needs a restart.");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string DescribeResults(IReadOnlyList<LastingChangeResult> results)
    {
        var sb = new StringBuilder();
        AppendGroup(sb, results, LastingChangeOutcome.Undone, "Undone:");
        AppendGroup(sb, results, LastingChangeOutcome.Failed, "Failed (still recorded, so Restore All lists it again):");
        AppendGroup(sb, results, LastingChangeOutcome.LeftAlone, "Left as it is:");

        if (results.Any(r => r.Outcome == LastingChangeOutcome.Undone && r.Change.RequiresRestart))
            sb.AppendLine("Restart your PC to finish undoing the changes that need a restart.");
        return sb.ToString().TrimEnd();
    }

    private static void AppendGroup(StringBuilder sb, IReadOnlyList<LastingChangeResult> results,
        LastingChangeOutcome outcome, string heading)
    {
        var group = results.Where(r => r.Outcome == outcome).ToList();
        if (group.Count == 0) return;

        sb.AppendLine(heading);
        foreach (var r in group)
        {
            sb.Append("• ").Append(r.Change.DisplayName);
            // Kernel results say what was written, since that may not be the earlier value.
            bool showMessage = outcome != LastingChangeOutcome.Undone || r.Change.Kind == LastingChangeKind.KernelSetting;
            if (showMessage && !string.IsNullOrWhiteSpace(r.Message))
                sb.Append(": ").Append(r.Message);
            sb.AppendLine();
        }
        sb.AppendLine();
    }
}

/// <summary>The real reverters behind <see cref="LastingChangesInventory"/>.</summary>
internal sealed class WindowsLastingChangeSystem : ILastingChangeSystem
{
    private readonly SystemTweaksManager _tweaks;
    private readonly DpcFixEngine _dpcFixes;
    private readonly VbsHvciToggle _vbs;
    private readonly KernelTuningManager _kernel = new();

    public WindowsLastingChangeSystem(SystemTweaksManager tweaks, DpcFixEngine dpcFixes, VbsHvciToggle vbs)
    {
        _tweaks = tweaks;
        _dpcFixes = dpcFixes;
        _vbs = vbs;
    }

    public AppSettings LoadSettings() => SettingsManager.Load();

    public ISystemTweak? FindTweak(string className) => _tweaks.GetTweakByClassName(className);

    public bool RevertTweak(ISystemTweak tweak) => _tweaks.RevertTweak(tweak);

    public DpcFixResult RevertDpcFix(string fixId) => _dpcFixes.RevertFix(fixId);

    public bool TryReadBcdValues(out IReadOnlyDictionary<string, string?> values)
    {
        var ok = _kernel.TryReadCurrentValues(out var read);
        values = read;
        return ok;
    }

    public (bool Success, string Message) RevertKernelSetting(KernelTuningSetting setting) =>
        _kernel.Revert(setting);

    public void ForgetKernelSetting(KernelTuningSetting setting) =>
        SettingsManager.Update(s => KernelTuningManager.RemoveLedgerEntries(s, setting));

    public bool IsCoreReservationPresent() => CoreIsolationManager.IsReservationPresent();

    public string? RemoveCoreReservation() => new CoreIsolationManager().RemoveReservation();

    public bool ReEnableMemoryIntegrity() => _vbs.ReEnableVbsHvci();

    public IReadOnlyList<RenamedStartupEntry> ReadRenamedStartupEntries() => StartupAppManager.GetRenamedEntries();

    public string? RestoreStartupEntry(RenamedStartupEntry entry) => StartupAppManager.RestoreRenamedEntry(entry);
}
