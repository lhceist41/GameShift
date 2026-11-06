namespace GameShift.Core.Config;

/// <summary>
/// Application settings model matching PRD specifications.
/// Persisted to %AppData%/GameShift/settings.json via SettingsManager.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Whether GameShift should start automatically with Windows.
    /// </summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// Whether to start minimized to system tray.
    /// </summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// Whether to show desktop notifications for state changes.
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// Whether to enable Serilog structured logging.
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    /// Minimum log level for Serilog (Verbose, Debug, Information, Warning, Error, Fatal).
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    // -- v2 VBS/HVCI Advisory fields ----------------------------------------

    /// <summary>
    /// Whether GameShift disabled VBS/HVCI (tracks our responsibility).
    /// </summary>
    public bool VbsHvciDisabledByGameShift { get; set; } = false;

    /// <summary>
    /// Whether RequirePlatformSecurityFeatures existed before GameShift cleared it, so re-enable can
    /// restore the original value instead of deleting it (deleting weakens enforcement on machines
    /// where an OEM/MDM/policy set it directly in the Control hive).
    /// </summary>
    public bool VbsHvciOriginalRpsfExisted { get; set; } = false;

    /// <summary>
    /// The original RequirePlatformSecurityFeatures DWORD captured before disable. Valid only when
    /// <see cref="VbsHvciOriginalRpsfExisted"/> is true.
    /// </summary>
    public int VbsHvciOriginalRpsfValue { get; set; } = 0;

    /// <summary>
    /// When GameShift last turned VBS/HVCI off, so a later start can tell whether Windows has
    /// restarted since. Null when there is no such record.
    /// </summary>
    public DateTime? VbsHvciDisabledAtUtc { get; set; } = null;

    /// <summary>
    /// Whether user dismissed the VBS/HVCI performance warning banner.
    /// </summary>
    public bool VbsHvciNotificationDismissed { get; set; } = false;

    /// <summary>
    /// Whether the user dismissed the advisory offering to remove leftover AMD platform-timer BCD
    /// tweaks (disabledynamictick / useplatformtick) applied by an older GameShift version.
    /// </summary>
    public bool AmdPlatformTimerAdvisoryDismissed { get; set; } = false;

    /// <summary>
    /// Last time VBS/HVCI state was checked. Null if never checked.
    /// </summary>
    public DateTime? VbsHvciLastChecked { get; set; } = null;

    // -- v2 DPC Monitoring fields ------------------------------------------------

    /// <summary>
    /// Global default DPC load threshold in the stored DpcLoad scale (percent x 10000;
    /// 1000 = 0.1% of CPU time). Per-game overrides in GameProfile.DpcThresholdMicroseconds.
    /// </summary>
    public int DefaultDpcThresholdMicroseconds { get; set; } = 1000;

    /// <summary>
    /// Whether user dismissed the DPC spike alert banner on the dashboard.
    /// Persisted across sessions. Reset when a new DPC spike occurs (optional).
    /// </summary>
    public bool DpcSpikeAlertDismissed { get; set; } = false;

    // -- v2 Game Preset fields ------------------------------------------------

    /// <summary>
    /// Tracks which one-time tips have been shown (tip IDs like "valorant_xmp", "lol_dx11", etc.).
    /// Persisted in settings.json. Once a tip ID is added, that tip never shows again.
    /// </summary>
    public HashSet<string> DismissedTips { get; set; } = new();

    // -- v2.1 Notification Preferences ----------------------------------------

    /// <summary>
    /// Whether to show a toast when a game is detected and optimizations activate.
    /// Only checked when ShowNotifications (master toggle) is true.
    /// </summary>
    public bool ShowGameDetectedToast { get; set; } = true;

    /// <summary>
    /// Whether to show post-session summary toast after a game exits.
    /// Only checked when ShowNotifications (master toggle) is true.
    /// </summary>
    public bool ShowSessionSummaryToast { get; set; } = true;

    /// <summary>
    /// Whether to show DPC latency spike alert toasts.
    /// Only checked when ShowNotifications (master toggle) is true.
    /// </summary>
    public bool ShowDpcAlertToast { get; set; } = true;

    /// <summary>
    /// Whether to suppress all non-critical notifications during active gaming sessions.
    /// When true and a game is running, no toasts are shown (post-session toast fires after exit).
    /// </summary>
    public bool SuppressNotificationsDuringGaming { get; set; } = true;

    // -- v2.1 Convenience Features ----------------------------------------

    /// <summary>
    /// Global hotkey binding string. Format: modifier keys + key separated by +.
    /// Supported modifiers: Ctrl, Shift, Alt. Example: "Ctrl+Shift+G".
    /// Pressing the hotkey toggles optimization pause state system-wide.
    /// </summary>
    public string GlobalHotkeyBinding { get; set; } = "Ctrl+Shift+G";

    // -- v2.3 Network Monitoring ----------------------------------------

    /// <summary>
    /// Target IP address or hostname for the network ping monitor.
    /// Default: Google DNS (8.8.8.8). Configurable in Settings > Network.
    /// </summary>
    public string PingTarget { get; set; } = "8.8.8.8";

    // -- DPC Doctor --------------------------------------------------------

    /// <summary>
    /// Whether DPC Doctor shows Simple Mode (true) or Technical Mode (false).
    /// Persisted immediately on toggle.
    /// </summary>
    public bool DpcDoctorSimpleMode { get; set; } = true;

    /// <summary>
    /// The removed Easy Mode switch. Nothing reads it; it stays so settings files that carry it keep their value.
    /// </summary>
    public bool AdvancedMode { get; set; } = false;

    /// <summary>
    /// Colour theme: follow Windows (the default) or always dark or light. Windows high contrast
    /// overrides any choice. Settings files without it load as <see cref="AppTheme.System"/>.
    /// </summary>
    public AppTheme AppTheme { get; set; } = AppTheme.System;

    /// <summary>
    /// DPC fixes that have been applied, with their previous values for rollback.
    /// Persisted so fixes can be reverted even after app restart or crash.
    /// </summary>
    public List<AppliedDpcFix> AppliedDpcFixes { get; set; } = new();

    /// <summary>
    /// Fix IDs awaiting a system reboot to take effect.
    /// Cleared after reboot when DPC Doctor runs a comparison scan.
    /// </summary>
    public List<string> PendingRebootFixes { get; set; } = new();

    /// <summary>
    /// Per-driver peak DPC latency values from the last trace run (driver filename → peak µs).
    /// Used for post-reboot before/after comparison.
    /// </summary>
    public Dictionary<string, double> DpcDoctorLastRunPeaks { get; set; } = new();

    // -- Background Mode ------------------------------------------------

    /// <summary>
    /// Background Mode settings as earlier versions saved them. Background Mode is gone; these are
    /// kept only so the one-time cleanup can tell whether it was on.
    /// Null means Background Mode was never configured.
    /// </summary>
    public BackgroundModeSettings? BackgroundMode { get; set; }

    /// <summary>Set once the one-time Background Mode cleanup has run.</summary>
    public bool BackgroundModeRetired { get; set; }

    // -- System Tweaks ------------------------------------------------

    /// <summary>
    /// System Tweaks settings for one-time registry optimizations.
    /// Null means never configured.
    /// </summary>
    public SystemTweaksSettings? SystemTweaks { get; set; }

    // -- Window Position Persistence ----------------------------------------

    /// <summary>Window left position in device-independent pixels.</summary>
    public double WindowLeft { get; set; }

    /// <summary>Window top position in device-independent pixels.</summary>
    public double WindowTop { get; set; }

    /// <summary>Window width in device-independent pixels.</summary>
    public double WindowWidth { get; set; }

    /// <summary>Window height in device-independent pixels.</summary>
    public double WindowHeight { get; set; }

    /// <summary>Whether the window was maximized when last closed.</summary>
    public bool WindowMaximized { get; set; }

    // -- Update Preferences ------------------------------------------------

    /// <summary>
    /// Version string the user chose to skip (e.g., "2.2.0").
    /// Startup update popup will not show for this version.
    /// Reset to empty when a newer version is released.
    /// </summary>
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    /// <summary>
    /// True once a portable copy has shown the one-time recommendation to switch to the installer.
    /// </summary>
    public bool InstallRecommendationShown { get; set; }

    /// <summary>
    /// True once a portable copy with nothing installed has offered to install GameShift. Separate from
    /// <see cref="InstallRecommendationShown"/>, which 3.9.0 already set when it only linked to the download.
    /// </summary>
    public bool InstallOfferShown { get; set; }

    /// <summary>
    /// The portable exe that started the installer to install GameShift, so the installed copy can say
    /// once that it may be deleted. Null when unknown or already mentioned. Display only: user-writable.
    /// </summary>
    public string? MigratedPortableExePath { get; set; }
}

/// <summary>
/// Tracks an applied DPC fix with enough info to revert it.
/// </summary>
public class AppliedDpcFix
{
    /// <summary>Unique identifier for this fix (e.g., "msi_mode_nvidia", "disable_dynamic_tick").</summary>
    public string FixId { get; set; } = "";

    /// <summary>Human-readable description of the fix.</summary>
    public string Description { get; set; } = "";

    /// <summary>The fix action type (RegistrySet, BcdEdit, NetshCommand, PowerPlanSetting, SetNetAdapterProperty).</summary>
    public string ActionType { get; set; } = "";

    /// <summary>Previous value before the fix was applied. Null if key/setting did not exist.</summary>
    public string? PreviousValue { get; set; }

    /// <summary>Registry path, bcdedit identifier, or other target for rollback.</summary>
    public string Target { get; set; } = "";

    /// <summary>When the fix was applied.</summary>
    public DateTime AppliedAt { get; set; }

    /// <summary>Whether this fix requires a reboot to take effect.</summary>
    public bool RequiresReboot { get; set; }
}

/// <summary>
/// Settings saved by earlier versions for Background Mode, which has been removed. Kept so
/// files from those versions keep loading and the one-time cleanup can read them.
/// </summary>
public class BackgroundModeSettings
{
    /// <summary>Master toggle for all Background Mode services.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Enable always-on standby list cleaning.</summary>
    public bool StandbyListCleanerEnabled { get; set; } = true;
    /// <summary>
    /// Standby list size threshold in MB. Purge is triggered only when standby exceeds this
    /// AND free memory drops below StandbyListFreeMemoryMinMB simultaneously.
    /// 0 = auto-scale based on total RAM.
    /// </summary>
    public int StandbyListStandbyThresholdMB { get; set; } = 0;
    /// <summary>
    /// Minimum free physical memory in MB. Purge is triggered only when free RAM drops below
    /// this AND standby list exceeds StandbyListStandbyThresholdMB simultaneously.
    /// 0 = auto-scale based on total RAM.
    /// </summary>
    public int StandbyListFreeMemoryMinMB { get; set; } = 0;
    /// <summary>Poll interval in milliseconds for memory checks. Default 1000ms.</summary>
    public int StandbyListPollIntervalMs { get; set; } = 1000;
    /// <summary>When true, standby list purging only occurs during active gaming sessions.</summary>
    public bool StandbyListOnlyDuringGaming { get; set; } = true;

    /// <summary>Enable always-on timer resolution lock.</summary>
    public bool TimerResolutionEnabled { get; set; } = true;
    /// <summary>Target timer resolution in 100ns units. 5000 = 0.5ms.</summary>
    public int TimerResolution100ns { get; set; } = 5000;

    /// <summary>Enable always-on power plan management.</summary>
    public bool PowerPlanEnabled { get; set; } = true;
    /// <summary>Minutes of user idle before switching to balanced plan. 0 = never switch.</summary>
    public int IdleTimeoutMinutes { get; set; } = 15;

    /// <summary>Enable Windows scheduled task deferral during gaming.</summary>
    public bool TaskDeferralEnabled { get; set; } = true;

    /// <summary>
    /// Enable dynamic background CPU restraint during gaming sessions.
    /// Demotes background processes that exceed 15% CPU for 3 consecutive samples
    /// to BelowNormal priority, restores them when they drop below threshold.
    /// </summary>
    public bool ProBalanceEnabled { get; set; } = true;

    /// <summary>Enable persistent process priority rules.</summary>
    public bool ProcessPriorityEnabled { get; set; } = false;
    /// <summary>
    /// Process rules: executable name → priority class name (e.g., "chrome.exe" → "BelowNormal").
    /// </summary>
    public Dictionary<string, string> ProcessPriorityRules { get; set; } = new();
}

/// <summary>
/// Settings for System Tweaks - one-time registry and system-level optimizations.
/// </summary>
public class SystemTweaksSettings
{
    /// <summary>
    /// Tracks which tweaks GameShift has applied, with enough info to revert.
    /// Key = tweak class name (e.g., "DisableGameDvr").
    /// </summary>
    public Dictionary<string, TweakState> AppliedTweaks { get; set; } = new();
}

/// <summary>
/// Tracks the state of an applied system tweak for safe revert.
/// </summary>
public class TweakState
{
    /// <summary>Whether this tweak was applied by GameShift (vs. already applied by user).</summary>
    public bool IsAppliedByGameShift { get; set; }

    /// <summary>JSON-serialized original values before GameShift applied the tweak. Null if not applied by GameShift.</summary>
    public string? OriginalValues { get; set; }

    /// <summary>When the tweak was applied.</summary>
    public DateTime AppliedAt { get; set; }
}
