using System.Diagnostics;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Advisory module for detecting and managing VBS (Virtualization-Based Security)
/// and HVCI (Hypervisor-enforced Code Integrity) state.
/// NOT an IOptimization — VBS/HVCI changes require reboot and cannot participate
/// in the per-game Apply/Revert lifecycle.
/// </summary>
public class VbsHvciToggle
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // ── Registry paths ──────────────────────────────────────────────────

    private const string DeviceGuardPath =
        @"SYSTEM\CurrentControlSet\Control\DeviceGuard";

    private const string HvciScenarioPath =
        @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    private const string VbsValueName = "EnableVirtualizationBasedSecurity";
    private const string HvciValueName = "Enabled";

    // ── Public state properties ─────────────────────────────────────────

    /// <summary>
    /// Whether Virtualization-Based Security is enabled via DeviceGuard registry.
    /// </summary>
    public bool IsVbsEnabled { get; private set; }

    /// <summary>
    /// Whether Hypervisor-enforced Code Integrity (Memory Integrity) is enabled.
    /// </summary>
    public bool IsHvciEnabled { get; private set; }

    /// <summary>
    /// Whether either VBS or HVCI is currently enabled.
    /// </summary>
    public bool IsEitherEnabled => IsVbsEnabled || IsHvciEnabled;

    /// <summary>
    /// Banner message shown when VBS/HVCI is detected as enabled.
    /// </summary>
    public string BannerMessage =>
        "Memory Integrity (VBS/HVCI) is enabled \u2014 this costs 5-15% gaming FPS.";

    /// <summary>
    /// Whether the dashboard should display the VBS/HVCI warning banner.
    /// True when either VBS or HVCI is enabled AND user has not dismissed the notification.
    /// </summary>
    public bool ShouldShowBanner
    {
        get
        {
            if (!IsEitherEnabled)
                return false;

            var settings = SettingsManager.Load();
            return !settings.VbsHvciNotificationDismissed;
        }
    }

    /// <summary>
    /// Warning message displayed when VBS-requiring anti-cheat is detected.
    /// </summary>
    public string VanguardWarning =>
        "Riot Vanguard / FACEIT Anti-Cheat requires Memory Integrity (VBS/HVCI) to be enabled. " +
        "Disabling it will prevent you from playing Valorant, League of Legends, and FACEIT-protected games.";

    // ── Detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads VBS and HVCI state from DeviceGuard registry keys and caches the results.
    /// Updates AppSettings.VbsHvciLastChecked timestamp.
    /// Call once on startup.
    /// </summary>
    public void CheckState()
    {
        try
        {
            // Check VBS: HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard -> EnableVirtualizationBasedSecurity
            IsVbsEnabled = ReadRegistryDword(DeviceGuardPath, VbsValueName) == 1;

            // Check HVCI: DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity -> Enabled
            IsHvciEnabled = ReadRegistryDword(HvciScenarioPath, HvciValueName) == 1;

            _logger.Information(
                "VbsHvciToggle: VBS={VbsEnabled}, HVCI={HvciEnabled}, Either={EitherEnabled}",
                IsVbsEnabled, IsHvciEnabled, IsEitherEnabled);

            // Update last-checked timestamp in settings
            var settings = SettingsManager.Load();
            settings.VbsHvciLastChecked = DateTime.UtcNow;
            SettingsManager.Save(settings);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VbsHvciToggle: Failed to check VBS/HVCI state");
            // Default to false on error — do not show false positives
            IsVbsEnabled = false;
            IsHvciEnabled = false;
        }
    }

    // ── Banner dismiss ─────────────────────────────────────────────────

    /// <summary>
    /// Dismisses the VBS/HVCI warning banner permanently by saving the preference.
    /// </summary>
    public void DismissBanner()
    {
        try
        {
            var settings = SettingsManager.Load();
            settings.VbsHvciNotificationDismissed = true;
            SettingsManager.Save(settings);
            _logger.Information("VbsHvciToggle: User dismissed VBS/HVCI warning banner");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VbsHvciToggle: Failed to dismiss banner");
        }
    }

    // ── Riot game safety interlock ────────────────────────────────────────

    /// <summary>
    /// Checks whether any Riot Vanguard game executables exist on disk.
    /// Stronger than IsVanguardInstalled() — checks for game files even if
    /// Vanguard service isn't running. Used by VBS safety interlock.
    /// </summary>
    public bool AreRiotGamesOnDisk()
    {
        try
        {
            var riotPaths = new[]
            {
                @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
                @"C:\Riot Games\League of Legends\Game\League of Legends.exe",
                @"C:\Program Files\Riot Vanguard\vgc.exe"
            };

            foreach (var path in riotPaths)
            {
                if (File.Exists(path))
                {
                    _logger.Information("VbsHvciToggle: Riot game detected on disk: {Path}", path);
                    return true;
                }
            }

            // Also check via existing Vanguard detection methods
            return IsVanguardInstalled();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "VbsHvciToggle: Error checking for Riot games on disk");
            return false;
        }
    }

    /// <summary>Whether VBS disable is blocked due to VBS-requiring anti-cheat being installed.</summary>
    public bool IsVbsDisableBlocked => AntiCheatDetector.IsVbsRequiredByAntiCheat();

    /// <summary>
    /// Returns the names of anti-cheat systems that require VBS/HVCI.
    /// Used for UI display (error messages, tooltips).
    /// </summary>
    public List<string> GetBlockingAntiCheats() =>
        AntiCheatDetector.GetVbsRequiringAntiCheats()
            .Select(ac => ac.DisplayName)
            .ToList();

    // ── Disable VBS/HVCI ───────────────────────────────────────────────

    /// <summary>
    /// Disables both VBS and HVCI by setting registry values to 0 AND disabling
    /// the hypervisor launch type via bcdedit. Both are required — registry alone
    /// is insufficient on many modern systems where UEFI firmware or Group Policy
    /// can re-enable VBS on reboot.
    /// BLOCKED when Riot games are detected on disk (Vanguard requires HVCI).
    /// Changes take effect after reboot. NEVER initiates a reboot — the caller
    /// (UI layer) handles the reboot prompt via ScheduleReboot.
    /// </summary>
    /// <returns>True if all changes succeeded, false on failure or blocked.</returns>
    public bool DisableVbsHvci()
    {
        try
        {
            // Safety interlock: block VBS disable when VBS-requiring anti-cheat is installed
            if (AntiCheatDetector.IsVbsRequiredByAntiCheat())
            {
                var blockers = string.Join(", ", GetBlockingAntiCheats());
                _logger.Warning(
                    "VbsHvciToggle: VBS disable BLOCKED — {AntiCheats} detected. These require HVCI to be enabled.",
                    blockers);
                return false;
            }

            _logger.Information("VbsHvciToggle: Disabling VBS and HVCI");

            // 1. Disable HVCI (Memory Integrity)
            bool hvciResult = WriteRegistryDword(HvciScenarioPath, HvciValueName, 0);
            if (!hvciResult)
            {
                _logger.Error("VbsHvciToggle: Failed to disable HVCI");
                return false;
            }

            // 2. Disable VBS
            bool vbsResult = WriteRegistryDword(DeviceGuardPath, VbsValueName, 0);
            if (!vbsResult)
            {
                _logger.Error("VbsHvciToggle: Failed to disable VBS");
                return false;
            }

            // 3. Clear RequirePlatformSecurityFeatures (prevents UEFI from re-enabling)
            WriteRegistryDword(DeviceGuardPath, "RequirePlatformSecurityFeatures", 0);

            // 4. Disable hypervisor launch — without this, the hypervisor still loads
            //    at boot and Windows can re-enable VBS despite registry values being 0.
            //    This is the most common reason VBS disable doesn't persist after reboot.
            SetHypervisorLaunchType(off: true);

            // Track that GameShift performed this change
            var settings = SettingsManager.Load();
            settings.VbsHvciDisabledByGameShift = true;
            SettingsManager.Save(settings);

            _logger.Information(
                "VbsHvciToggle: VBS and HVCI disabled successfully. Reboot required for changes to take effect");

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VbsHvciToggle: Failed to disable VBS/HVCI");
            return false;
        }
    }

    // ── Schedule reboot ────────────────────────────────────────────────

    /// <summary>
    /// Schedules a graceful system reboot with a 30-second countdown.
    /// Only call this after the user explicitly confirms they want to reboot.
    /// NEVER call automatically.
    /// </summary>
    public static void ScheduleReboot()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = NativeInterop.SystemExePath("shutdown.exe"),
            Arguments = "/r /t 30 /c \"GameShift: Disabling Memory Integrity for better gaming performance.\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    // ── Vanguard compatibility check ───────────────────────────────────

    /// <summary>
    /// Checks whether Riot Vanguard anti-cheat is installed or running.
    /// Used to show a cautionary warning before disabling HVCI.
    /// </summary>
    /// <returns>True if Vanguard is detected by any method.</returns>
    public bool IsVanguardInstalled()
    {
        try
        {
            // Method 1: Check for running vgc.exe process
            var processes = Process.GetProcessesByName("vgc");
            if (processes.Length > 0)
            {
                foreach (var p in processes) p.Dispose();
                _logger.Information("VbsHvciToggle: Vanguard detected — vgc.exe process running");
                return true;
            }

            // Method 2: Check registry for Riot Vanguard installation
            using var vanguardKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Riot Vanguard");
            if (vanguardKey != null)
            {
                _logger.Information("VbsHvciToggle: Vanguard detected — registry key found");
                return true;
            }

            // Method 3: Check common install path
            if (File.Exists(@"C:\Program Files\Riot Vanguard\vgc.exe"))
            {
                _logger.Information("VbsHvciToggle: Vanguard detected — install path found");
                return true;
            }

            _logger.Debug("VbsHvciToggle: Vanguard not detected");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VbsHvciToggle: Error checking for Vanguard installation");
            return false;
        }
    }

    // ── Re-enable VBS/HVCI ──────────────────────────────────────────────

    /// <summary>
    /// Re-enables both VBS and HVCI by setting their registry values to 1
    /// and restoring the hypervisor launch type to auto.
    /// Changes take effect after reboot. Caller handles the reboot prompt.
    /// </summary>
    /// <returns>True if registry writes succeeded, false on failure.</returns>
    public bool ReEnableVbsHvci()
    {
        try
        {
            _logger.Information("VbsHvciToggle: Re-enabling VBS and HVCI");

            // Enable HVCI (Memory Integrity)
            bool hvciResult = WriteRegistryDword(HvciScenarioPath, HvciValueName, 1);
            if (!hvciResult)
            {
                _logger.Error("VbsHvciToggle: Failed to re-enable HVCI");
                return false;
            }

            // Enable VBS
            bool vbsResult = WriteRegistryDword(DeviceGuardPath, VbsValueName, 1);
            if (!vbsResult)
            {
                _logger.Error("VbsHvciToggle: Failed to re-enable VBS");
                return false;
            }

            // Restore hypervisor launch type
            SetHypervisorLaunchType(off: false);

            // Update tracking
            var settings = SettingsManager.Load();
            settings.VbsHvciDisabledByGameShift = false;
            SettingsManager.Save(settings);

            _logger.Information(
                "VbsHvciToggle: VBS and HVCI re-enabled successfully. Reboot required for changes to take effect");

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "VbsHvciToggle: Failed to re-enable VBS/HVCI");
            return false;
        }
    }

    // ── Hypervisor launch type (bcdedit) ─────────────────────────────────

    /// <summary>
    /// Sets the hypervisor launch type via bcdedit. This controls whether the
    /// Windows hypervisor loads at boot. Without disabling this, VBS can remain
    /// active even when registry values are set to 0 (common on OEM systems with
    /// UEFI-level VBS enforcement or Group Policy).
    /// </summary>
    /// <param name="off">True to set hypervisorlaunchtype=off, false to restore auto.</param>
    private void SetHypervisorLaunchType(bool off)
    {
        try
        {
            var launchType = off ? "off" : "auto";
            var psi = new ProcessStartInfo
            {
                FileName = NativeInterop.SystemExePath("bcdedit.exe"),
                Arguments = $"/set hypervisorlaunchtype {launchType}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            // Read stdout and stderr concurrently to avoid pipe deadlock
            string stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var stdout = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(10_000);

            if (!process.WaitForExit(10_000))
            {
                _logger.Warning("VbsHvciToggle: bcdedit timed out after 10 seconds");
                try { process.Kill(); } catch { }
                return;
            }

            if (process.ExitCode == 0)
            {
                _logger.Information(
                    "VbsHvciToggle: Set hypervisorlaunchtype={LaunchType}",
                    launchType);
            }
            else
            {
                _logger.Warning(
                    "VbsHvciToggle: bcdedit exited with code {ExitCode}: {StdOut} {StdErr}",
                    process.ExitCode, stdout.Trim(), stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "VbsHvciToggle: Failed to set hypervisor launch type");
        }
    }

    // ── Registry helpers ────────────────────────────────────────────────

    /// <summary>
    /// Reads a DWORD value from HKLM registry. Returns -1 if not found or on error.
    /// </summary>
    private int ReadRegistryDword(string subKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (key == null)
            {
                _logger.Debug(
                    "VbsHvciToggle: Registry key not found: HKLM\\{Path}",
                    subKeyPath);
                return -1;
            }

            var value = key.GetValue(valueName);
            if (value == null)
            {
                _logger.Debug(
                    "VbsHvciToggle: Registry value not found: HKLM\\{Path}\\{Name}",
                    subKeyPath, valueName);
                return -1;
            }

            int result = (int)value;
            _logger.Debug(
                "VbsHvciToggle: Read HKLM\\{Path}\\{Name} = {Value}",
                subKeyPath, valueName, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "VbsHvciToggle: Failed to read HKLM\\{Path}\\{Name}",
                subKeyPath, valueName);
            return -1;
        }
    }

    /// <summary>
    /// Writes a DWORD value to HKLM registry. Logs old and new values.
    /// </summary>
    /// <returns>True if the write succeeded.</returns>
    private bool WriteRegistryDword(string subKeyPath, string valueName, int newValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true);
            if (key == null)
            {
                _logger.Error(
                    "VbsHvciToggle: Cannot open registry key for writing: HKLM\\{Path}",
                    subKeyPath);
                return false;
            }

            // Log current value before changing
            var oldValue = key.GetValue(valueName);
            string oldDisplay = oldValue != null ? oldValue.ToString()! : "<not set>";

            key.SetValue(valueName, newValue, RegistryValueKind.DWord);

            _logger.Information(
                "VbsHvciToggle: Set HKLM\\{Path}\\{Name} = {NewValue} (was: {OldValue})",
                subKeyPath, valueName, newValue, oldDisplay);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "VbsHvciToggle: Failed to write HKLM\\{Path}\\{Name} = {Value}",
                subKeyPath, valueName, newValue);
            return false;
        }
    }
}
