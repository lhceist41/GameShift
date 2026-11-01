using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameShift.Core.SystemTweaks;

namespace GameShift.App.ViewModels;

/// <summary>
/// The lasting changes GameShift can find, the system tweaks it offers, and Restore All's status.
/// </summary>
public sealed class LastingChangesViewModel : INotifyPropertyChanged
{
    private bool _isChecking;
    private string _statusMessage = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LastingChangeRow> Changes { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<SystemTweakRow> Tweaks { get; } = new();

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            _isChecking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoChangesFound));
        }
    }

    /// <summary>The list was read, without warnings, and holds nothing.</summary>
    public bool NoChangesFound => !IsChecking && Changes.Count == 0 && Warnings.Count == 0;

    public bool HasWarnings => Warnings.Count > 0;

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public void ShowChecking()
    {
        Changes.Clear();
        Warnings.Clear();
        IsChecking = true;
        OnPropertyChanged(nameof(HasWarnings));
    }

    public void ShowChanges(IReadOnlyList<LastingChange> changes, IReadOnlyList<string> warnings)
    {
        Changes.Clear();
        foreach (var change in changes)
            Changes.Add(new LastingChangeRow(change));
        Warnings.Clear();
        foreach (var warning in warnings)
            Warnings.Add(warning);
        IsChecking = false;
        OnPropertyChanged(nameof(HasWarnings));
    }

    public void RefreshTweaks(SystemTweaksManager manager)
    {
        Tweaks.Clear();
        foreach (var tweak in manager.OfferedTweaks)
            Tweaks.Add(new SystemTweakRow(tweak, manager.GetTweakStatus(tweak), tweak.DetectIsApplied()));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One lasting change found on this PC.</summary>
public sealed class LastingChangeRow(LastingChange change)
{
    public string DisplayName => change.DisplayName;
    public string UndoDescription => change.UndoDescription;
    public bool RequiresRestart => change.RequiresRestart;
    public bool NeedsOwnConfirmation => change.NeedsOwnConfirmation;
}

/// <summary>One system tweak GameShift offers, with the action its button takes.</summary>
public sealed class SystemTweakRow
{
    /// <summary>The <see cref="SystemTweaksManager.GetTweakStatus"/> text of a tweak GameShift applied and can revert.</summary>
    public const string AppliedByGameShift = "Applied (by GameShift)";

    public SystemTweakRow(ISystemTweak tweak, string status, bool isApplied)
    {
        Tweak = tweak;
        Status = status;
        bool isGameShift = status == AppliedByGameShift;
        // A restart action opens the restart dialog, so its caption carries an ellipsis.
        var more = tweak.RequiresReboot ? "…" : "";
        ButtonText = isApplied ? (isGameShift ? "Revert" + more : "Applied") : "Apply" + more;
        ButtonEnabled = !isApplied || isGameShift;
    }

    public ISystemTweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public bool RequiresReboot => Tweak.RequiresReboot;
    public bool NoReboot => !Tweak.RequiresReboot;
    public string Status { get; }
    public string ButtonText { get; }
    public bool ButtonEnabled { get; }
}
