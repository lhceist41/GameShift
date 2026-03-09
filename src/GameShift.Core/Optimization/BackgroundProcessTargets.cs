namespace GameShift.Core.Optimization;

/// <summary>
/// Shared configuration for which background processes GameShift manages during gaming sessions.
/// Used by IoPriorityManager, EfficiencyModeController, and MemoryOptimizer.
/// Centralizes process targeting to ensure all three modules agree on what to demote.
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
}
