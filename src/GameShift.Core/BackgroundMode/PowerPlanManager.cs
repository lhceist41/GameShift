using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.System;
using Timer = System.Threading.Timer;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// Always-on power plan manager. Creates a custom "GameShift Performance" plan
/// (cloned from Ultimate Performance with aggressive overrides) and keeps it active.
/// Monitors user idle time and switches to Balanced when idle to save power.
/// 3-state: Gaming (during game session) → Desktop (GameShift Performance) → Idle (Balanced).
/// </summary>
public class PowerPlanManager : IDisposable
{
    private static readonly Guid UltimatePerformanceGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private Timer? _idleTimer;
    private volatile bool _running;
    private int _idleTimeoutMinutes;
    private Guid _originalPlanGuid;
    private Guid _customPlanGuid;
    private bool _isIdle;

    public bool IsRunning => _running;

    /// <summary>Whether the user is currently idle (Balanced plan active).</summary>
    public bool IsIdle => _isIdle;

    /// <summary>The GUID of the active custom GameShift Performance plan.</summary>
    public Guid CustomPlanGuid => _customPlanGuid;

    /// <summary>
    /// Starts the power plan manager. Creates the custom plan if needed,
    /// activates it, and starts idle monitoring.
    /// </summary>
    public void Start(BackgroundModeSettings settings)
    {
        if (_running) return;

        try
        {
            _idleTimeoutMinutes = settings.IdleTimeoutMinutes;

            // Save current plan as fallback for full stop
            _originalPlanGuid = GetActivePlanGuid();

            // Create or find our custom plan
            _customPlanGuid = FindOrCreateCustomPlan();
            if (_customPlanGuid == Guid.Empty)
            {
                SettingsManager.Logger.Error("[PowerPlanManager] Failed to create custom plan");
                return;
            }

            // Activate custom plan
            var guid = _customPlanGuid;
            uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref guid);
            if (result != 0)
            {
                SettingsManager.Logger.Error("[PowerPlanManager] Failed to activate custom plan, error {Error}", result);
                return;
            }

            _running = true;
            _isIdle = false;

            // Start idle monitoring if timeout is set
            if (_idleTimeoutMinutes > 0)
            {
                _idleTimer = new Timer(_ => CheckIdle(), null,
                    TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }

            SettingsManager.Logger.Information(
                "[PowerPlanManager] Started with custom plan {Guid}, idle timeout {Minutes}min",
                _customPlanGuid, _idleTimeoutMinutes);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanManager] Failed to start");
        }
    }

    /// <summary>
    /// Stops the power plan manager and restores the original power plan.
    /// </summary>
    public void Stop()
    {
        _running = false;

        if (_idleTimer != null)
        {
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _idleTimer.Dispose();
            _idleTimer = null;
        }

        // Restore original plan
        if (_originalPlanGuid != Guid.Empty)
        {
            var guid = _originalPlanGuid;
            NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref guid);
            SettingsManager.Logger.Information("[PowerPlanManager] Restored original plan {Guid}", _originalPlanGuid);
        }

        _isIdle = false;
        SettingsManager.Logger.Information("[PowerPlanManager] Stopped");
    }

    /// <summary>
    /// Called by BackgroundModeService when a game starts.
    /// Switches to the Gaming state (custom plan, cancel idle).
    /// </summary>
    public void OnGamingStart()
    {
        if (!_running) return;

        if (_isIdle)
        {
            // Switch back from Balanced to custom plan
            var guid = _customPlanGuid;
            NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref guid);
            _isIdle = false;
            SettingsManager.Logger.Information("[PowerPlanManager] Switched to Gaming state (custom plan)");
        }
    }

    /// <summary>
    /// Called by BackgroundModeService when a game stops.
    /// Returns to Desktop state (custom plan still active, idle timer resumes).
    /// </summary>
    public void OnGamingStop()
    {
        // Nothing to do — custom plan stays active, idle timer continues
        SettingsManager.Logger.Debug("[PowerPlanManager] Returned to Desktop state");
    }

    /// <summary>
    /// Gets the GUID of the currently active plan for snapshot recording.
    /// </summary>
    public Guid GetActivePlanGuid()
    {
        try
        {
            uint result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptrGuid);
            if (result == 0 && ptrGuid != IntPtr.Zero)
            {
                Guid guid = Marshal.PtrToStructure<Guid>(ptrGuid);
                LocalFree(ptrGuid);
                return guid;
            }
        }
        catch { }
        return Guid.Empty;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private void CheckIdle()
    {
        if (!_running || _idleTimeoutMinutes <= 0) return;

        try
        {
            var lastInput = new NativeInterop.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeInterop.LASTINPUTINFO>()
            };

            if (!NativeInterop.GetLastInputInfo(ref lastInput)) return;

            uint idleMs = (uint)Environment.TickCount - lastInput.dwTime;
            double idleMinutes = idleMs / 60000.0;

            if (!_isIdle && idleMinutes >= _idleTimeoutMinutes)
            {
                // Switch to Balanced (fall back to original plan if Balanced doesn't exist)
                var balanced = BalancedGuid;
                uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref balanced);
                if (result == 0)
                {
                    _isIdle = true;
                    SettingsManager.Logger.Information(
                        "[PowerPlanManager] User idle {Minutes:F0}min, switched to Balanced",
                        idleMinutes);
                }
                else if (_originalPlanGuid != Guid.Empty)
                {
                    var orig = _originalPlanGuid;
                    if (NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref orig) == 0)
                    {
                        _isIdle = true;
                        SettingsManager.Logger.Warning(
                            "[PowerPlanManager] Balanced plan not available, fell back to original plan for idle");
                    }
                }
            }
            else if (_isIdle && idleMinutes < 1)
            {
                // User active again — switch back to custom plan
                var guid = _customPlanGuid;
                uint result = NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref guid);
                if (result == 0)
                {
                    _isIdle = false;
                    SettingsManager.Logger.Information("[PowerPlanManager] User active, switched back to custom plan");
                }
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanManager] Idle check error");
        }
    }

    /// <summary>
    /// Finds or creates the "GameShift Performance" custom power plan.
    /// Cloned from Ultimate Performance with aggressive overrides via powercfg.
    /// </summary>
    private Guid FindOrCreateCustomPlan()
    {
        try
        {
            // Delete any existing "GameShift Performance" plans so we always
            // start fresh with the latest overrides on each app launch.
            DeleteExistingCustomPlans();

            // Create by duplicating Ultimate Performance (fall back to High Performance)
            var dupOutput = RunPowercfg($"/duplicatescheme {UltimatePerformanceGuid}");
            if (dupOutput == null)
            {
                SettingsManager.Logger.Warning(
                    "[PowerPlanManager] Ultimate Performance template unavailable, trying High Performance");
                dupOutput = RunPowercfg($"/duplicatescheme {HighPerformanceGuid}");
            }
            if (dupOutput == null)
            {
                SettingsManager.Logger.Error("[PowerPlanManager] Failed to duplicate any performance plan");
                return Guid.Empty;
            }

            // Parse new GUID from output (format: "Power Scheme GUID: <guid>  (...)")
            Guid newGuid = Guid.Empty;
            foreach (var line in dupOutput.Split('\n'))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var afterColon = line[(colonIdx + 1)..].Trim();
                    var spaceIdx = afterColon.IndexOf(' ');
                    var candidate = spaceIdx > 0 ? afterColon[..spaceIdx] : afterColon;
                    if (Guid.TryParse(candidate.Trim(), out newGuid))
                        break;
                }
            }

            if (newGuid == Guid.Empty)
            {
                SettingsManager.Logger.Error("[PowerPlanManager] Could not parse GUID from powercfg output");
                return Guid.Empty;
            }

            // Rename the plan
            RunPowercfg($"/changename {newGuid} \"GameShift Performance\" \"Custom performance plan managed by GameShift\"");

            // Apply aggressive overrides
            ApplyPowerOverrides(newGuid);

            SettingsManager.Logger.Information("[PowerPlanManager] Created custom plan {Guid}", newGuid);
            return newGuid;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[PowerPlanManager] Failed to create custom plan");
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Deletes all existing "GameShift Performance" power plans.
    /// Called before creating a new one to ensure users always get fresh overrides
    /// when the app updates. Also cleans up duplicates from previous versions.
    /// If the plan being deleted is currently active, switches to the original plan first.
    /// </summary>
    private void DeleteExistingCustomPlans()
    {
        var listOutput = RunPowercfg("/list");
        if (listOutput == null) return;

        foreach (var line in listOutput.Split('\n'))
        {
            if (!line.Contains("GameShift Performance", StringComparison.OrdinalIgnoreCase))
                continue;

            // Extract GUID
            var parts = line.Split(' ');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length >= 36 && trimmed.Contains('-') &&
                    Guid.TryParse(trimmed[..36], out var oldGuid))
                {
                    // If this plan is currently active, switch away first
                    if (line.Contains('*'))
                    {
                        var orig = _originalPlanGuid != Guid.Empty ? _originalPlanGuid : BalancedGuid;
                        NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref orig);
                    }

                    RunPowercfg($"/delete {oldGuid}");
                    SettingsManager.Logger.Information(
                        "[PowerPlanManager] Deleted old custom plan {Guid}", oldGuid);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Applies all performance overrides to the custom plan via powercfg.
    /// Includes the original 6 core overrides plus 50+ expanded overrides from PowerPlanConfigurator
    /// covering processor tuning, storage, USB, wireless, idle resiliency, interrupt steering,
    /// global device policy, and vendor-aware heterogeneous scheduling.
    /// Sets both AC (plugged in) and DC (battery) values for laptop support.
    /// </summary>
    private void ApplyPowerOverrides(Guid planGuid)
    {
        var plan = planGuid.ToString();

        // === Original 6 core overrides (kept for clarity) ===

        // Processor performance boost mode = Efficient Aggressive (4)
        // Better sustained performance than Aggressive (2) by respecting thermal/power limits
        RunPowercfg($"/setacvalueindex {plan} 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7 4");
        RunPowercfg($"/setdcvalueindex {plan} 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7 4");

        // Minimum processor state = 100%
        RunPowercfg($"/setacvalueindex {plan} 54533251-82be-4824-96c1-47b60b740d00 893dee8e-2bef-41e0-89c6-b55d0929964c 100");
        RunPowercfg($"/setdcvalueindex {plan} 54533251-82be-4824-96c1-47b60b740d00 893dee8e-2bef-41e0-89c6-b55d0929964c 100");

        // Maximum processor state = 100%
        RunPowercfg($"/setacvalueindex {plan} 54533251-82be-4824-96c1-47b60b740d00 bc5038f7-23e0-4960-96da-33abaf5935ec 100");
        RunPowercfg($"/setdcvalueindex {plan} 54533251-82be-4824-96c1-47b60b740d00 bc5038f7-23e0-4960-96da-33abaf5935ec 100");

        // USB selective suspend = Disabled
        RunPowercfg($"/setacvalueindex {plan} 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
        RunPowercfg($"/setdcvalueindex {plan} 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");

        // PCI Express Link State Power Management = Off
        RunPowercfg($"/setacvalueindex {plan} 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0");
        RunPowercfg($"/setdcvalueindex {plan} 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0");

        // Hard disk turn off timeout = 0 (never)
        RunPowercfg($"/setacvalueindex {plan} 0012ee47-9041-4b5d-9b77-535fba8b1442 6738e2c4-e8a5-4a42-b16a-e040e769756e 0");
        RunPowercfg($"/setdcvalueindex {plan} 0012ee47-9041-4b5d-9b77-535fba8b1442 6738e2c4-e8a5-4a42-b16a-e040e769756e 0");

        // === Expanded overrides from PowerPlanConfigurator ===

        var cpuProfile = PowerPlanConfigurator.DetectCpuProfile();
        bool hasIntelGpu = PowerPlanConfigurator.DetectHasIntelGpu();

        SettingsManager.Logger.Information(
            "[PowerPlanManager] CPU profile: {Profile}, Intel GPU: {IntelGpu}",
            cpuProfile, hasIntelGpu);

        var configurator = new PowerPlanConfigurator();
        var overrides = configurator.GetPlanOverrides(cpuProfile, hasIntelGpu);

        int applied = 0;
        int skipped = 0;

        foreach (var o in overrides)
        {
            // Apply AC value
            var acResult = RunPowercfg($"/setacvalueindex {plan} {o.SubGroupGuid} {o.SettingGuid} {o.Value}");

            // Apply DC value (for laptop support)
            RunPowercfg($"/setdcvalueindex {plan} {o.SubGroupGuid} {o.SettingGuid} {o.Value}");

            if (acResult != null)
            {
                applied++;
            }
            else
            {
                skipped++;
                SettingsManager.Logger.Debug(
                    "[PowerPlanManager] Setting not available: {Description} ({Guid})",
                    o.Description ?? "unknown", o.SettingGuid);
            }
        }

        SettingsManager.Logger.Information(
            "[PowerPlanManager] Applied {Applied} expanded overrides, {Skipped} skipped (not available on this hardware)",
            applied, skipped);
    }

    private static string? RunPowercfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            string stderr = "";
            var stderrTask = Task.Run(() => { stderr = process.StandardError.ReadToEnd(); });
            var output = process.StandardOutput.ReadToEnd();
            stderrTask.Wait(10_000);
            process.WaitForExit(10_000);
            return output;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[PowerPlanManager] powercfg failed: {Args}", arguments);
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
