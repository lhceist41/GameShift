using System.Text.RegularExpressions;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Applies session-scoped system tweaks during gaming and reverts them on exit.
///
/// 9A — Multimedia SystemProfile: MMCSS scheduler priority, GPU priority,
///      timer resolution hint, network throttling, and system responsiveness.
/// 9E — USB Selective Suspend: disables selective suspend and enhanced power
///      management on HID (gaming peripheral) device class entries.
/// 9F — PCIe ASPM: disables Active State Power Management link-state power
///      savings for GPU/NVMe during gaming via powercfg.
///
/// All values are read before writing and stored for clean revert.
/// </summary>
public class SessionSystemTweaksOptimizer : IOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private readonly List<RegistryBackup> _backups = new();
    private int? _originalAspmValue;

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

    // PCIe ASPM powercfg GUIDs
    private const string PcieSubgroupGuid = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string AspmSettingGuid  = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    // ── IOptimization ─────────────────────────────────────────────────────────

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
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
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SessionSystemTweaks] Apply failed");
            return Task.FromResult(false);
        }
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!IsApplied)
            return Task.FromResult(true);

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
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SessionSystemTweaks] Revert failed");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }

    // ── 9A: Multimedia SystemProfile (MMCSS) ──────────────────────────────────

    private void Apply9A_MmcssProfile()
    {
        // Parent profile: network throttling + system responsiveness
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
                // Only check numbered subkeys (0000, 0001, ...)
                if (!int.TryParse(subKeyName, out _)) continue;

                string fullPath = $@"{HidClassPath}\{subKeyName}";

                // Check that this is actually a USB HID device (has DriverDesc)
                using var devKey = Registry.LocalMachine.OpenSubKey(fullPath);
                if (devKey == null) continue;

                string desc = devKey.GetValue("DriverDesc")?.ToString() ?? "";
                if (string.IsNullOrEmpty(desc)) continue;

                // Disable selective suspend
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

            // Set to 0 (Off)
            RunPowercfg($"/setacvalueindex SCHEME_CURRENT {PcieSubgroupGuid} {AspmSettingGuid} 0");
            RunPowercfg("/setactive SCHEME_CURRENT");

            _logger.Information(
                "[SessionSystemTweaks] 9F: PCIe ASPM disabled (was: {Original})",
                _originalAspmValue?.ToString() ?? "unknown");
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
        using var process = global::System.Diagnostics.Process.Start(
            new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

        if (process == null) return "";
        process.WaitForExit(10_000);
        return process.StandardOutput.ReadToEnd();
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
