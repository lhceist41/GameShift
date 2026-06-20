using System.Text.Json;
using System.Text.RegularExpressions;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Applies session-scoped system tweaks during gaming and reverts them on exit.
///
/// 9A - Multimedia SystemProfile: MMCSS scheduler priority, GPU priority,
///      timer resolution hint, network throttling, and system responsiveness.
/// 9E - USB Selective Suspend: disables selective suspend and enhanced power
///      management on HID (gaming peripheral) device class entries.
/// 9F - PCIe ASPM: disables Active State Power Management link-state power
///      savings for GPU/NVMe during gaming via powercfg.
///
/// All values are read before writing and stored for clean revert.
/// </summary>
public class SessionSystemTweaksOptimizer : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private readonly List<RegistryBackup> _backups = new();
    private int? _originalAspmValue;
    private SystemContext? _context;

    public const string OptimizationId = "Session System Tweaks";
    public string Name => OptimizationId;
    public string Description => "MMCSS scheduler, USB suspend, and PCIe ASPM tweaks during gaming";
    public bool IsApplied { get; private set; }
    public bool IsAvailable => true;

    // ── Registry paths ────────────────────────────────────────────────────────

    private const string MmcssProfilePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private const string MmcssGamesPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    private const string HidClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}";

    // PCIe ASPM powercfg GUIDs. This always-on optimizer is the SOLE session owner of the PCIe
    // ASPM powercfg setting: PowerPlanSwitcher (toggleable via SwitchPowerPlan, runs first)
    // deliberately omits ASPM from its SessionOverrides so only one module captures/restores it,
    // avoiding the dual-writer revert asymmetry fixed for MMCSS in 2e17176. Do not also write this
    // setting from PowerPlanSwitcher.
    private const string PcieSubgroupGuid = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string AspmSettingGuid = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    // ── IOptimization ─────────────────────────────────────────────────────────

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        CanApply(new SystemContext { Profile = profile, Snapshot = snapshot });
        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    public OptimizationResult Apply()
    {
        // Already applied: do NOT re-capture state. Re-reading the registry now would record the
        // already-modified gaming values as the "original" and corrupt revert. The engine guards
        // against this too, but keep the module self-protecting.
        if (IsApplied)
        {
            _logger.Warning("[SessionSystemTweaks] Apply called while already applied; preserving original baseline");
            return new OptimizationResult(OptimizationId, BuildOriginalJson(), string.Empty, OptimizationState.Applied);
        }

        try
        {
            _backups.Clear();
            _originalAspmValue = null;

            Apply9A_MmcssProfile();
            Apply9E_UsbSuspend();
            Apply9F_PcieAspm();

            IsApplied = true;
            _logger.Information(
                "[SessionSystemTweaks] Applied {Count} registry values + PCIe ASPM",
                _backups.Count);
            return new OptimizationResult(OptimizationId, BuildOriginalJson(), string.Empty, OptimizationState.Applied);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SessionSystemTweaks] Apply failed");

            // If persistent changes were already committed before the exception, keep the
            // optimization tracked so the engine reverts them instead of orphaning them.
            // (The engine only reverts optimizations that report Applied.)
            if (_backups.Count > 0 || _originalAspmValue.HasValue)
            {
                IsApplied = true;
                string original;
                try { original = BuildOriginalJson(); }
                catch { original = string.Empty; }
                _logger.Warning(
                    "[SessionSystemTweaks] Apply failed mid-way but {Count} registry change(s) were committed - " +
                    "tracking for revert.", _backups.Count);
                return new OptimizationResult(OptimizationId, original, string.Empty, OptimizationState.Applied);
            }

            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Serializes the captured registry backups + original ASPM value to the journal OriginalValue
    /// string so the watchdog can restore them after a crash with no live instance state.
    /// </summary>
    private string BuildOriginalJson()
    {
        var payload = new SessionRevertPayload
        {
            Aspm = _originalAspmValue,
            Backups = _backups.Select(b => new SessionBackupEntry
            {
                Hklm = b.IsHklm,
                Path = b.SubKeyPath,
                Name = b.ValueName,
                Existed = b.PreviouslyExisted,
                Kind = b.ValueKind.ToString(),
                Value = b.PreviouslyExisted ? b.PreviousValue : null
            }).ToList()
        };
        return JsonSerializer.Serialize(payload);
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!IsApplied)
            return Task.FromResult(true);
        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    public bool Verify() => IsApplied;

    public OptimizationResult Revert()
    {
        try
        {
            // Revert registry values in reverse order
            for (int i = _backups.Count - 1; i >= 0; i--)
            {
                var b = _backups[i];
                try
                {
                    if (b.IsHklm)
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(b.SubKeyPath, writable: true);
                        if (key == null) continue;

                        if (b.PreviouslyExisted && b.PreviousValue != null)
                        {
                            key.SetValue(b.ValueName, b.PreviousValue, b.ValueKind);
                        }
                        else if (!b.PreviouslyExisted)
                        {
                            key.DeleteValue(b.ValueName, throwOnMissingValue: false);
                        }
                    }
                    else
                    {
                        // HKCU
                        using var key = Registry.CurrentUser.OpenSubKey(b.SubKeyPath, writable: true);
                        if (key == null) continue;

                        if (b.PreviouslyExisted && b.PreviousValue != null)
                            key.SetValue(b.ValueName, b.PreviousValue, b.ValueKind);
                        else if (!b.PreviouslyExisted)
                            key.DeleteValue(b.ValueName, throwOnMissingValue: false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SessionSystemTweaks] Failed to revert {Path}\\{Name}",
                        b.SubKeyPath, b.ValueName);
                }
            }

            _backups.Clear();

            // Revert ASPM
            Revert9F_PcieAspm();

            IsApplied = false;
            _logger.Information("[SessionSystemTweaks] Reverted all session tweaks");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SessionSystemTweaks] Revert failed");
            IsApplied = false;
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Watchdog recovery: restores the registry values and PCIe ASPM from the journaled
    /// OriginalValue JSON without any live instance state.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SessionRevertPayload>(originalValueJson);
            if (payload == null)
                return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "Failed to parse originalValueJson");

            // Restore registry values in reverse (LIFO) order.
            for (int i = payload.Backups.Count - 1; i >= 0; i--)
            {
                var b = payload.Backups[i];
                try
                {
                    var root = b.Hklm ? Registry.LocalMachine : Registry.CurrentUser;
                    using var key = root.OpenSubKey(b.Path, writable: true);
                    if (key == null) continue;

                    if (!b.Existed)
                    {
                        key.DeleteValue(b.Name, throwOnMissingValue: false);
                    }
                    else if (b.Value is JsonElement el)
                    {
                        if (string.Equals(b.Kind, "String", StringComparison.Ordinal))
                            key.SetValue(b.Name, el.GetString() ?? "", RegistryValueKind.String);
                        else
                            key.SetValue(b.Name, el.GetInt32(), RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SessionSystemTweaks] RevertFromRecord failed for {Path}\\{Name}", b.Path, b.Name);
                }
            }

            if (payload.Aspm.HasValue)
            {
                RunPowercfg($"/setacvalueindex SCHEME_CURRENT {PcieSubgroupGuid} {AspmSettingGuid} {payload.Aspm.Value}");
                RunPowercfg("/setactive SCHEME_CURRENT");
            }

            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SessionSystemTweaks] RevertFromRecord failed");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    // ── Journal payload DTOs ────────────────────────────────────────────────────

    private sealed class SessionRevertPayload
    {
        public List<SessionBackupEntry> Backups { get; set; } = new();
        public int? Aspm { get; set; }
    }

    private sealed class SessionBackupEntry
    {
        public bool Hklm { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
        public string Kind { get; set; } = "";
        public object? Value { get; set; }
    }

    // ── 9A: Multimedia SystemProfile (MMCSS) ──────────────────────────────────

    private void Apply9A_MmcssProfile()
    {
        // Parent profile: network throttling + system responsiveness. This module is the SOLE
        // session-scoped owner of these two SystemProfile values; NetworkOptimizer deliberately
        // does NOT write them. Because this module is always-on (no per-profile toggle) it sets
        // them in every session regardless of whether NetworkOptimizer is enabled, so a single
        // clean capture/restore replaces the old order-dependent double-capture that two session
        // writers of the same key caused.
        SetHklmDword(MmcssProfilePath, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF));
        SetHklmDword(MmcssProfilePath, "SystemResponsiveness", 10);

        // Games task: scheduler priority, GPU priority, timer, I/O
        SetHklmDword(MmcssGamesPath, "Affinity", 0);
        SetHklmString(MmcssGamesPath, "Background Only", "False");
        SetHklmDword(MmcssGamesPath, "Clock Rate", 10000);
        SetHklmDword(MmcssGamesPath, "GPU Priority", 8);
        SetHklmDword(MmcssGamesPath, "Priority", 6);
        SetHklmString(MmcssGamesPath, "Scheduling Category", "High");
        SetHklmString(MmcssGamesPath, "SFIO Priority", "High");

        _logger.Information("[SessionSystemTweaks] 9A: MMCSS profile configured for gaming");
    }

    // ── 9E: USB Selective Suspend ─────────────────────────────────────────────

    private void Apply9E_UsbSuspend()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(HidClassPath);
            if (classKey == null)
            {
                _logger.Debug("[SessionSystemTweaks] 9E: HID class key not found");
                return;
            }

            int count = 0;
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;

                string fullPath = $@"{HidClassPath}\{subKeyName}";

                using var devKey = Registry.LocalMachine.OpenSubKey(fullPath);
                if (devKey == null) continue;

                // Only target USB input devices - check if the device has
                // SelectiveSuspendEnabled already present (means it's a USB device
                // that supports suspend) AND has a relevant HID description
                var existingSuspend = devKey.GetValue("SelectiveSuspendEnabled");
                if (existingSuspend == null) continue; // Not a USB HID device

                SetHklmDword(fullPath, "SelectiveSuspendEnabled", 0);
                SetHklmDword(fullPath, "EnhancedPowerMgmtEnabled", 0);
                count++;
            }

            _logger.Information("[SessionSystemTweaks] 9E: USB selective suspend disabled for {Count} HID devices", count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SessionSystemTweaks] 9E: USB suspend disable failed");
        }
    }

    // ── 9F: PCIe ASPM Disable ─────────────────────────────────────────────────

    private void Apply9F_PcieAspm()
    {
        try
        {
            // Read current ASPM value
            _originalAspmValue = ReadPowercfgValue(PcieSubgroupGuid, AspmSettingGuid);

            // Never change a value we couldn't capture - without a baseline there is nothing to
            // revert to, so leave ASPM untouched if the pre-apply read failed.
            if (_originalAspmValue == null)
            {
                _logger.Warning("[SessionSystemTweaks] 9F: skipped - could not read current PCIe ASPM value");
                return;
            }

            // Set to 0 (Off)
            RunPowercfg($"/setacvalueindex SCHEME_CURRENT {PcieSubgroupGuid} {AspmSettingGuid} 0");
            RunPowercfg("/setactive SCHEME_CURRENT");

            _logger.Information(
                "[SessionSystemTweaks] 9F: PCIe ASPM disabled (was: {Original})",
                _originalAspmValue.Value);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SessionSystemTweaks] 9F: PCIe ASPM disable failed");
        }
    }

    private void Revert9F_PcieAspm()
    {
        if (_originalAspmValue == null) return;

        try
        {
            RunPowercfg($"/setacvalueindex SCHEME_CURRENT {PcieSubgroupGuid} {AspmSettingGuid} {_originalAspmValue.Value}");
            RunPowercfg("/setactive SCHEME_CURRENT");

            _logger.Information(
                "[SessionSystemTweaks] 9F: PCIe ASPM restored to {Value}",
                _originalAspmValue.Value);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SessionSystemTweaks] 9F: PCIe ASPM restore failed");
        }

        _originalAspmValue = null;
    }

    // ── Registry helpers ──────────────────────────────────────────────────────

    private void SetHklmDword(string subKeyPath, string valueName, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true);
            if (key == null)
            {
                _logger.Warning("[SessionSystemTweaks] Cannot open HKLM\\{Path}", subKeyPath);
                return;
            }

            var existing = key.GetValue(valueName);
            _backups.Add(new RegistryBackup
            {
                IsHklm = true,
                SubKeyPath = subKeyPath,
                ValueName = valueName,
                PreviouslyExisted = existing != null,
                PreviousValue = existing,
                ValueKind = RegistryValueKind.DWord
            });

            key.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SessionSystemTweaks] Failed to set {Path}\\{Name}", subKeyPath, valueName);
        }
    }

    private void SetHklmString(string subKeyPath, string valueName, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true);
            if (key == null) return;

            var existing = key.GetValue(valueName);
            _backups.Add(new RegistryBackup
            {
                IsHklm = true,
                SubKeyPath = subKeyPath,
                ValueName = valueName,
                PreviouslyExisted = existing != null,
                PreviousValue = existing,
                ValueKind = RegistryValueKind.String
            });

            key.SetValue(valueName, value, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SessionSystemTweaks] Failed to set {Path}\\{Name}", subKeyPath, valueName);
        }
    }

    // ── Powercfg helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current AC value index for a power setting via powercfg /query.
    /// Returns null if the query fails or the value cannot be parsed.
    /// </summary>
    private static int? ReadPowercfgValue(string subgroupGuid, string settingGuid)
    {
        try
        {
            var output = RunPowercfg($"/query SCHEME_CURRENT {subgroupGuid} {settingGuid}");
            if (string.IsNullOrEmpty(output)) return null;

            // Look for "Current AC Power Setting Index: 0x00000002" pattern
            var match = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            if (match.Success && int.TryParse(match.Groups[1].Value,
                    global::System.Globalization.NumberStyles.HexNumber, null, out int val))
            {
                return val;
            }
        }
        catch { /* Best-effort */ }
        return null;
    }

    private static string RunPowercfg(string arguments)
    {
        // Returns stdout regardless of exit code (callers regex-parse it); empty string when the
        // process can't start. The shared runner additionally reaps a wedged child instead of
        // leaving it running, with no change to the returned output.
        return ProcessRunner.Run(NativeInterop.SystemExePath("powercfg.exe"), arguments, 10_000).StdOut;
    }

    // ── Backup record ─────────────────────────────────────────────────────────

    private class RegistryBackup
    {
        public bool IsHklm { get; init; }
        public string SubKeyPath { get; init; } = "";
        public string ValueName { get; init; } = "";
        public bool PreviouslyExisted { get; init; }
        public object? PreviousValue { get; init; }
        public RegistryValueKind ValueKind { get; init; }
    }
}
