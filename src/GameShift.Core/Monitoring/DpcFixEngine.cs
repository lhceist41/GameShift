using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Result of applying or checking a DPC fix.
/// </summary>
public class DpcFixResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool RebootRequired { get; init; }

    /// <summary>
    /// The fix failed, but part of it could not be rolled back, so its ledger entry is kept
    /// for an undo.
    /// </summary>
    internal bool ChangeLeftInPlace { get; init; }
}

/// <summary>
/// Executes DPC latency fixes and persists rollback state to AppSettings.
/// Applies 4 action types: RegistrySet, NetshCommand, PowerPlanSetting, SetNetAdapterProperty.
/// Reverts those plus BcdEdit, which earlier versions could apply. All fixes require admin privileges.
/// </summary>
public class DpcFixEngine
{
    // No held AppSettings instance: every read goes through SettingsManager.Load() and every write
    // through the transactional SettingsManager.Update, so this engine and the DPC Doctor view model
    // can no longer diverge from each other or clobber other components' settings.

    /// <summary>
    /// Applies a fix from the known driver database.
    /// Reads current state, applies the fix, stores rollback info in AppSettings.
    /// </summary>
    public DpcFixResult ApplyFix(DriverAutoFix fix)
    {
        if (!AdminHelper.IsRunningAsAdmin())
            return new DpcFixResult { Success = false, Message = "Administrator privileges required." };

        // Cheap pre-check to avoid running system commands for an already-applied fix. The
        // authoritative dedup is re-done inside the Update below, under the settings lock.
        var current = SettingsManager.Load();
        if (current.AppliedDpcFixes.Any(f => f.FixId == fix.Id))
            return new DpcFixResult { Success = false, Message = "This fix has already been applied." };

        if (RetiredTweakHolding(fix.Id, current) != null)
            return new DpcFixResult
            {
                Success = false,
                Message = "An earlier version of GameShift already made this change from System Tweaks. " +
                          "Undo it with Restore All in Settings before applying it here."
            };

        try
        {
            var (result, applied) = fix.ActionType switch
            {
                "RegistrySet" => ApplyRegistryFix(fix),
                "NetshCommand" => ApplyNetshFix(fix),
                "PowerPlanSetting" => ApplyPowerPlanFix(fix),
                "SetNetAdapterProperty" => ApplyNetAdapterFix(fix),
                _ => (new DpcFixResult { Success = false, Message = $"Unknown action type: {fix.ActionType}" }, (AppliedDpcFix?)null)
            };

            if (result.Success || result.ChangeLeftInPlace)
            {
                // Persist the rollback ledger entry AND the pending-reboot marker in one atomic,
                // lock-guarded transaction; the in-lambda dedup makes a Load-then-write race a no-op.
                SettingsManager.Update(s =>
                {
                    if (applied != null && !s.AppliedDpcFixes.Any(f => f.FixId == applied.FixId))
                        s.AppliedDpcFixes.Add(applied);
                    if (result.Success && fix.RequiresReboot && !s.PendingRebootFixes.Contains(fix.Id))
                        s.PendingRebootFixes.Add(fix.Id);
                });
                if (result.Success)
                    Log.Information("DpcFixEngine: applied fix {FixId} ({Name})", fix.Id, fix.Name);
                else
                    Log.Warning("DpcFixEngine: fix {FixId} failed and could not be rolled back; kept for undo", fix.Id);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DpcFixEngine: failed to apply fix {FixId}", fix.Id);
            return new DpcFixResult { Success = false, Message = ex.Message };
        }
    }

    // The System Tweaks that earlier versions offered for the same settings. Each recorded the value
    // it replaced, so applying the fix on top would capture GameShift's own value, and undoing the
    // two in the wrong order would put it back.
    internal static readonly IReadOnlyDictionary<string, string> RetiredTweakByFixId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disable_mpo"] = nameof(SystemTweaks.Tweaks.DisableMpo),
        ["disable_usb_selective_suspend"] = nameof(SystemTweaks.Tweaks.DisableUsbSelectiveSuspend),
        ["msi_mode_nvidia"] = nameof(SystemTweaks.Tweaks.OptimizeInterruptHandling),
        ["msi_mode_amd"] = nameof(SystemTweaks.Tweaks.OptimizeInterruptHandling),
    };

    /// <summary>
    /// The retired System Tweak an earlier version applied for the same setting as
    /// <paramref name="fixId"/>, or null when there is none.
    /// </summary>
    internal static string? RetiredTweakHolding(string fixId, AppSettings settings) =>
        RetiredTweakByFixId.TryGetValue(fixId, out var tweak)
        && settings.SystemTweaks?.AppliedTweaks.TryGetValue(tweak, out var state) == true
        && state.IsAppliedByGameShift
            ? tweak
            : null;

    /// <summary>
    /// Checks whether a quick fix is currently active (for toggle state display).
    /// </summary>
    public bool IsFixActive(string fixId) => IsFixActive(fixId, SettingsManager.Load());

    /// <summary>
    /// Checks whether a quick fix is active against an already-loaded settings snapshot, so a caller
    /// rendering many fixes does not Load() (and log) once per fix.
    /// </summary>
    public bool IsFixActive(string fixId, AppSettings settings) =>
        settings.AppliedDpcFixes.Any(f => f.FixId == fixId);

    /// <summary>
    /// Reverts a previously applied fix using stored rollback data.
    /// </summary>
    public DpcFixResult RevertFix(string fixId)
    {
        if (!AdminHelper.IsRunningAsAdmin())
            return new DpcFixResult { Success = false, Message = "Administrator privileges required." };

        var applied = SettingsManager.Load().AppliedDpcFixes.FirstOrDefault(f => f.FixId == fixId);
        if (applied == null)
            return new DpcFixResult { Success = false, Message = "Fix not found in applied fixes." };

        try
        {
            var result = applied.ActionType switch
            {
                "RegistrySet" => RevertRegistryFix(applied),
                "BcdEdit" => RevertBcdEditFix(applied),
                "NetshCommand" => RevertNetshFix(applied),
                "PowerPlanSetting" => RevertPowerPlanFix(applied),
                "SetNetAdapterProperty" => RevertNetAdapterFix(applied),
                _ => new DpcFixResult { Success = false, Message = $"Unknown action type: {applied.ActionType}" }
            };

            if (result.Success)
            {
                SettingsManager.Update(s =>
                {
                    s.AppliedDpcFixes.RemoveAll(f => f.FixId == fixId);
                    s.PendingRebootFixes.Remove(fixId);
                });
                Log.Information("DpcFixEngine: reverted fix {FixId}", fixId);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DpcFixEngine: failed to revert fix {FixId}", fixId);
            return new DpcFixResult { Success = false, Message = ex.Message };
        }
    }

    // ── Ledger input validation (security) ─────────────────────────────────────
    // The rollback ledger (AppSettings.AppliedDpcFixes) is persisted to the per-user, user-writable
    // %AppData%\GameShift\settings.json. Revert runs elevated and feeds ledger fields (Target /
    // PreviousValue) into PowerShell, bcdedit, netsh, powercfg, and registry writes. Without
    // validation a tampered entry is a local privilege escalation. Every ledger value used in an
    // elevated action is validated here against the exact shapes GameShift itself produces (see
    // Resources/known-drivers.json); anything else is refused and the action does not run. Pure, so
    // unit-tested independently of the admin-gated execution paths.

    // Net adapter advanced-property registry keywords are alphanumeric and may start with '*'
    // (e.g. *InterruptModeration, *EEE). They reach PowerShell only as environment values.
    private static readonly Regex NetAdapterKeywordPattern =
        new(@"^\*?[A-Za-z0-9]{1,64}$", RegexOptions.Compiled);

    // A plain integer literal - the only thing ever passed as a -RegistryValue / powercfg value.
    private static readonly Regex IntegerLiteralPattern =
        new(@"^-?[0-9]{1,19}$", RegexOptions.Compiled);

    // bcdedit reverts GameShift generates only ever target the two platform-timer elements earlier
    // versions could set (disabledynamictick = Disable Dynamic Tick, useplatformtick = Disable HPET).
    // The element name is ALLOWLISTED, not merely shape-checked, so a tampered ledger cannot revert
    // into e.g. "/set nointegritychecks on" (disabling driver-signature enforcement) or "/set safeboot minimal".
    private static readonly Regex BcdRevertArgsPattern =
        new(@"^/(set|deletevalue)\s+(disabledynamictick|useplatformtick)(\s+[A-Za-z0-9]{1,16})?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A single netsh argument token (int, ip, set, global, taskoffload=enabled, ...). Excludes path
    // separators and quotes; the leading context is additionally allowlisted in IsSafeNetshRevertArgs.
    private static readonly Regex NetshTokenPattern =
        new(@"^[A-Za-z0-9=._-]{1,64}$", RegexOptions.Compiled);

    // Registry value path: a well-formed HKLM path (PCI instance keys contain '&' and spaces).
    private static readonly Regex RegistryTargetCharsPattern =
        new(@"^[A-Za-z0-9 _\\{}().&-]{1,256}$", RegexOptions.Compiled);

    // The exact registry targets GameShift's RegistrySet fixes write. The two static keys are matched
    // exactly; the per-device MSI-mode key has a machine-specific PCI instance path in the middle but a
    // fixed Enum\ root and a fixed value-name suffix. Restricting reverts to these prevents a tampered
    // ledger from redirecting the elevated write to a Run key, a service ImagePath, IFEO, ConfigFlags,
    // or a device's Service binding under Enum\.
    private const string GraphicsDriversHwSchModeTarget =
        @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode";
    private const string DwmOverlayTestModeTarget =
        @"HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode";
    private const string MsiModeEnumPrefix =
        @"HKLM\SYSTEM\CurrentControlSet\Enum\";
    private const string MsiModeValueSuffix =
        @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties\MSISupported";

    internal static bool IsValidNetAdapterKeyword(string? keyword) =>
        keyword != null && NetAdapterKeywordPattern.IsMatch(keyword);

    internal static bool IsIntegerValue(string? value) =>
        value != null && IntegerLiteralPattern.IsMatch(value);

    /// <summary>Validates the bcdedit argument string (after the leading "bcdedit " is stripped).</summary>
    internal static bool IsSafeBcdRevertArgs(string? args) =>
        args != null && BcdRevertArgsPattern.IsMatch(args);

    /// <summary>
    /// Validates the netsh argument string (after the leading "netsh " is stripped). GameShift only
    /// ever applies TCP/IP global settings (e.g. "int ip set global taskoffload=enabled"), so the
    /// leading context is allowlisted - not just the character set - to keep a tampered ledger from
    /// reverting into "advfirewall set ...", "firewall set opmode disable", "int portproxy add ...",
    /// "add helper x.dll", etc.
    /// </summary>
    internal static bool IsSafeNetshRevertArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args) || args.Length > 256) return false;
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5) return false;
        if (!tokens[0].Equals("int", StringComparison.OrdinalIgnoreCase)) return false;
        if (!tokens[1].Equals("ip", StringComparison.OrdinalIgnoreCase) &&
            !tokens[1].Equals("tcp", StringComparison.OrdinalIgnoreCase)) return false;
        if (!tokens[2].Equals("set", StringComparison.OrdinalIgnoreCase)) return false;
        if (!tokens[3].Equals("global", StringComparison.OrdinalIgnoreCase)) return false;
        return tokens.All(NetshTokenPattern.IsMatch);
    }

    /// <summary>
    /// True only for one of the exact registry targets GameShift's RegistrySet fixes write: the
    /// GraphicsDrivers\HwSchMode and Dwm\OverlayTestMode keys, or a per-device MSI-mode key under the
    /// Enum\ root ending exactly at ...\MessageSignaledInterruptProperties\MSISupported. Anything else
    /// (including other keys under Enum\) is refused.
    /// </summary>
    internal static bool IsAllowedRegistryRevertTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Length > 256) return false;
        if (!RegistryTargetCharsPattern.IsMatch(target)) return false;
        if (target.Contains("..")) return false;
        if (target.Equals(GraphicsDriversHwSchModeTarget, StringComparison.OrdinalIgnoreCase)) return true;
        if (target.Equals(DwmOverlayTestModeTarget, StringComparison.OrdinalIgnoreCase)) return true;
        return target.StartsWith(MsiModeEnumPrefix, StringComparison.OrdinalIgnoreCase)
            && target.EndsWith(MsiModeValueSuffix, StringComparison.OrdinalIgnoreCase)
            && target.Length > MsiModeEnumPrefix.Length + MsiModeValueSuffix.Length;
    }

    /// <summary>
    /// Strips a single leading "bcdedit "/"netsh " prefix to recover the bare argument string. Unlike
    /// String.Replace (which removes every occurrence and so could be steered by an attacker), this
    /// only removes one leading, trimmed occurrence and leaves the remainder untouched for validation.
    /// </summary>
    internal static string StripLeadingTool(string command, string toolPrefix)
    {
        var trimmed = command.TrimStart();
        return trimmed.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[toolPrefix.Length..]
            : trimmed;
    }

    // -- Registry fixes ────────────────────────────────────────────

    private (DpcFixResult result, AppliedDpcFix? applied) ApplyRegistryFix(DriverAutoFix fix)
    {
        var regPath = fix.RegistryPath!;

        // Resolve GPU PCI path if needed
        if (regPath.Contains("{detected_nvidia_pci_path}") || regPath.Contains("{detected_amd_pci_path}"))
        {
            var gpuInfo = GpuPciDetector.DetectGpuMsiState();
            if (gpuInfo == null)
                return (new DpcFixResult { Success = false, Message = "No NVIDIA or AMD GPU detected." }, null);

            regPath = gpuInfo.RegistryPath;
        }

        // Strip HKLM\ prefix for Registry API
        var path = regPath.Replace(@"HKLM\", "");

        // Read current value for rollback
        string? previousValue = null;
        using (var key = Registry.LocalMachine.OpenSubKey(path))
        {
            var val = key?.GetValue(fix.RegistryKey!);
            previousValue = val?.ToString();
        }

        // Create key path if needed and set value
        using (var key = Registry.LocalMachine.CreateSubKey(path))
        {
            if (key == null)
                return (new DpcFixResult { Success = false, Message = $"Failed to create registry key: {path}" }, null);

            var regKind = fix.RegistryType?.ToUpperInvariant() switch
            {
                "DWORD" => RegistryValueKind.DWord,
                "QWORD" => RegistryValueKind.QWord,
                "STRING" => RegistryValueKind.String,
                _ => RegistryValueKind.DWord
            };

            object regValue = regKind == RegistryValueKind.DWord
                ? int.Parse(fix.RegistryValue!)
                : fix.RegistryValue!;

            key.SetValue(fix.RegistryKey!, regValue, regKind);
        }

        // Build the rollback ledger entry; ApplyFix persists it transactionally.
        var applied = new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "RegistrySet",
            PreviousValue = previousValue,
            Target = $@"HKLM\{path}\{fix.RegistryKey}",
            AppliedAt = DateTime.Now,
            RequiresReboot = fix.RequiresReboot
        };

        return (new DpcFixResult
        {
            Success = true,
            Message = fix.RequiresReboot ? "Fix applied. Reboot required." : "Fix applied successfully.",
            RebootRequired = fix.RequiresReboot
        }, applied);
    }

    private DpcFixResult RevertRegistryFix(AppliedDpcFix applied)
    {
        // Refuse a tampered ledger from redirecting this elevated write to an arbitrary HKLM key.
        if (!IsAllowedRegistryRevertTarget(applied.Target))
        {
            Log.Warning("[DpcFix] Rejecting registry revert to disallowed target for {FixId}", applied.FixId);
            return new DpcFixResult { Success = false, Message = "Registry revert target not allowed." };
        }

        // Parse "HKLM\path\to\key\ValueName" -> path + valueName
        var target = applied.Target.Replace(@"HKLM\", "");
        var lastSlash = target.LastIndexOf('\\');
        var path = target[..lastSlash];
        var valueName = target[(lastSlash + 1)..];

        using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
        if (key == null)
            return new DpcFixResult { Success = false, Message = $"Registry key not found: {path}" };

        if (applied.PreviousValue == null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            if (long.TryParse(applied.PreviousValue, out long numValue))
            {
                if (numValue >= int.MinValue && numValue <= int.MaxValue)
                    key.SetValue(valueName, (int)numValue, RegistryValueKind.DWord);
                else
                    key.SetValue(valueName, numValue, RegistryValueKind.QWord);
            }
            else
            {
                key.SetValue(valueName, applied.PreviousValue, RegistryValueKind.String);
            }
        }

        return new DpcFixResult { Success = true, Message = "Fix reverted.", RebootRequired = applied.RequiresReboot };
    }

    // -- BcdEdit fixes (revert only) ───────────────────────────────

    private DpcFixResult RevertBcdEditFix(AppliedDpcFix applied)
    {
        if (string.IsNullOrEmpty(applied.PreviousValue))
            return new DpcFixResult { Success = false, Message = "No revert command stored." };

        var args = StripLeadingTool(applied.PreviousValue, "bcdedit ");
        if (!IsSafeBcdRevertArgs(args))
        {
            Log.Warning("[DpcFix] Rejecting unsafe bcdedit revert command for {FixId}", applied.FixId);
            return new DpcFixResult { Success = false, Message = "Unsafe bcdedit revert command rejected." };
        }

        var (success, output) = RunProcess(NativeInterop.SystemExePath("bcdedit.exe"), args);
        return new DpcFixResult
        {
            Success = success,
            Message = success ? "Fix reverted. Reboot required." : $"Revert failed: {output}",
            RebootRequired = true
        };
    }

    // -- Netsh fixes ───────────────────────────────────────────────

    private (DpcFixResult result, AppliedDpcFix? applied) ApplyNetshFix(DriverAutoFix fix)
    {
        var (success, output) = RunProcess(NativeInterop.SystemExePath("netsh.exe"), StripLeadingTool(fix.Command!, "netsh "));
        if (!success)
            return (new DpcFixResult { Success = false, Message = $"netsh failed: {output}" }, null);

        var applied = new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "NetshCommand",
            PreviousValue = fix.RevertCommand,
            Target = fix.Command!,
            AppliedAt = DateTime.Now,
            RequiresReboot = fix.RequiresReboot
        };

        return (new DpcFixResult { Success = true, Message = "Fix applied.", RebootRequired = fix.RequiresReboot }, applied);
    }

    private DpcFixResult RevertNetshFix(AppliedDpcFix applied)
    {
        if (string.IsNullOrEmpty(applied.PreviousValue))
            return new DpcFixResult { Success = false, Message = "No revert command stored." };

        var args = StripLeadingTool(applied.PreviousValue, "netsh ");
        if (!IsSafeNetshRevertArgs(args))
        {
            Log.Warning("[DpcFix] Rejecting unsafe netsh revert command for {FixId}", applied.FixId);
            return new DpcFixResult { Success = false, Message = "Unsafe netsh revert command rejected." };
        }

        var (success, output) = RunProcess(NativeInterop.SystemExePath("netsh.exe"), args);
        return new DpcFixResult { Success = success, Message = success ? "Fix reverted." : $"Revert failed: {output}" };
    }

    // -- Power plan fixes ──────────────────────────────────────────

    internal (DpcFixResult result, AppliedDpcFix? applied) ApplyPowerPlanFix(DriverAutoFix fix)
    {
        // A power plan sub-setting such as USB selective suspend
        var subgroup = fix.Subgroup ?? "";
        var setting = fix.Setting ?? "";
        var value = fix.Value ?? "0";

        if (!Guid.TryParse(subgroup, out var subgroupGuid) || !Guid.TryParse(setting, out var settingGuid))
            return (new DpcFixResult { Success = false, Message = "Invalid power setting." }, null);

        // Without the current value the fix could not be undone, so nothing is changed.
        var previous = ReadAcIndex(subgroupGuid, settingGuid);
        if (previous == null)
            return (new DpcFixResult { Success = false, Message = "The current power setting could not be read, so nothing was changed." }, null);

        var (sSuccess, sOutput) = RunPowercfg($"/setacvalueindex SCHEME_CURRENT {subgroupGuid} {settingGuid} {value}");
        if (!sSuccess)
            return (new DpcFixResult { Success = false, Message = $"powercfg failed: {sOutput}" }, null);

        var subApplied = new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "PowerPlanSetting",
            PreviousValue = previous.Value.ToString(CultureInfo.InvariantCulture),
            Target = $"{subgroupGuid}|{settingGuid}",
            AppliedAt = DateTime.Now,
            RequiresReboot = false
        };

        var (aSuccess, aOutput) = RunPowercfg("/setactive SCHEME_CURRENT");
        if (!aSuccess)
        {
            var (rSuccess, _) = RunPowercfg($"/setacvalueindex SCHEME_CURRENT {subgroupGuid} {settingGuid} {previous.Value}");
            var (rActive, _) = RunPowercfg("/setactive SCHEME_CURRENT");
            if (rSuccess && rActive)
                return (new DpcFixResult { Success = false, Message = $"powercfg could not apply the setting, so the earlier value was put back: {aOutput}" }, null);

            return (new DpcFixResult
            {
                Success = false,
                ChangeLeftInPlace = true,
                Message = $"powercfg could not apply the setting or put the earlier value back. It stays recorded so it can be undone later: {aOutput}"
            }, subApplied);
        }

        return (new DpcFixResult { Success = true, Message = "Power plan setting applied." }, subApplied);
    }

    /// <summary>
    /// True for a power setting entry that earlier versions recorded without the value it replaced;
    /// a revert sets it back to Windows' default instead.
    /// </summary>
    internal static bool IsUnrecordedPowerSetting(AppliedDpcFix applied) =>
        applied.ActionType == "PowerPlanSetting"
        && string.IsNullOrEmpty(applied.PreviousValue)
        && applied.Target.Contains('|');

    internal DpcFixResult RevertPowerPlanFix(AppliedDpcFix applied)
    {
        if (Guid.TryParse(applied.Target, out _))
        {
            // Power plan GUID - revert to previous plan. The previous value must itself be a GUID so a
            // tampered ledger cannot inject extra powercfg arguments.
            if (string.IsNullOrEmpty(applied.PreviousValue) || !Guid.TryParse(applied.PreviousValue, out _))
                return new DpcFixResult { Success = false, Message = "No valid previous power plan GUID stored." };

            var (success, output) = RunPowercfg($"/setactive {applied.PreviousValue}");
            return new DpcFixResult { Success = success, Message = success ? "Power plan reverted." : output };
        }

        // Sub-setting revert. Both GUID tokens and the numeric value are validated so the interpolated
        // powercfg arguments cannot be widened by a tampered ledger.
        var parts = applied.Target.Split('|');
        if (parts.Length != 2)
            return new DpcFixResult { Success = false, Message = "Invalid revert target." };
        if (!Guid.TryParse(parts[0], out var subgroupGuid) || !Guid.TryParse(parts[1], out var settingGuid))
            return new DpcFixResult { Success = false, Message = "Invalid power plan revert target." };

        bool recorded = !IsUnrecordedPowerSetting(applied);
        uint revertValue;
        if (recorded)
        {
            if (!IsIntegerValue(applied.PreviousValue)
                || !uint.TryParse(applied.PreviousValue, NumberStyles.None, CultureInfo.InvariantCulture, out revertValue))
                return new DpcFixResult { Success = false, Message = "Invalid power plan revert target." };
        }
        else
        {
            var defaultValue = ReadAcDefaultIndex(subgroupGuid, settingGuid);
            if (defaultValue == null)
                return new DpcFixResult { Success = false, Message = "The value before the fix was not recorded and the Windows default could not be read, so nothing was changed." };
            revertValue = defaultValue.Value;
        }

        var (s, o) = RunPowercfg($"/setacvalueindex SCHEME_CURRENT {subgroupGuid} {settingGuid} {revertValue}");
        if (!s)
            return new DpcFixResult { Success = false, Message = o };

        // A failed activation keeps the entry; running the revert again writes the same value.
        var (a, ao) = RunPowercfg("/setactive SCHEME_CURRENT");
        if (!a)
            return new DpcFixResult { Success = false, Message = $"powercfg could not apply the setting: {ao}" };

        return new DpcFixResult
        {
            Success = true,
            Message = recorded
                ? "Setting reverted."
                : "The value before the fix was not recorded, so the Windows default was restored."
        };
    }

    // -- Network adapter fixes ─────────────────────────────────────

    internal (DpcFixResult result, AppliedDpcFix? applied) ApplyNetAdapterFix(DriverAutoFix fix)
    {
        var property = fix.Property ?? "";
        var value = fix.Value ?? "0";

        // Reject non-numeric values to prevent PowerShell injection
        if (!int.TryParse(value, out _))
        {
            Log.Warning("[DpcFix] Rejecting non-numeric adapter value: {Value}", value);
            return (new DpcFixResult { Success = false, Message = $"Invalid non-numeric adapter value: {value}" }, null);
        }

        // The property name is stored as the revert Target, so validate it even though it originates
        // from the built-in fix database.
        if (!IsValidNetAdapterKeyword(property))
        {
            Log.Warning("[DpcFix] Rejecting unsafe net adapter keyword on apply: {Property}", property);
            return (new DpcFixResult { Success = false, Message = $"Invalid network adapter keyword: {property}" }, null);
        }

        // Query current value from the first physical adapter that has this property
        string previousValue = QueryNetAdapterPropertyValue(property) ?? "1";

        // Use PowerShell to set the property on all physical adapters. The value goes as validated,
        // minus surrounding whitespace, so a revert writes back exactly the string it captured.
        var (success, output) = RunNetAdapterScript(SetNetAdapterPropertyScript, property, value.Trim());

        // Store rollback with the actual previous value
        var applied = new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "SetNetAdapterProperty",
            PreviousValue = previousValue,
            Target = property,
            AppliedAt = DateTime.Now,
            RequiresReboot = false
        };

        return (new DpcFixResult
        {
            // -ErrorAction SilentlyContinue means a property missing on some adapters is not a failure
            // (exit code stays 0); only a genuine failure (non-zero exit / process did not run) is reported.
            Success = success,
            Message = success ? "Network adapter property updated on all physical adapters." : $"Update failed: {output}"
        }, applied);
    }

    internal DpcFixResult RevertNetAdapterFix(AppliedDpcFix applied)
    {
        // Reject non-numeric values to prevent PowerShell injection
        var revertValue = applied.PreviousValue ?? "1";
        if (!int.TryParse(revertValue, out _))
        {
            Log.Warning("[DpcFix] Rejecting non-numeric adapter revert value: {Value}", revertValue);
            return new DpcFixResult { Success = false, Message = $"Invalid non-numeric adapter revert value: {revertValue}" };
        }

        // The keyword comes from the user-writable ledger and the numeric guard above does not cover
        // it. Allow only real keyword shapes.
        if (!IsValidNetAdapterKeyword(applied.Target))
        {
            Log.Warning("[DpcFix] Rejecting unsafe net adapter keyword on revert for {FixId}", applied.FixId);
            return new DpcFixResult { Success = false, Message = "Invalid network adapter keyword." };
        }

        var (success, output) = RunNetAdapterScript(SetNetAdapterPropertyScript, applied.Target, revertValue.Trim());
        // Report the real result. -ErrorAction SilentlyContinue means a property missing on some
        // adapters is not a failure (exit code stays 0); but a genuine failure must be surfaced so the
        // rollback entry is kept for a retry instead of being silently dropped from the ledger.
        return new DpcFixResult
        {
            Success = success,
            Message = success ? "Network adapter property reverted." : $"Revert failed: {output}"
        };
    }

    /// <summary>
    /// Queries the current registry value of a net adapter advanced property.
    /// Returns the first adapter's value, or null if the query fails.
    /// </summary>
    private string? QueryNetAdapterPropertyValue(string registryKeyword)
    {
        try
        {
            var (ok, output) = RunNetAdapterScript(QueryNetAdapterPropertyScript, registryKeyword);
            if (ok && !string.IsNullOrWhiteSpace(output))
            {
                var val = output.Trim().Split('\n')[0].Trim();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DpcFixEngine: failed to query net adapter property {Keyword}", registryKeyword);
        }

        return null;
    }

    // -- Process runner ────────────────────────────────────────────

    private const string NetAdapterModule = "NetAdapter";
    private const string SetNetAdapterPropertyScript =
        "Get-NetAdapter -Physical | Set-NetAdapterAdvancedProperty -RegistryKeyword $env:GS_ARG0 " +
        "-RegistryValue $env:GS_ARG1 -ErrorAction SilentlyContinue";
    private const string QueryNetAdapterPropertyScript =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
        "Get-NetAdapter -Physical | Get-NetAdapterAdvancedProperty -RegistryKeyword $env:GS_ARG0 " +
        "-ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty RegistryValue";

    /// <summary>Process-start seam for the net adapter scripts; tests replace it so no PowerShell runs.</summary>
    internal Func<ProcessStartInfo, int, ProcessRunResult> RunPowerShell { get; set; } = ParameterizedPowerShell.Run;

    /// <summary>powercfg seam; tests replace it so no power setting changes.</summary>
    internal Func<string, (bool success, string output)> RunPowercfg { get; set; } =
        args => RunProcess(NativeInterop.SystemExePath("powercfg.exe"), args);

    /// <summary>Reads the active scheme's AC index; tests replace it.</summary>
    internal Func<Guid, Guid, uint?> ReadAcIndex { get; set; } =
        (subgroup, setting) => ActiveSchemePowerSetting.ReadIndex(subgroup, setting, ac: true);

    /// <summary>Reads Windows' default AC index for the active scheme; tests replace it.</summary>
    internal Func<Guid, Guid, uint?> ReadAcDefaultIndex { get; set; } =
        (subgroup, setting) => ActiveSchemePowerSetting.ReadDefaultIndex(subgroup, setting, ac: true);

    private static (bool success, string output) RunProcess(string fileName, string arguments)
    {
        try
        {
            return ToOutcome(ProcessRunner.Run(fileName, arguments, 10_000));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool success, string output) RunNetAdapterScript([ConstantExpected] string script, params string[] values)
    {
        try
        {
            return ToOutcome(RunPowerShell(ParameterizedPowerShell.CreateStartInfo(NetAdapterModule, script, values), 10_000));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool success, string output) ToOutcome(ProcessRunResult result)
    {
        if (!result.Exited && !result.TimedOut)
            return (false, "Failed to start process.");

        var output = string.IsNullOrWhiteSpace(result.StdErr)
            ? result.StdOut.Trim()
            : $"{result.StdOut.Trim()}\n{result.StdErr.Trim()}";
        // success only on a clean exit with code 0; a timeout (child killed) reports failure
        // with whatever partial output was captured.
        return (result.Exited && result.ExitCode == 0, output);
    }
}
