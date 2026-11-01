using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GameShift.Core.Config;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.Monitoring;
using GameShift.Core.Presentation;
using GameShift.Core.Profiles;

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

    // ── Optimization switches, one per module id ──────────────────────

    public IReadOnlyList<ProfileModuleSwitch> Modules { get; }
    public IReadOnlyList<ProfileModuleSwitch> AdvancedModules { get; }

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

    private string _dpcThresholdPercentText = "0";

    /// <summary>
    /// The per-game threshold as typed, in percent of CPU time. Text that does not parse keeps the last valid value.
    /// </summary>
    public string DpcThresholdPercentText
    {
        get => _dpcThresholdPercentText;
        set
        {
            if (_dpcThresholdPercentText == value) return;
            _dpcThresholdPercentText = value;
            OnPropertyChanged();
            if (DpcLoad.TryParseThreshold(value, out var threshold))
                DpcThresholdMicroseconds = threshold;
        }
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

    /// <summary>
    /// Creates the profile editor ViewModel.
    /// </summary>
    /// <param name="profileManager">For loading/saving profiles</param>
    /// <param name="orchestrator">For the known games list</param>
    public ProfileEditorViewModel(ProfileManager profileManager, DetectionOrchestrator orchestrator)
    {
        _profileManager = profileManager;
        _orchestrator = orchestrator;

        var switches = ProfileModules.Rows.Select(r => new ProfileModuleSwitch(r, () => IsDirty = true)).ToList();
        Modules = switches.Where(m => !m.IsAdvanced).ToList();
        AdvancedModules = switches.Where(m => m.IsAdvanced).ToList();
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
            DisplayName = "Default profile",
            Source = "For games without their own profile",
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

        foreach (var module in Modules.Concat(AdvancedModules))
            module.Load(profile.IsOptimizationEnabled(module.ModuleName));

        // v2 fields
        _enableDpcMonitoring = profile.EnableDpcMonitoring;
        _dpcThresholdMicroseconds = profile.DpcThresholdMicroseconds;
        _dpcThresholdPercentText = DpcLoad.FormatThreshold(profile.DpcThresholdMicroseconds);

        IsDirty = false;
        StatusMessage = "";

        // Load per-game statistics
        LoadPerGameStats(SelectedProfile.Id);

        // Load game-specific action display
        LoadGameActionDisplay(SelectedProfile.Id);

        OnPropertyChanged(nameof(EnableDpcMonitoring));
        OnPropertyChanged(nameof(DpcThresholdMicroseconds));
        OnPropertyChanged(nameof(DpcThresholdPercentText));
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
            ? stats.TotalPlayTime.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " hours"
            : stats.TotalPlayTime.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " minutes";
        SessionCount = stats.SessionCount.ToString(CultureInfo.InvariantCulture) + " sessions";
        AvgDpcLatency = DpcLoad.Format(stats.AvgDpcLatency);
        BestDpcLatency = DpcLoad.Format(stats.BestDpcLatency);
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
        var preset = string.IsNullOrEmpty(exeName) ? null : BuiltInProfiles.FindPresetGame(exeName)?.Preset;
        if (preset == null) return;

        IsPresetGame = true;

        HasAntiCheat = preset.HasAntiCheat;
        AntiCheatBadge = preset.AntiCheatName;
        VbsSafetyActive = !preset.VbsSafeToDisable;
        VbsSafetyReason = preset.VbsSafetyReason;

        var actions = BuiltInProfiles.GetPresetTips(exeName!);
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

        foreach (var module in Modules.Concat(AdvancedModules))
            ModuleSwitch.TrySet(profile, module.ModuleName, module.IsEnabled);

        // v2 fields
        profile.EnableDpcMonitoring = EnableDpcMonitoring;
        profile.DpcThresholdMicroseconds = DpcThresholdMicroseconds;

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
/// One module's switch in the profile editor. Loading a profile sets it without marking the editor dirty.
/// </summary>
public class ProfileModuleSwitch : INotifyPropertyChanged
{
    private readonly ProfileModuleRow _row;
    private readonly Action _changed;
    private bool _isEnabled;

    public ProfileModuleSwitch(ProfileModuleRow row, Action changed)
    {
        _row = row;
        _changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModuleName => _row.ModuleName;
    public string Title => _row.Title;
    public string Effect => _row.Effect;
    public string Note => _row.Note;
    public bool HasNote => _row.Note.Length > 0;
    public bool IsAdvanced => _row.IsAdvanced;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            _changed();
        }
    }

    public void Load(bool enabled)
    {
        _isEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
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
    public bool IsHardwareMismatch => !IsHardwareMatched;
}
