using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Suspends overlay processes and kills non-essential GPU consumers during competitive gaming.
///
/// - Suspends overlay processes (Discord, Steam, NVIDIA) via NtSuspendProcess
/// - Kills GPU consumers (Widgets, SearchHost, msedgewebview2)
/// - Writes frame cap hint (monitor refresh rate - 3)
/// - Respects anti-cheat blocklist (never touch vgc, EAC, BattlEye, FACEIT)
/// - Disables/restores Discord overlay via registry
/// - 6-hour safety timeout for auto-resume
/// - Per-profile sub-toggles (SuspendDiscordOverlay, SuspendSteamOverlay, SuspendNvidiaOverlay, KillWidgets)
/// </summary>
public class CompetitiveMode : IOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;
    private bool _isApplied;
    private readonly List<SuspendedProcessInfo> _suspendedProcesses = new();
    private readonly List<string> _killedProcessNames = new();
    private bool _discordOverlayWasEnabled;
    private object? _discordOverlayPreviousValue;
    private global::System.Threading.Timer? _safetyTimer;

    // ── Anti-cheat blocklist — NEVER interact with these processes ──
    private static readonly HashSet<string> AntiCheatBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc.exe", "vgtray.exe",
        "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe",
        "BEService.exe", "BattlEye.exe",
        "FACEITClient.exe"
    };

    // ── Overlay process names to suspend ───────────────────
    private static readonly string[] DiscordOverlayProcessNames = { "Discord" };
    private static readonly string[] SteamOverlayProcessNames = { "GameOverlayUI" };
    private static readonly string[] NvidiaOverlayProcessNames = { "NVIDIA Share", "nvcontainer" };

    // ── GPU consumers to kill ──────────────────────────────
    private static readonly string[] GpuConsumersToKill = { "Widgets", "WidgetService", "SearchHost" };

    // ── Discord overlay registry path ─────────────────────
    private const string DiscordOverlayRegistryPath = @"SOFTWARE\Discord\Modules\discord_overlay2";
    private const string DiscordOverlayEnabledValue = "enabled";

    // ── Frame cap hint path ────────────────────────────────
    private static readonly string FrameCapHintPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameShift", "framecap_hint.json");

    // ── Safety timer — 6 hours in milliseconds ────────────
    private const int SafetyTimeoutMs = 6 * 60 * 60 * 1000; // 21,600,000 ms

    public const string OptimizationId = "Competitive Mode";

    public string Name => OptimizationId;

    public string Description => "Suspends overlays and kills GPU consumers for competitive gaming";

    public bool IsApplied => _isApplied;

    /// <summary>
    /// Competitive Mode is always available — individual sub-toggles control what happens.
    /// </summary>
    public bool IsAvailable => true;

    /// <summary>
    /// Applies competitive mode optimizations:
    /// 1. Check anti-cheat blocklist first
    /// 2. Suspend overlay processes per sub-toggles
    /// 3. Disable Discord overlay via registry
    /// 4. Kill GPU consumers
    /// 5. Write frame cap hint
    /// 6. Start 6-hour safety timer
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            _logger.Information(
                "CompetitiveMode: Applying competitive mode optimizations at {Timestamp}",
                DateTime.UtcNow.ToString("o"));

            // ── Step 1: Log any running anti-cheat processes ──
            WarnAboutRunningAntiCheat();

            // ── Step 2: Suspend overlay processes per sub-toggles ──
            if (profile.SuspendDiscordOverlay)
            {
                SuspendProcessesByName(DiscordOverlayProcessNames, "Discord overlay");

                // Disable Discord overlay via registry
                DisableDiscordOverlayRegistry();
            }

            if (profile.SuspendSteamOverlay)
            {
                SuspendProcessesByName(SteamOverlayProcessNames, "Steam overlay");
            }

            if (profile.SuspendNvidiaOverlay)
            {
                SuspendProcessesByName(NvidiaOverlayProcessNames, "NVIDIA overlay");
            }

            // ── Step 3: Kill GPU consumers ──
            if (profile.KillWidgets)
            {
                KillGpuConsumers();
            }

            // ── Step 4: Write frame cap hint ──
            WriteFrameCapHint();

            // ── Step 5: Start 6-hour safety timer ──
            _safetyTimer = new global::System.Threading.Timer(
                _ => SafetyTimeoutResumeAll(),
                null,
                SafetyTimeoutMs,
                Timeout.Infinite);

            _logger.Information(
                "CompetitiveMode: Safety timeout started — auto-resume in 6 hours");

            _logger.Information(
                "CompetitiveMode: Apply complete — {SuspendedCount} processes suspended, {KilledCount} processes killed",
                _suspendedProcesses.Count,
                _killedProcessNames.Count);

            _isApplied = true;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "CompetitiveMode: Failed to apply competitive mode");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Reverts competitive mode:
    /// 1. Dispose safety timer
    /// 2. Resume all suspended processes
    /// 3. Restore Discord overlay registry
    /// 4. Delete frame cap hint file
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        if (!_isApplied)
        {
            return Task.FromResult(true);
        }

        try
        {
            _logger.Information(
                "CompetitiveMode: Reverting competitive mode at {Timestamp}",
                DateTime.UtcNow.ToString("o"));

            // ── Step 1: Dispose safety timer ──
            _safetyTimer?.Dispose();
            _safetyTimer = null;

            // ── Step 2: Resume all suspended processes ──
            ResumeAllSuspended();

            // ── Step 3: Restore Discord overlay registry ──
            RestoreDiscordOverlayRegistry();

            // ── Step 4: Delete frame cap hint file ──
            DeleteFrameCapHint();

            // Note: Killed processes (Widgets, SearchHost, msedgewebview2) are NOT restarted
            // They restart naturally on their own or on next login
            _killedProcessNames.Clear();

            _logger.Information(
                "CompetitiveMode: Revert complete");

            _isApplied = false;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "CompetitiveMode: Failed to revert competitive mode");
            return Task.FromResult(false);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Helper methods
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if a process name is in the anti-cheat blocklist.
    /// Checks both with and without .exe extension for safety.
    /// </summary>
    private bool IsBlocklisted(string processName)
    {
        if (AntiCheatBlocklist.Contains(processName))
            return true;
        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return AntiCheatBlocklist.Contains(processName + ".exe");
        return false;
    }

    /// <summary>
    /// Logs warnings about any running anti-cheat processes.
    /// Called before any process interaction to ensure awareness.
    /// </summary>
    private void WarnAboutRunningAntiCheat()
    {
        try
        {
            foreach (var acName in AntiCheatBlocklist)
            {
                var nameWithoutExe = Path.GetFileNameWithoutExtension(acName);
                var processes = Process.GetProcessesByName(nameWithoutExe);
                if (processes.Length > 0)
                {
                    _logger.Warning(
                        "CompetitiveMode: Anti-cheat process detected: {ProcessName} (PID: {Pids}) — will NOT be touched",
                        acName,
                        string.Join(", ", processes.Select(p => p.Id)));
                }
                foreach (var p in processes) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(
                ex,
                "CompetitiveMode: Error during anti-cheat detection (non-blocking)");
        }
    }

    /// <summary>
    /// Finds and suspends processes matching the given names.
    /// Skips any process in the anti-cheat blocklist.
    /// Logs each suspension with process name, PID, and timestamp.
    /// </summary>
    private void SuspendProcessesByName(string[] processNames, string category)
    {
        foreach (var name in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                {
                    try
                    {
                        var fullName = process.ProcessName + ".exe";
                        if (IsBlocklisted(fullName))
                        {
                            _logger.Warning(
                                "CompetitiveMode: Skipping blocklisted process {ProcessName} (PID: {ProcessId})",
                                fullName,
                                process.Id);
                            continue;
                        }

                        SuspendProcess(process);
                        _suspendedProcesses.Add(new SuspendedProcessInfo(
                            process.Id, process.ProcessName, DateTime.UtcNow));

                        _logger.Information(
                            "CompetitiveMode: Suspended {Category} process: {ProcessName} (PID: {ProcessId}) at {Timestamp}",
                            category,
                            process.ProcessName,
                            process.Id,
                            DateTime.UtcNow.ToString("o"));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(
                            ex,
                            "CompetitiveMode: Failed to suspend {Category} process {ProcessName} (PID: {ProcessId})",
                            category,
                            process.ProcessName,
                            process.Id);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "CompetitiveMode: Failed to enumerate processes for {ProcessName}",
                    name);
            }
        }
    }

    /// <summary>
    /// Suspends a single process using NtSuspendProcess via NativeInterop.
    /// Process handle is always closed in a finally block.
    /// </summary>
    private void SuspendProcess(Process process)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeInterop.OpenProcess(
                NativeInterop.PROCESS_SUSPEND_RESUME, false, process.Id);

            if (handle == IntPtr.Zero)
            {
                _logger.Warning(
                    "CompetitiveMode: Failed to open process handle for {ProcessName} (PID: {ProcessId})",
                    process.ProcessName,
                    process.Id);
                return;
            }

            int status = NativeInterop.NtSuspendProcess(handle);
            if (status != 0)
            {
                _logger.Warning(
                    "CompetitiveMode: NtSuspendProcess returned non-zero status {Status} for {ProcessName} (PID: {ProcessId})",
                    status,
                    process.ProcessName,
                    process.Id);
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeInterop.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// Resumes a single process by PID using NtResumeProcess via NativeInterop.
    /// Process handle is always closed in a finally block.
    /// </summary>
    private void ResumeProcess(int processId, string processName)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeInterop.OpenProcess(
                NativeInterop.PROCESS_SUSPEND_RESUME, false, processId);

            if (handle == IntPtr.Zero)
            {
                _logger.Warning(
                    "CompetitiveMode: Failed to open process handle for resume — {ProcessName} (PID: {ProcessId}) may have already exited",
                    processName,
                    processId);
                return;
            }

            int status = NativeInterop.NtResumeProcess(handle);
            if (status != 0)
            {
                _logger.Warning(
                    "CompetitiveMode: NtResumeProcess returned non-zero status {Status} for {ProcessName} (PID: {ProcessId})",
                    status,
                    processName,
                    processId);
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeInterop.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// Resumes all suspended processes and clears the tracking list.
    /// Called during normal Revert and by the safety timeout.
    /// </summary>
    private void ResumeAllSuspended()
    {
        foreach (var info in _suspendedProcesses)
        {
            try
            {
                // Verify process still exists before attempting resume
                try
                {
                    Process.GetProcessById(info.ProcessId);
                }
                catch (ArgumentException)
                {
                    _logger.Debug(
                        "CompetitiveMode: Process {ProcessName} (PID: {ProcessId}) already exited, skipping resume",
                        info.ProcessName,
                        info.ProcessId);
                    continue;
                }

                ResumeProcess(info.ProcessId, info.ProcessName);

                _logger.Information(
                    "CompetitiveMode: Resumed process: {ProcessName} (PID: {ProcessId}) at {Timestamp}",
                    info.ProcessName,
                    info.ProcessId,
                    DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "CompetitiveMode: Failed to resume process {ProcessName} (PID: {ProcessId})",
                    info.ProcessName,
                    info.ProcessId);
            }
        }

        _suspendedProcesses.Clear();
    }

    /// <summary>
    /// Kills GPU consumer processes.
    /// Includes Widgets, WidgetService, SearchHost, and non-game msedgewebview2.
    /// Skips blocklisted processes. Logs each kill with PID and timestamp.
    /// </summary>
    private void KillGpuConsumers()
    {
        // Kill standard GPU consumers
        foreach (var name in GpuConsumersToKill)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                {
                    try
                    {
                        var fullName = process.ProcessName + ".exe";
                        if (IsBlocklisted(fullName))
                        {
                            _logger.Warning(
                                "CompetitiveMode: Skipping blocklisted process {ProcessName} (PID: {ProcessId})",
                                fullName,
                                process.Id);
                            continue;
                        }

                        var pid = process.Id;
                        process.Kill();
                        _killedProcessNames.Add(name);

                        _logger.Information(
                            "CompetitiveMode: Killed GPU consumer: {ProcessName} (PID: {ProcessId}) at {Timestamp}",
                            name,
                            pid,
                            DateTime.UtcNow.ToString("o"));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(
                            ex,
                            "CompetitiveMode: Failed to kill GPU consumer {ProcessName} (PID: {ProcessId})",
                            name,
                            process.Id);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    ex,
                    "CompetitiveMode: Failed to enumerate processes for {ProcessName}",
                    name);
            }
        }

        // Kill non-game msedgewebview2 processes
        KillNonGameEdgeWebView();
    }

    /// <summary>
    /// Kills msedgewebview2.exe processes that are NOT part of the game.
    /// Checks the process module path to distinguish game-embedded vs system WebView2.
    /// </summary>
    private void KillNonGameEdgeWebView()
    {
        try
        {
            var processes = Process.GetProcessesByName("msedgewebview2");
            foreach (var process in processes)
            {
                try
                {
                    // Attempt to check if this WebView2 instance is non-game
                    // Skip if we can't determine the path (safer to leave running)
                    string? modulePath = null;
                    try
                    {
                        modulePath = process.MainModule?.FileName;
                    }
                    catch (Exception)
                    {
                        // Access denied or process exited — skip this instance
                        continue;
                    }

                    // If the path contains "Program Files" WebView2 runtime, it's system-level — kill it
                    // If path is null or we can't determine, skip (safety first)
                    if (modulePath == null)
                        continue;

                    // Keep WebView2 instances that might be game-related
                    // System WebView2 is typically in Program Files or Windows paths
                    bool isSystemWebView =
                        modulePath.Contains(@"\Microsoft\EdgeWebView\", StringComparison.OrdinalIgnoreCase) ||
                        modulePath.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase) ||
                        modulePath.Contains(@"Program Files", StringComparison.OrdinalIgnoreCase);

                    if (isSystemWebView)
                    {
                        var pid = process.Id;
                        process.Kill();
                        _killedProcessNames.Add("msedgewebview2");

                        _logger.Information(
                            "CompetitiveMode: Killed non-game msedgewebview2 (PID: {ProcessId}, Path: {Path}) at {Timestamp}",
                            pid,
                            modulePath,
                            DateTime.UtcNow.ToString("o"));
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process already exited
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        ex,
                        "CompetitiveMode: Failed to evaluate/kill msedgewebview2 (PID: {ProcessId})",
                        process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "CompetitiveMode: Failed to enumerate msedgewebview2 processes");
        }
    }

    /// <summary>
    /// Disables Discord overlay via registry.
    /// Stores previous value for restoration on revert.
    /// </summary>
    private void DisableDiscordOverlayRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DiscordOverlayRegistryPath, writable: true);
            if (key == null)
            {
                _logger.Debug(
                    "CompetitiveMode: Discord overlay registry key not found — Discord may not be installed");
                _discordOverlayWasEnabled = false;
                return;
            }

            _discordOverlayPreviousValue = key.GetValue(DiscordOverlayEnabledValue);
            _discordOverlayWasEnabled = _discordOverlayPreviousValue != null;

            key.SetValue(DiscordOverlayEnabledValue, 0, RegistryValueKind.DWord);

            _logger.Information(
                "CompetitiveMode: Disabled Discord overlay via registry (previous value: {PreviousValue})",
                _discordOverlayPreviousValue ?? "<not set>");
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "CompetitiveMode: Failed to disable Discord overlay via registry");
        }
    }

    /// <summary>
    /// Restores Discord overlay registry to its pre-apply state.
    /// </summary>
    private void RestoreDiscordOverlayRegistry()
    {
        try
        {
            if (!_discordOverlayWasEnabled)
            {
                _logger.Debug(
                    "CompetitiveMode: Discord overlay registry was not modified, skipping restore");
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(DiscordOverlayRegistryPath, writable: true);
            if (key == null)
            {
                _logger.Warning(
                    "CompetitiveMode: Discord overlay registry key not found during revert");
                return;
            }

            if (_discordOverlayPreviousValue != null)
            {
                key.SetValue(DiscordOverlayEnabledValue, _discordOverlayPreviousValue, RegistryValueKind.DWord);
                _logger.Information(
                    "CompetitiveMode: Restored Discord overlay registry to {PreviousValue}",
                    _discordOverlayPreviousValue);
            }

            _discordOverlayWasEnabled = false;
            _discordOverlayPreviousValue = null;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "CompetitiveMode: Failed to restore Discord overlay registry");
        }
    }

    /// <summary>
    /// Writes frame cap hint JSON to %AppData%/GameShift/framecap_hint.json.
    /// Frame cap = primary monitor refresh rate - 3.
    /// </summary>
    private void WriteFrameCapHint()
    {
        try
        {
            int refreshRate = GetPrimaryMonitorRefreshRate();
            int frameCap = Math.Max(refreshRate - 3, 30); // Never go below 30 FPS

            var hint = new
            {
                framecap = frameCap,
                monitor_refresh_rate = refreshRate,
                calculated_at = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(hint, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            // Ensure directory exists
            var dir = Path.GetDirectoryName(FrameCapHintPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FrameCapHintPath, json);

            _logger.Information(
                "CompetitiveMode: Frame cap hint written: {FrameCap} ({RefreshRate}Hz monitor)",
                frameCap,
                refreshRate);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "CompetitiveMode: Failed to write frame cap hint");
        }
    }

    /// <summary>
    /// Deletes the frame cap hint file during revert.
    /// </summary>
    private void DeleteFrameCapHint()
    {
        try
        {
            if (File.Exists(FrameCapHintPath))
            {
                File.Delete(FrameCapHintPath);
                _logger.Information(
                    "CompetitiveMode: Deleted frame cap hint file");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "CompetitiveMode: Failed to delete frame cap hint file");
        }
    }

    /// <summary>
    /// Gets the primary monitor refresh rate via EnumDisplaySettingsW P/Invoke.
    /// Returns 60 as a safe default if detection fails.
    /// </summary>
    private int GetPrimaryMonitorRefreshRate()
    {
        try
        {
            var devMode = new DEVMODE();
            devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

            if (EnumDisplaySettingsW(null!, ENUM_CURRENT_SETTINGS, ref devMode))
            {
                if (devMode.dmDisplayFrequency > 0)
                {
                    return (int)devMode.dmDisplayFrequency;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(
                ex,
                "CompetitiveMode: Failed to detect monitor refresh rate, using default 60Hz");
        }

        return 60; // Safe default
    }

    /// <summary>
    /// Safety timeout callback.
    /// Auto-resumes all suspended processes if they've been suspended for more than 6 hours.
    /// Handles the edge case where game detection fails and processes remain suspended.
    /// </summary>
    private void SafetyTimeoutResumeAll()
    {
        try
        {
            _logger.Warning(
                "CompetitiveMode: Safety timeout triggered after 6 hours — resuming all suspended processes");

            ResumeAllSuspended();
            RestoreDiscordOverlayRegistry();
            DeleteFrameCapHint();
            _killedProcessNames.Clear();
            _isApplied = false;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "CompetitiveMode: Error during safety timeout resume");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Helper record
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks a suspended process for later resumption.
    /// </summary>
    private record SuspendedProcessInfo(int ProcessId, string ProcessName, DateTime SuspendedAt);

    // ════════════════════════════════════════════════════════════════════
    // Private P/Invoke declarations for monitor refresh rate detection
    // Module-specific, declared here rather than in NativeInterop (same
    // pattern as MpoToggle for its display enumeration structs).
    // ════════════════════════════════════════════════════════════════════

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        // Position union (POINTL or display orientation fields)
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;

        // ICM fields (not used but needed for correct struct size)
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
