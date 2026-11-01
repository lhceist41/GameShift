using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using GameShift.Core.Detection;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.Profiles;

namespace GameShift.App.ViewModels;

/// <summary>
/// The session modules, each with the default profile's switch and its badge for the session
/// running now.
/// </summary>
public sealed class DuringGamesViewModel
{
    private readonly OptimizationEngine _engine;
    private readonly DetectionOrchestrator? _orchestrator;
    private readonly ProfileManager? _profiles;
    private readonly Dictionary<string, IOptimization> _modulesById = new();
    private readonly DispatcherTimer _pollTimer;
    private bool _cleanedUp;

    public ObservableCollection<SessionModuleRow> Modules { get; } = new();

    public DuringGamesViewModel(
        IReadOnlyList<IOptimization> modules, OptimizationEngine engine,
        DetectionOrchestrator? orchestrator, ProfileManager? profiles)
    {
        _engine = engine;
        _orchestrator = orchestrator;
        _profiles = profiles;

        foreach (var module in modules)
        {
            _modulesById[module.Name] = module;
            Modules.Add(new SessionModuleRow(
                module.Name, SessionModuleText.For(module.Name, module.Description),
                ModuleSwitch.HasSwitch(module.Name), OnSwitchChanged));
        }

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += OnPollTick;

        _engine.OptimizationApplied += OnEngineEvent;
        _engine.OptimizationFailed += OnEngineEvent;
        _engine.OptimizationReverted += OnEngineEvent;
        _engine.ActivationCompleted += OnEngineEvent;
        _engine.DeactivationCompleted += OnEngineEvent;
    }

    /// <summary>Reads every badge and switch again; polls while a game session runs.</summary>
    public void Refresh()
    {
        // An engine event can queue a refresh that runs after Cleanup; it must not restart the timer.
        if (_cleanedUp) return;

        var profile = _profiles?.GetDefaultProfile();
        var inputs = Modules
            .Select(row => new SessionModuleInput(
                row.ModuleName,
                _modulesById[row.ModuleName].IsAvailable,
                profile?.IsOptimizationEnabled(row.ModuleName) ?? true))
            .ToList();

        var snapshot = SessionModuleStates.Read(
            inputs, () => _orchestrator?.SessionRequestedUtc, () => _engine.LastOutcome,
            _orchestrator?.IsPaused == true);

        foreach (var row in Modules)
        {
            row.ShowSwitch(profile?.IsOptimizationEnabled(row.ModuleName) ?? true);
            row.ShowStatus(snapshot.ByModule[row.ModuleName]);
        }

        if (snapshot.SessionRunning)
            _pollTimer.Start();
        else
            _pollTimer.Stop();
    }

    private void OnSwitchChanged(SessionModuleRow row, bool enabled)
    {
        if (_profiles == null) return;

        var profile = _profiles.GetDefaultProfile();
        if (ModuleSwitch.TrySet(profile, row.ModuleName, enabled))
            _profiles.SaveProfile(profile);
        Refresh();
    }

    private void OnPollTick(object? sender, EventArgs e) => Refresh();

    private void OnEngineEvent(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(Refresh);

    /// <summary>Stops polling and unsubscribes. Call when the page is unloaded.</summary>
    public void Cleanup()
    {
        _cleanedUp = true;
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _engine.OptimizationApplied -= OnEngineEvent;
        _engine.OptimizationFailed -= OnEngineEvent;
        _engine.OptimizationReverted -= OnEngineEvent;
        _engine.ActivationCompleted -= OnEngineEvent;
        _engine.DeactivationCompleted -= OnEngineEvent;
    }
}

/// <summary>One session module on the During games page.</summary>
public sealed class SessionModuleRow : INotifyPropertyChanged
{
    private readonly Action<SessionModuleRow, bool> _onSwitchChanged;
    private bool _isEnabled;
    private bool _showingSwitch;
    private ModuleStatusKind _statusKind = ModuleStatusKind.Ready;
    private string _statusWord = "Ready";
    private string? _statusReason;

    public SessionModuleRow(string moduleName, SessionModuleText text, bool hasSwitch,
        Action<SessionModuleRow, bool> onSwitchChanged)
    {
        ModuleName = moduleName;
        Title = text.Title;
        Effect = text.Effect;
        HasSwitch = hasSwitch;
        _onSwitchChanged = onSwitchChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The module id, shown as the technical name.</summary>
    public string ModuleName { get; }
    public string Title { get; }
    public string Effect { get; }
    public bool HasSwitch { get; }
    public bool HasNoSwitch => !HasSwitch;
    public string FollowsOtherSwitchesText => ModuleSwitch.FollowsOtherSwitchesText;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            if (!_showingSwitch && HasSwitch)
                _onSwitchChanged(this, value);
        }
    }

    public ModuleStatusKind StatusKind
    {
        get => _statusKind;
        private set { if (_statusKind == value) return; _statusKind = value; OnPropertyChanged(); }
    }

    public string StatusWord
    {
        get => _statusWord;
        private set { if (_statusWord == value) return; _statusWord = value; OnPropertyChanged(); }
    }

    public string? StatusReason
    {
        get => _statusReason;
        private set
        {
            if (_statusReason == value) return;
            _statusReason = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusReason));
        }
    }

    public bool HasStatusReason => !string.IsNullOrEmpty(StatusReason);

    /// <summary>Shows the saved switch state without saving it again.</summary>
    internal void ShowSwitch(bool enabled)
    {
        _showingSwitch = true;
        try { IsEnabled = enabled; }
        finally { _showingSwitch = false; }
    }

    internal void ShowStatus(ModuleStatus status)
    {
        StatusKind = status.Kind;
        StatusWord = status.Word;
        StatusReason = status.Reason;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
