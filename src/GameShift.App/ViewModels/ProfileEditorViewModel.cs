using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.Profiles;
using GameShift.Core.Profiles.GameActions;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages the profile editor view: game/profile selection and per-game optimization toggles.
/// Uses ProfileManager for profile CRUD and DetectionOrchestrator for the known games list.
/// Each toggle setter marks IsDirty=true so the Save button enables on any change.
/// </summary>
public class ProfileEditorViewModel : INotifyPropertyChanged
{
    private readonly ProfileManager _profileManager;
    private readonly DetectionOrchestrator _orchestrator;

    private ProfileListItem? _selectedProfile;
    private bool _suppressServices;
    private bool _switchPowerPlan;
    private bool _setTimerResolution;
    private bool _boostProcessPriority;
    private bool _optimizeMemory;
    private bool _reduceVisualEffects;
    private bool _optimizeNetwork;
    private bool _usePerformanceCoresOnly;
    private int _memoryThresholdMB;

    // v2 Competitive Mode
    private bool _enableCompetitiveMode;
    private bool _suspendDiscordOverlay;
    private bool _suspendSteamOverlay;
    private bool _suspendNvidiaOverlay;
    private bool _killWidgets;

    // v2 GPU Optimization
    private bool _enableGpuOptimization;
    private bool _forceMaxPerformancePowerMode;
    private bool _optimizeShaderCache;
    private bool _enableLowLatencyMode;

    // v2 MPO
    private bool _disableMpo;

    // v3 Intensity
    private string _intensity = "Casual";

    // v2 DPC Monitoring
    private bool _enableDpcMonitoring;
    private int _dpcThresholdMicroseconds;

    private bool _isDirty;
    private string _statusMessage = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Observable list of profiles/games shown in the left panel.
    /// </summary>
    public ObservableCollection<ProfileListItem> Profiles { get; } = new();

    /// <summary>
    /// Currently selected profile in the list. Triggers LoadProfile on change.
    /// </summary>
    public ProfileListItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            _selectedProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedProfile));
            OnSelectedProfileChanged();
        }
    }

    // ── Optimization toggle properties ────────────────────────────────

    public bool SuppressServices
    {
        get => _suppressServices;
        set { if (_suppressServices != value) { _suppressServices = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool SwitchPowerPlan
    {
        get => _switchPowerPlan;
        set { if (_switchPowerPlan != value) { _switchPowerPlan = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool SetTimerResolution
    {
        get => _setTimerResolution;
        set { if (_setTimerResolution != value) { _setTimerResolution = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool BoostProcessPriority
    {
        get => _boostProcessPriority;
        set { if (_boostProcessPriority != value) { _boostProcessPriority = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool OptimizeMemory
    {
        get => _optimizeMemory;
        set { if (_optimizeMemory != value) { _optimizeMemory = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool ReduceVisualEffects
    {
        get => _reduceVisualEffects;
        set { if (_reduceVisualEffects != value) { _reduceVisualEffects = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool OptimizeNetwork
    {
        get => _optimizeNetwork;
        set { if (_optimizeNetwork != value) { _optimizeNetwork = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool UsePerformanceCoresOnly
    {
        get => _usePerformanceCoresOnly;
        set { if (_usePerformanceCoresOnly != value) { _usePerformanceCoresOnly = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int MemoryThresholdMB
    {
        get => _memoryThresholdMB;
        set { if (_memoryThresholdMB != value) { _memoryThresholdMB = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── v2 Competitive Mode toggles ──────────────────────────────────

    public bool EnableCompetitiveMode
    {
        get => _enableCompetitiveMode;
        set { if (_enableCompetitiveMode != value) { _enableCompetitiveMode = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool SuspendDiscordOverlay
    {
        get => _suspendDiscordOverlay;
        set { if (_suspendDiscordOverlay != value) { _suspendDiscordOverlay = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool SuspendSteamOverlay
    {
        get => _suspendSteamOverlay;
        set { if (_suspendSteamOverlay != value) { _suspendSteamOverlay = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool SuspendNvidiaOverlay
    {
        get => _suspendNvidiaOverlay;
        set { if (_suspendNvidiaOverlay != value) { _suspendNvidiaOverlay = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool KillWidgets
    {
        get => _killWidgets;
        set { if (_killWidgets != value) { _killWidgets = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── v2 GPU Optimization toggles ──────────────────────────────────

    public bool EnableGpuOptimization
    {
        get => _enableGpuOptimization;
        set { if (_enableGpuOptimization != value) { _enableGpuOptimization = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool ForceMaxPerformancePowerMode
    {
        get => _forceMaxPerformancePowerMode;
        set { if (_forceMaxPerformancePowerMode != value) { _forceMaxPerformancePowerMode = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool OptimizeShaderCache
    {
        get => _optimizeShaderCache;
        set { if (_optimizeShaderCache != value) { _optimizeShaderCache = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public bool EnableLowLatencyMode
    {
        get => _enableLowLatencyMode;
        set { if (_enableLowLatencyMode != value) { _enableLowLatencyMode = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── v2 MPO toggle ────────────────────────────────────────────────

    public bool DisableMpo
    {
        get => _disableMpo;
        set { if (_disableMpo != value) { _disableMpo = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── v3 Intensity ──────────────────────────────────────────────────

    public List<string> IntensityOptions { get; } = new() { "Competitive", "Casual" };

    public string Intensity
    {
        get => _intensity;
        set { if (_intensity != value) { _intensity = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── v2 DPC Monitoring ────────────────────────────────────────────

    public bool EnableDpcMonitoring
    {
        get => _enableDpcMonitoring;
        set { if (_enableDpcMonitoring != value) { _enableDpcMonitoring = value; IsDirty = true; OnPropertyChanged(); } }
    }

    public int DpcThresholdMicroseconds
    {
        get => _dpcThresholdMicroseconds;
        set { if (_dpcThresholdMicroseconds != value) { _dpcThresholdMicroseconds = value; IsDirty = true; OnPropertyChanged(); } }
    }

    // ── UI state ──────────────────────────────────────────────────────

    /// <summary>
    /// True when a profile is selected in the list.
    /// </summary>
    public bool HasSelectedProfile => SelectedProfile != null;

    /// <summary>
    /// True when there are unsaved changes. Enables the Save button.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        set { _isDirty = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Status message shown next to the Save button (e.g. "Profile saved.").
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    // ── Per-game statistics (from session history) ──────────────────────

    private string _totalPlayTime = "";
    public string TotalPlayTime { get => _totalPlayTime; set { _totalPlayTime = value; OnPropertyChanged(); } }

    private string _sessionCount = "";
    public string SessionCount { get => _sessionCount; set { _sessionCount = value; OnPropertyChanged(); } }

    private string _avgDpcLatency = "";
    public string AvgDpcLatency { get => _avgDpcLatency; set { _avgDpcLatency = value; OnPropertyChanged(); } }

    private string _bestDpcLatency = "";
    public string BestDpcLatency { get => _bestDpcLatency; set { _bestDpcLatency = value; OnPropertyChanged(); } }

    private bool _hasStats = false;
    public bool HasStats { get => _hasStats; set { _hasStats = value; OnPropertyChanged(); } }

    // ── Per-game action display ──────────────────────────────────────

    /// <summary>Observable list of game-specific action cards for the selected profile.</summary>
    public ObservableCollection<GameActionDisplayItem> GameActions { get; } = new();

    private bool _isPresetGame;
    /// <summary>Whether the selected game is a preset game (controls section visibility).</summary>
    public bool IsPresetGame { get => _isPresetGame; set { _isPresetGame = value; OnPropertyChanged(); } }

    private string _antiCheatBadge = "";
    /// <summary>Anti-cheat system name for badge display (e.g. "Riot Vanguard").</summary>
    public string AntiCheatBadge { get => _antiCheatBadge; set { _antiCheatBadge = value; OnPropertyChanged(); } }

    private bool _hasAntiCheat;
    /// <summary>Whether the game has an anti-cheat system (controls badge visibility).</summary>
    public bool HasAntiCheat { get => _hasAntiCheat; set { _hasAntiCheat = value; OnPropertyChanged(); } }

    private bool _vbsSafetyActive;
    /// <summary>Whether VBS cannot be disabled for this game.</summary>
    public bool VbsSafetyActive { get => _vbsSafetyActive; set { _vbsSafetyActive = value; OnPropertyChanged(); } }

    private string _vbsSafetyReason = "";
    /// <summary>Explanation text for VBS safety interlock.</summary>
    public string VbsSafetyReason { get => _vbsSafetyReason; set { _vbsSafetyReason = value; OnPropertyChanged(); } }

    private bool _hasGameActions;
    /// <summary>Whether there are any game-specific actions to display.</summary>
    public bool HasGameActions { get => _hasGameActions; set { _hasGameActions = value; OnPropertyChanged(); } }

    private int _activeActionCount;
    /// <summary>Number of Tier 1 hardware-matched actions (auto-applied).</summary>
    public int ActiveActionCount { get => _activeActionCount; set { _activeActionCount = value; OnPropertyChanged(); } }

    private int _totalActionCount;
    /// <summary>Total number of game actions across all tiers.</summary>
    public int TotalActionCount { get => _totalActionCount; set { _totalActionCount = value; OnPropertyChanged(); } }

    /// <summary>
    /// Creates the profile editor ViewModel.
    /// </summary>
    /// <param name="profileManager">For loading/saving profiles</param>
    /// <param name="orchestrator">For the known games list</param>
    public ProfileEditorViewModel(ProfileManager profileManager, DetectionOrchestrator orchestrator)
    {
        _profileManager = profileManager;
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// Populates the profile list from two sources:
    /// 1. The default profile (always shown first)
    /// 2. All known games from the orchestrator
    /// </summary>
    public void LoadProfiles()
    {
        Profiles.Clear();

        // Always show default profile first
        Profiles.Add(new ProfileListItem
        {
            Id = "default",
            DisplayName = "Default Profile (applies to all games without custom profile)",
            Source = "Default",
            IsDefault = true
        });

        // Add each known game
        foreach (var game in _orchestrator.GetKnownGames())
        {
            Profiles.Add(new ProfileListItem
            {
                Id = game.Id,
                DisplayName = game.GameName,
                Source = game.LauncherSource
            });
        }
    }

    /// <summary>
    /// Called when SelectedProfile changes.
    /// Loads the profile's toggle values into the ViewModel properties.
    /// </summary>
    private void OnSelectedProfileChanged()
    {
        if (SelectedProfile == null) return;

        var profile = _profileManager.GetProfileForGame(SelectedProfile.Id);

        // Populate toggle properties from the loaded profile
        _suppressServices = profile.SuppressServices;
        _switchPowerPlan = profile.SwitchPowerPlan;
        _setTimerResolution = profile.SetTimerResolution;
        _boostProcessPriority = profile.BoostProcessPriority;
        _optimizeMemory = profile.OptimizeMemory;
        _reduceVisualEffects = profile.ReduceVisualEffects;
        _optimizeNetwork = profile.OptimizeNetwork;
        _usePerformanceCoresOnly = profile.UsePerformanceCoresOnly;
        _memoryThresholdMB = profile.MemoryThresholdMB;

        // v2 fields
        _enableCompetitiveMode = profile.EnableCompetitiveMode;
        _suspendDiscordOverlay = profile.SuspendDiscordOverlay;
        _suspendSteamOverlay = profile.SuspendSteamOverlay;
        _suspendNvidiaOverlay = profile.SuspendNvidiaOverlay;
        _killWidgets = profile.KillWidgets;
        _enableGpuOptimization = profile.EnableGpuOptimization;
        _forceMaxPerformancePowerMode = profile.ForceMaxPerformancePowerMode;
        _optimizeShaderCache = profile.OptimizeShaderCache;
        _enableLowLatencyMode = profile.EnableLowLatencyMode;
        _disableMpo = profile.DisableMpo;
        _enableDpcMonitoring = profile.EnableDpcMonitoring;
        _dpcThresholdMicroseconds = profile.DpcThresholdMicroseconds;
        _intensity = profile.Intensity.ToString();

        IsDirty = false;
        StatusMessage = "";

        // Load per-game statistics
        LoadPerGameStats(SelectedProfile.Id);

        // Load game-specific action display
        LoadGameActionDisplay(SelectedProfile.Id);

        // Notify all toggle properties changed
        OnPropertyChanged(nameof(SuppressServices));
        OnPropertyChanged(nameof(SwitchPowerPlan));
        OnPropertyChanged(nameof(SetTimerResolution));
        OnPropertyChanged(nameof(BoostProcessPriority));
        OnPropertyChanged(nameof(OptimizeMemory));
        OnPropertyChanged(nameof(ReduceVisualEffects));
        OnPropertyChanged(nameof(OptimizeNetwork));
        OnPropertyChanged(nameof(UsePerformanceCoresOnly));
        OnPropertyChanged(nameof(MemoryThresholdMB));
        OnPropertyChanged(nameof(EnableCompetitiveMode));
        OnPropertyChanged(nameof(SuspendDiscordOverlay));
        OnPropertyChanged(nameof(SuspendSteamOverlay));
        OnPropertyChanged(nameof(SuspendNvidiaOverlay));
        OnPropertyChanged(nameof(KillWidgets));
        OnPropertyChanged(nameof(EnableGpuOptimization));
        OnPropertyChanged(nameof(ForceMaxPerformancePowerMode));
        OnPropertyChanged(nameof(OptimizeShaderCache));
        OnPropertyChanged(nameof(EnableLowLatencyMode));
        OnPropertyChanged(nameof(DisableMpo));
        OnPropertyChanged(nameof(EnableDpcMonitoring));
        OnPropertyChanged(nameof(DpcThresholdMicroseconds));
        OnPropertyChanged(nameof(Intensity));
        OnPropertyChanged(nameof(HasSelectedProfile));
    }

    /// <summary>
    /// Loads per-game statistics from session history for the given game.
    /// </summary>
    private void LoadPerGameStats(string gameId)
    {
        if (App.Services.SessionStore == null || string.IsNullOrEmpty(gameId))
        {
            HasStats = false;
            return;
        }

        var stats = App.Services.SessionStore.GetStatsForGame(gameId);
        if (stats == null)
        {
            HasStats = false;
            return;
        }

        HasStats = true;
        TotalPlayTime = stats.TotalPlayTime.TotalHours >= 1
            ? $"{stats.TotalPlayTime.TotalHours:F1} hours"
            : $"{stats.TotalPlayTime.TotalMinutes:F0} minutes";
        SessionCount = $"{stats.SessionCount} sessions";
        AvgDpcLatency = $"{stats.AvgDpcLatency:F0} us";
        BestDpcLatency = $"{stats.BestDpcLatency:F0} us";
    }

    /// <summary>
    /// Loads game-specific action display data for preset games.
    /// Populates GameActions, metadata badges, and VBS safety warnings.
    /// </summary>
    private void LoadGameActionDisplay(string profileId)
    {
        GameActions.Clear();
        IsPresetGame = false;
        HasAntiCheat = false;
        HasGameActions = false;
        VbsSafetyActive = false;

        var game = _orchestrator.GetKnownGames().FirstOrDefault(g => g.Id == profileId);
        if (game == null) return;

        var exeName = Path.GetFileName(game.ExecutablePath);
        if (string.IsNullOrEmpty(exeName) || !CompetitivePresets.IsPresetGame(exeName)) return;

        IsPresetGame = true;

        var metadata = CompetitivePresets.GetMetadata(exeName);
        if (metadata != null)
        {
            HasAntiCheat = metadata.HasAntiCheat;
            AntiCheatBadge = metadata.AntiCheatName;
            VbsSafetyActive = !metadata.VbsSafeToDisable;
            VbsSafetyReason = metadata.VbsSafetyReason;
        }

        var actions = CompetitivePresets.GetGameActions(exeName);
        var hw = App.Services.HardwareScan;

        foreach (var action in actions)
        {
            GameActions.Add(new GameActionDisplayItem
            {
                Name = action.Name,
                Tier = action.Tier,
                Impact = action.Impact,
                Condition = action.Condition,
                IsConditional = action.IsConditional,
                IsHardwareMatched = hw != null ? action.IsHardwareMatch(hw) : true
            });
        }

        HasGameActions = GameActions.Count > 0;
        ActiveActionCount = GameActions.Count(a => a.Tier == 1 && a.IsHardwareMatched);
        TotalActionCount = GameActions.Count;
    }

    /// <summary>
    /// Saves the current toggle values to the selected profile.
    /// If the selected game only has the default profile, creates a new game-specific profile.
    /// </summary>
    public void SaveProfile()
    {
        if (SelectedProfile == null) return;

        GameProfile profile;
        if (SelectedProfile.IsDefault)
        {
            profile = _profileManager.GetDefaultProfile();
        }
        else
        {
            profile = _profileManager.GetProfileForGame(SelectedProfile.Id);
            // If it returned default, create a new profile for this game
            if (profile.Id == "default")
            {
                var game = _orchestrator.GetKnownGames()
                    .FirstOrDefault(g => g.Id == SelectedProfile.Id);
                if (game != null)
                {
                    profile = _profileManager.CreateProfileFromGameInfo(game);
                }
            }
        }

        // Apply toggle values
        profile.SuppressServices = SuppressServices;
        profile.SwitchPowerPlan = SwitchPowerPlan;
        profile.SetTimerResolution = SetTimerResolution;
        profile.BoostProcessPriority = BoostProcessPriority;
        profile.OptimizeMemory = OptimizeMemory;
        profile.ReduceVisualEffects = ReduceVisualEffects;
        profile.OptimizeNetwork = OptimizeNetwork;
        profile.UsePerformanceCoresOnly = UsePerformanceCoresOnly;
        profile.MemoryThresholdMB = MemoryThresholdMB;

        // v2 fields
        profile.EnableCompetitiveMode = EnableCompetitiveMode;
        profile.SuspendDiscordOverlay = SuspendDiscordOverlay;
        profile.SuspendSteamOverlay = SuspendSteamOverlay;
        profile.SuspendNvidiaOverlay = SuspendNvidiaOverlay;
        profile.KillWidgets = KillWidgets;
        profile.EnableGpuOptimization = EnableGpuOptimization;
        profile.ForceMaxPerformancePowerMode = ForceMaxPerformancePowerMode;
        profile.OptimizeShaderCache = OptimizeShaderCache;
        profile.EnableLowLatencyMode = EnableLowLatencyMode;
        profile.DisableMpo = DisableMpo;
        profile.EnableDpcMonitoring = EnableDpcMonitoring;
        profile.DpcThresholdMicroseconds = DpcThresholdMicroseconds;
        profile.Intensity = Enum.Parse<OptimizationIntensity>(Intensity);

        _profileManager.SaveProfile(profile);
        IsDirty = false;
        StatusMessage = "Profile saved.";
    }

    /// <summary>
    /// Reloads the current profile from disk, discarding unsaved changes.
    /// </summary>
    public void ResetToDefaults()
    {
        OnSelectedProfileChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Display model for a game/profile entry in the Profile Editor's left panel list.
/// </summary>
public class ProfileListItem
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsDefault { get; set; }
}

/// <summary>
/// Display model for a game-specific action card in the Profiles page.
/// </summary>
public class GameActionDisplayItem
{
    public string Name { get; set; } = "";
    public int Tier { get; set; } = 1;
    public string TierLabel => Tier switch { 1 => "AUTO", 2 => "MANUAL", 3 => "TIP", _ => "" };
    public string Impact { get; set; } = "";
    public string Condition { get; set; } = "";
    public bool IsConditional { get; set; }
    public bool IsHardwareMatched { get; set; } = true;
}
