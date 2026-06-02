using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// Shared configuration for which background processes GameShift manages during gaming sessions.
/// Used by IoPriorityManager, EfficiencyModeController, MemoryOptimizer and CpuSchedulingOptimizer.
/// Centralizes process targeting to ensure all modules agree on what to demote.
///
/// The demotions these modules apply (memory priority, I/O priority, EcoQoS, CPU-set affinity) are
/// volatile - they do not survive a process exit or reboot - so there is no per-PID state to journal.
/// But if GameShift crashes mid-session a long-lived background process can stay demoted until it
/// restarts. <see cref="ResetDemotedProcessesToDefaults"/> handles that during crash recovery by
/// re-resolving the targets by NAME and writing OS defaults.
/// </summary>
public static class BackgroundProcessTargets
{
    /// <summary>
    /// Processes that should be demoted (lower I/O priority, Efficiency Mode, memory priority)
    /// during gaming. These are known resource-heavy background processes.
    /// </summary>
    public static readonly HashSet<string> AlwaysDemote = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows indexing and search
        "SearchIndexer",
        "SearchProtocolHost",
        "SearchFilterHost",

        // Windows telemetry and diagnostics
        "WmiPrvSE",
        "CompatTelRunner",
        "SgrmBroker",
        "DiagTrackRunner",

        // Windows Defender (scan engine, not the service)
        "MsMpEng",
        "MpCmdRun",
        "SecurityHealthService",
        "NisSrv",

        // Windows Update
        "TiWorker",
        "TrustedInstaller",
        "WaasMedic",
        "UsoClient",
        "musNotification",
        "MusNotifyIcon",

        // Microsoft background apps
        "OneDrive",
        "Teams",
        "Outlook",
        "msedge",
        "PhoneExperienceHost",
        "YourPhone",
        "HxTsr",
        "Microsoft.SharePoint",

        // Game launcher helpers (not the launchers themselves)
        "EpicWebHelper",
        "steamwebhelper",
        "BattleNetHelper",
        "OriginWebHelperService",
        "EABackgroundService",

        // Browser background processes (if user has browser open)
        "chrome",
        "firefox",
        "brave",
        "opera",
    };

    /// <summary>
    /// Processes that must NEVER be touched under any circumstances.
    /// Includes critical system processes, anti-cheat, DWM, and GameShift itself.
    /// </summary>
    public static readonly HashSet<string> NeverDemote = new(StringComparer.OrdinalIgnoreCase)
    {
        // Critical system processes (touching these = BSOD or hang)
        "System",
        "Registry",
        "csrss",
        "lsass",
        "smss",
        "services",
        "svchost",
        "wininit",
        "winlogon",
        "dwm",
        "fontdrvhost",
        "dasHost",
        "LsaIso",
        "Memory Compression",

        // Shell and user session (breaking these = desktop hangs)
        "sihost",
        "explorer",
        "RuntimeBroker",
        "dllhost",
        "conhost",
        "taskhostw",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "TextInputHost",
        "ctfmon",

        // Audio (breaking this = no game audio)
        "audiodg",
        "AudioSrv",

        // Graphics (breaking these = display issues)
        "igfxCUIService",
        "igfxEM",
        "NVDisplay.Container",

        // Anti-cheat (from Sprint 1 AntiCheatDetector)
        "vgc",
        "vgtray",
        "vgk",
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "BEService",
        "BEDaisy",
        "FACEITService",
        "faceit",

        // GameShift itself
        "GameShift",
    };

    /// <summary>
    /// Check if a process should be demoted during gaming.
    /// Returns true only if process is in AlwaysDemote AND NOT in NeverDemote
    /// AND NOT the active game process.
    /// </summary>
    public static bool ShouldDemote(string processName, IEnumerable<string> activeGameProcessNames)
    {
        if (NeverDemote.Contains(processName)) return false;
        if (activeGameProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return false;
        return AlwaysDemote.Contains(processName);
    }

    /// <summary>
    /// Crash recovery: resets every live process in <see cref="AlwaysDemote"/> (and not in
    /// <see cref="NeverDemote"/>) back to OS-default memory priority (Normal), I/O priority (Normal),
    /// power throttling (EcoQoS cleared) and CPU-set affinity (cleared). Re-resolves by NAME because
    /// the original PIDs are gone after a crash. Idempotent and safe - writing the defaults to an
    /// already-default process is a no-op. Called from the watchdog and the startup crash handler.
    /// </summary>
    /// <returns>The number of processes successfully reset.</returns>
    public static int ResetDemotedProcessesToDefaults()
    {
        int reset = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!AlwaysDemote.Contains(proc.ProcessName) || NeverDemote.Contains(proc.ProcessName))
                    continue;

                if (ResetProcessToDefaults(proc.Id))
                    reset++;
            }
            catch { /* process may have exited between enumeration and open - ignore */ }
            finally { proc.Dispose(); }
        }

        if (reset > 0)
            SettingsManager.Logger.Information(
                "[BackgroundProcessTargets] Crash recovery: reset {Count} background process(es) to default priorities",
                reset);

        return reset;
    }

    private static bool ResetProcessToDefaults(int pid)
    {
        IntPtr h = NativeInterop.OpenProcess(
            NativeInterop.PROCESS_SET_INFORMATION | NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION,
            false, pid);
        if (h == IntPtr.Zero) return false;

        try
        {
            // Memory priority → Normal (5)
            SetProcessStruct(h, NativeInterop.ProcessMemoryPriority,
                new NativeInterop.MEMORY_PRIORITY_INFORMATION { MemoryPriority = 5 });

            // Power throttling (EcoQoS) → cleared: ControlMask 0 hands control back to the OS.
            SetProcessStruct(h, NativeInterop.ProcessPowerThrottling,
                new NativeInterop.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = 0,
                    StateMask = 0
                });

            // I/O priority → Normal (2)
            int io = NativeInterop.IoPriorityNormal;
            NativeInterop.NtSetInformationProcess(h, NativeInterop.ProcessIoPriority, ref io, sizeof(int));

            // CPU-set affinity → cleared (threads may run anywhere again). 22H2+ only; ignore failures.
            try { NativeInterop.SetProcessDefaultCpuSetMasks(h, null, 0); } catch { /* pre-22H2 */ }

            return true;
        }
        finally
        {
            NativeInterop.CloseHandle(h);
        }
    }

    private static void SetProcessStruct<T>(IntPtr hProcess, int infoClass, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            NativeInterop.SetProcessInformation(hProcess, infoClass, ptr, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
