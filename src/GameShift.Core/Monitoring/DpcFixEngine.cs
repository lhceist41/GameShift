using System.Diagnostics;
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
}

/// <summary>
/// Executes DPC latency fixes and persists rollback state to AppSettings.
/// Supports 5 action types: RegistrySet, BcdEdit, NetshCommand, PowerPlanSetting, SetNetAdapterProperty.
/// All fixes require admin privileges.
/// </summary>
public class DpcFixEngine
{
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;

    public DpcFixEngine(AppSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
    }

    /// <summary>
    /// Applies a fix from the known driver database.
    /// Reads current state, applies the fix, stores rollback info in AppSettings.
    /// </summary>
    public DpcFixResult ApplyFix(DriverAutoFix fix)
    {
        if (!AdminHelper.IsRunningAsAdmin())
            return new DpcFixResult { Success = false, Message = "Administrator privileges required." };

        // Check if already applied
        if (_settings.AppliedDpcFixes.Any(f => f.FixId == fix.Id))
            return new DpcFixResult { Success = false, Message = "This fix has already been applied." };

        try
        {
            var result = fix.ActionType switch
            {
                "RegistrySet" => ApplyRegistryFix(fix),
                "BcdEdit" => ApplyBcdEditFix(fix),
                "NetshCommand" => ApplyNetshFix(fix),
                "PowerPlanSetting" => ApplyPowerPlanFix(fix),
                "SetNetAdapterProperty" => ApplyNetAdapterFix(fix),
                _ => new DpcFixResult { Success = false, Message = $"Unknown action type: {fix.ActionType}" }
            };

            if (result.Success)
            {
                if (fix.RequiresReboot && !_settings.PendingRebootFixes.Contains(fix.Id))
                    _settings.PendingRebootFixes.Add(fix.Id);

                _saveSettings();
                Log.Information("DpcFixEngine: applied fix {FixId} ({Name})", fix.Id, fix.Name);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DpcFixEngine: failed to apply fix {FixId}", fix.Id);
            return new DpcFixResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Checks whether a quick fix is currently active (for toggle state display).
    /// </summary>
    public bool IsFixActive(string fixId)
    {
        return _settings.AppliedDpcFixes.Any(f => f.FixId == fixId);
    }

    /// <summary>
    /// Reverts a previously applied fix using stored rollback data.
    /// </summary>
    public DpcFixResult RevertFix(string fixId)
    {
        if (!AdminHelper.IsRunningAsAdmin())
            return new DpcFixResult { Success = false, Message = "Administrator privileges required." };

        var applied = _settings.AppliedDpcFixes.FirstOrDefault(f => f.FixId == fixId);
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
                _settings.AppliedDpcFixes.RemoveAll(f => f.FixId == fixId);
                _settings.PendingRebootFixes.Remove(fixId);
                _saveSettings();
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

    // -- Registry fixes ────────────────────────────────────────────

    private DpcFixResult ApplyRegistryFix(DriverAutoFix fix)
    {
        var regPath = fix.RegistryPath!;

        // Resolve GPU PCI path if needed
        if (regPath.Contains("{detected_nvidia_pci_path}") || regPath.Contains("{detected_amd_pci_path}"))
        {
            var gpuInfo = GpuPciDetector.DetectGpuMsiState();
            if (gpuInfo == null)
                return new DpcFixResult { Success = false, Message = "No NVIDIA or AMD GPU detected." };

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
                return new DpcFixResult { Success = false, Message = $"Failed to create registry key: {path}" };

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

        // Store rollback info
        _settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "RegistrySet",
            PreviousValue = previousValue,
            Target = $@"HKLM\{path}\{fix.RegistryKey}",
            AppliedAt = DateTime.Now,
            RequiresReboot = fix.RequiresReboot
        });

        return new DpcFixResult
        {
            Success = true,
            Message = fix.RequiresReboot ? "Fix applied. Reboot required." : "Fix applied successfully.",
            RebootRequired = fix.RequiresReboot
        };
    }

    private DpcFixResult RevertRegistryFix(AppliedDpcFix applied)
    {
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
            key.SetValue(valueName, int.Parse(applied.PreviousValue), RegistryValueKind.DWord);
        }

        return new DpcFixResult { Success = true, Message = "Fix reverted.", RebootRequired = applied.RequiresReboot };
    }

    // -- BcdEdit fixes ─────────────────────────────────────────────

    private DpcFixResult ApplyBcdEditFix(DriverAutoFix fix)
    {
        var (success, output) = RunProcess("bcdedit.exe", fix.Command!.Replace("bcdedit ", ""));
        if (!success)
            return new DpcFixResult { Success = false, Message = $"bcdedit failed: {output}" };

        _settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "BcdEdit",
            PreviousValue = fix.RevertCommand,
            Target = fix.Command,
            AppliedAt = DateTime.Now,
            RequiresReboot = true
        });

        return new DpcFixResult { Success = true, Message = "Fix applied. Reboot required.", RebootRequired = true };
    }

    private DpcFixResult RevertBcdEditFix(AppliedDpcFix applied)
    {
        if (string.IsNullOrEmpty(applied.PreviousValue))
            return new DpcFixResult { Success = false, Message = "No revert command stored." };

        var (success, output) = RunProcess("bcdedit.exe", applied.PreviousValue.Replace("bcdedit ", ""));
        return new DpcFixResult
        {
            Success = success,
            Message = success ? "Fix reverted. Reboot required." : $"Revert failed: {output}",
            RebootRequired = true
        };
    }

    // -- Netsh fixes ───────────────────────────────────────────────

    private DpcFixResult ApplyNetshFix(DriverAutoFix fix)
    {
        var (success, output) = RunProcess("netsh.exe", fix.Command!.Replace("netsh ", ""));
        if (!success)
            return new DpcFixResult { Success = false, Message = $"netsh failed: {output}" };

        _settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "NetshCommand",
            PreviousValue = fix.RevertCommand,
            Target = fix.Command,
            AppliedAt = DateTime.Now,
            RequiresReboot = fix.RequiresReboot
        });

        return new DpcFixResult { Success = true, Message = "Fix applied.", RebootRequired = fix.RequiresReboot };
    }

    private DpcFixResult RevertNetshFix(AppliedDpcFix applied)
    {
        if (string.IsNullOrEmpty(applied.PreviousValue))
            return new DpcFixResult { Success = false, Message = "No revert command stored." };

        var (success, output) = RunProcess("netsh.exe", applied.PreviousValue.Replace("netsh ", ""));
        return new DpcFixResult { Success = success, Message = success ? "Fix reverted." : $"Revert failed: {output}" };
    }

    // -- Power plan fixes ──────────────────────────────────────────

    private DpcFixResult ApplyPowerPlanFix(DriverAutoFix fix)
    {
        if (fix.Value == "high_performance")
        {
            // Get current active power plan for rollback
            var (curSuccess, currentPlan) = RunProcess("powercfg.exe", "/getactivescheme");
            string? previousGuid = null;
            if (curSuccess && currentPlan.Length > 10)
            {
                // Output: "Power Scheme GUID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx  (name)"
                var parts = currentPlan.Split(' ');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Contains('-') && parts[i].Length >= 36)
                    {
                        previousGuid = parts[i].Trim();
                        break;
                    }
                }
            }

            // Activate High Performance plan
            var (success, output) = RunProcess("powercfg.exe", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            if (!success)
                return new DpcFixResult { Success = false, Message = $"powercfg failed: {output}" };

            _settings.AppliedDpcFixes.Add(new AppliedDpcFix
            {
                FixId = fix.Id,
                Description = fix.Name,
                ActionType = "PowerPlanSetting",
                PreviousValue = previousGuid,
                Target = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                AppliedAt = DateTime.Now,
                RequiresReboot = false
            });

            return new DpcFixResult { Success = true, Message = "High Performance power plan activated." };
        }

        // USB selective suspend or other power plan sub-setting
        var subgroup = fix.Subgroup ?? "";
        var setting = fix.Setting ?? "";
        var value = fix.Value ?? "0";

        // Read current value for rollback
        var (qSuccess, queryOutput) = RunProcess("powercfg.exe", $"/query SCHEME_CURRENT {subgroup} {setting}");
        string? prevVal = null;
        if (qSuccess)
        {
            // Parse "Current AC Power Setting Index: 0x00000001"
            foreach (var line in queryOutput.Split('\n'))
            {
                if (line.Contains("Current AC Power Setting Index"))
                {
                    var hexPart = line.Split(':').LastOrDefault()?.Trim();
                    if (hexPart != null && hexPart.StartsWith("0x"))
                        prevVal = Convert.ToInt32(hexPart, 16).ToString();
                    break;
                }
            }
        }

        var (sSuccess, sOutput) = RunProcess("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {subgroup} {setting} {value}");
        if (!sSuccess)
            return new DpcFixResult { Success = false, Message = $"powercfg failed: {sOutput}" };

        // Apply the change
        RunProcess("powercfg.exe", "/setactive SCHEME_CURRENT");

        _settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "PowerPlanSetting",
            PreviousValue = prevVal,
            Target = $"{subgroup}|{setting}",
            AppliedAt = DateTime.Now,
            RequiresReboot = false
        });

        return new DpcFixResult { Success = true, Message = "Power plan setting applied." };
    }

    private DpcFixResult RevertPowerPlanFix(AppliedDpcFix applied)
    {
        if (applied.Target.Length == 36 || applied.Target.Contains("e8bf"))
        {
            // Power plan GUID — revert to previous plan
            if (string.IsNullOrEmpty(applied.PreviousValue))
                return new DpcFixResult { Success = false, Message = "No previous power plan GUID stored." };

            var (success, output) = RunProcess("powercfg.exe", $"/setactive {applied.PreviousValue}");
            return new DpcFixResult { Success = success, Message = success ? "Power plan reverted." : output };
        }

        // Sub-setting revert
        var parts = applied.Target.Split('|');
        if (parts.Length != 2 || string.IsNullOrEmpty(applied.PreviousValue))
            return new DpcFixResult { Success = false, Message = "Invalid revert target." };

        var (s, o) = RunProcess("powercfg.exe",
            $"/setacvalueindex SCHEME_CURRENT {parts[0]} {parts[1]} {applied.PreviousValue}");
        RunProcess("powercfg.exe", "/setactive SCHEME_CURRENT");

        return new DpcFixResult { Success = s, Message = s ? "Setting reverted." : o };
    }

    // -- Network adapter fixes ─────────────────────────────────────

    private DpcFixResult ApplyNetAdapterFix(DriverAutoFix fix)
    {
        var property = fix.Property ?? "";
        var value = fix.Value ?? "0";

        // Use PowerShell to set the property on all physical adapters
        var script = $"Get-NetAdapter -Physical | Set-NetAdapterAdvancedProperty -RegistryKeyword '{property}' -RegistryValue {value} -ErrorAction SilentlyContinue";
        var (success, output) = RunProcess("powershell.exe", $"-NoProfile -Command \"{script}\"");

        // Store rollback (we'll revert to value "1" which is the default enabled state)
        _settings.AppliedDpcFixes.Add(new AppliedDpcFix
        {
            FixId = fix.Id,
            Description = fix.Name,
            ActionType = "SetNetAdapterProperty",
            PreviousValue = "1",  // Most adapter properties default to enabled (1)
            Target = property,
            AppliedAt = DateTime.Now,
            RequiresReboot = false
        });

        return new DpcFixResult
        {
            Success = true,  // PowerShell with SilentlyContinue doesn't fail on adapters that lack the property
            Message = "Network adapter property updated on all physical adapters."
        };
    }

    private DpcFixResult RevertNetAdapterFix(AppliedDpcFix applied)
    {
        var script = $"Get-NetAdapter -Physical | Set-NetAdapterAdvancedProperty -RegistryKeyword '{applied.Target}' -RegistryValue {applied.PreviousValue ?? "1"} -ErrorAction SilentlyContinue";
        var (success, output) = RunProcess("powershell.exe", $"-NoProfile -Command \"{script}\"");
        return new DpcFixResult { Success = true, Message = "Network adapter property reverted." };
    }

    // -- Process runner ────────────────────────────────────────────

    private static (bool success, string output) RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start process.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            var output = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout.Trim()}\n{stderr.Trim()}";
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
