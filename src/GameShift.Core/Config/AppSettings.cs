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

    /// <summary>
    /// Memory threshold in MB before considering a process as a game.
    /// Processes using more memory than this are candidates for optimization.
    /// </summary>
    public int MemoryThresholdMB { get; set; } = 1024;

    /// <summary>
    /// Timer resolution in 100-nanosecond units.
    /// Default 5000 = 0.5ms (5000 * 100ns = 500,000ns = 0.5ms).
    /// </summary>
    public int TimerResolution100ns { get; set; } = 5000;

    // -- v2 VBS/HVCI Advisory fields ----------------------------------------

    /// <summary>
    /// Whether GameShift disabled VBS/HVCI (tracks our responsibility).
    /// </summary>
    public bool VbsHvciDisabledByGameShift { get; set; } = false;

    /// <summary>
    /// Whether user dismissed the VBS/HVCI performance warning banner.
    /// </summary>
    public bool VbsHvciNotificationDismissed { get; set; } = false;

    /// <summary>
    /// Last time VBS/HVCI state was checked. Null if never checked.
    /// </summary>
    public DateTime? VbsHvciLastChecked { get; set; } = null;

    // -- v2 GPU fields -------------------------------------------------------

    /// <summary>
    /// GPU vendor override. "Auto" detects via WMI, "NVIDIA" or "AMD" forces vendor.
    /// </summary>
    public string GpuVendorOverride { get; set; } = "Auto";

    // -- v2 DPC Monitoring fields ------------------------------------------------

    /// <summary>
    /// Global default DPC latency threshold in microseconds.
    /// Per-game overrides in GameProfile.DpcThresholdMicroseconds.
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

    /// <summary>
    /// Profile ID selected via Quick Profile Switch.
    /// Empty string means "use default profile lookup" (no override).
    /// Set by TrayIconManager.OnProfileSelected, read by DetectionOrchestrator.OnGameStarted.
    /// Cleared after a game session uses it.
    /// </summary>
    public string QuickSwitchProfileId { get; set; } = string.Empty;

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

    // -- DPC Monitoring Preferences ----------------------------------------

    /// <summary>
    /// Whether to prefer ETW-based DPC monitoring over PerformanceCounter.
    /// ETW provides per-driver attribution but has slightly higher overhead (~1-3% CPU).
    /// When false, always uses PerformanceCounter regardless of admin privileges.
    /// </summary>
    public bool PreferEtwDpcMonitoring { get; set; } = true;

    // -- DPC Doctor --------------------------------------------------------

    /// <summary>
    /// Whether DPC Doctor shows Simple Mode (true) or Technical Mode (false).
    /// Persisted immediately on toggle.
    /// </summary>
    public bool DpcDoctorSimpleMode { get; set; } = true;

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
    /// Background Mode settings for always-on system optimizations.
    /// Null means Background Mode has never been configured (defaults used on first enable).
    /// </summary>
    public BackgroundModeSettings? BackgroundMode { get; set; }

    // -- System Tweaks ------------------------------------------------

    /// <summary>
    /// System Tweaks settings for one-time registry optimizations.
    /// Null means never configured.
    /// </summary>
    public SystemTweaksSettings? SystemTweaks { get; set; }

    // -- Game Profiles ------------------------------------------------

    /// <summary>
    /// Game Profiles settings for per-game optimizations.
    /// Null means never configured.
    /// </summary>
    public GameProfileSettings? GameProfiles { get; set; }

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
/// Settings for Background Mode — always-on system optimizations that run 24/7.
/// </summary>
public class BackgroundModeSettings
{
    /// <summary>Master toggle for all Background Mode services.</summary>
    public bool Enabled { get; set; } = false;

    // -- StandbyListCleaner --
    /// <summary>Enable always-on standby list cleaning.</summary>
    public bool StandbyListCleanerEnabled { get; set; } = true;
    /// <summary>
    /// Standby list size threshold in MB. Purge is triggered only when standby exceeds this
    /// AND free memory drops below StandbyListFreeMemoryMinMB simultaneously.
    /// 0 = auto-scale based on total RAM (see StandbyListCleaner.ComputeDefaults).
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

    // -- TimerResolutionService --
    /// <summary>Enable always-on timer resolution lock.</summary>
    public bool TimerResolutionEnabled { get; set; } = true;
    /// <summary>Target timer resolution in 100ns units. 5000 = 0.5ms.</summary>
    public int TimerResolution100ns { get; set; } = 5000;

    // -- PowerPlanManager --
    /// <summary>Enable always-on power plan management.</summary>
    public bool PowerPlanEnabled { get; set; } = true;
    /// <summary>Minutes of user idle before switching to balanced plan. 0 = never switch.</summary>
    public int IdleTimeoutMinutes { get; set; } = 15;

    // -- TaskDeferralService --
    /// <summary>Enable Windows scheduled task deferral during gaming.</summary>
    public bool TaskDeferralEnabled { get; set; } = true;

    // -- ProBalance --
    /// <summary>
    /// Enable dynamic background CPU restraint during gaming sessions.
    /// Demotes background processes that exceed 15% CPU for 3 consecutive samples
    /// to BelowNormal priority, restores them when they drop below threshold.
    /// </summary>
    public bool ProBalanceEnabled { get; set; } = true;

    // -- ProcessPriorityPersistence --
    /// <summary>Enable persistent process priority rules.</summary>
    public bool ProcessPriorityEnabled { get; set; } = false;
    /// <summary>
    /// Process rules: executable name → priority class name (e.g., "chrome.exe" → "BelowNormal").
    /// </summary>
    public Dictionary<string, string> ProcessPriorityRules { get; set; } = new();
}

/// <summary>
/// Settings for System Tweaks — one-time registry and system-level optimizations.
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

/// <summary>
/// Settings for the Game Profiles system — per-game optimization profiles.
/// </summary>
public class GameProfileSettings
{
    /// <summary>Master toggle for the Game Profiles system.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Per-profile user customizations (overrides built-in defaults).
    /// Key = profile ID (e.g., "overwatch2").
    /// </summary>
    public Dictionary<string, GameProfileOverrides> ProfileOverrides { get; set; } = new();

    /// <summary>User-created custom profiles.</summary>
    public List<GameProfiles.GameSessionConfig> CustomProfiles { get; set; } = new();
}

/// <summary>
/// User overrides for a built-in or custom game profile.
/// </summary>
public class GameProfileOverrides
{
    /// <summary>Whether this profile is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Custom CPU affinity mask override. Null = use profile default.</summary>
    public long? CustomAffinityMask { get; set; }

    /// <summary>Custom process priority override. Null = use profile default.</summary>
    public string? CustomPriority { get; set; }
}
