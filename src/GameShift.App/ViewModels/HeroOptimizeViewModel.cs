using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using GameShift.Core.Detection;
using GameShift.Core.GameProfiles;
using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using GameShift.Core.Profiles;

namespace GameShift.App.ViewModels;

/// <summary>
/// Home's "Optimize now" / "End session" button and the list of what Quick Optimize applies.
/// The status itself comes from <see cref="GameShift.Core.Presentation.HomeSnapshot"/>.
/// </summary>
public class HeroOptimizeViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<IOptimization> _optimizations;
    private readonly DetectionOrchestrator _orchestrator;
    private readonly ProfileManager? _profileManager;

    private string _heroButtonText = "Optimize now";
    private bool _isApplyingHero;
    private bool _showHeroPreview = true;
    private string _heroPreviewText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HeroButtonText
    {
        get => _heroButtonText;
        private set { _heroButtonText = value; OnPropertyChanged(); }
    }

    public bool IsApplyingHero
    {
        get => _isApplyingHero;
        private set
        {
            _isApplyingHero = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPressButton));
        }
    }

    /// <summary>The button is disabled while it applies or undoes; its text says which.</summary>
    public bool CanPressButton => !_isApplyingHero;

    public bool ShowHeroPreview
    {
        get => _showHeroPreview;
        private set { _showHeroPreview = value; OnPropertyChanged(); }
    }

    public string HeroPreviewText
    {
        get => _heroPreviewText;
        private set { _heroPreviewText = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> HeroPreviewItems { get; } = new();

    public ICommand OptimizeNowCommand { get; }

    public HeroOptimizeViewModel(
        IReadOnlyList<IOptimization> optimizations, DetectionOrchestrator orchestrator, ProfileManager? profileManager)
    {
        _optimizations = optimizations;
        _orchestrator = orchestrator;
        _profileManager = profileManager;
        OptimizeNowCommand = new RelayCommand(ExecuteOptimizeNow);
        RefreshHeroState();
    }

    public void RefreshHeroState()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_isApplyingHero) return;

            if (_orchestrator.IsSessionActive)
            {
                HeroButtonText = "End session";
                ShowHeroPreview = false;
            }
            else
            {
                HeroButtonText = "Optimize now";
                ShowHeroPreview = true;
                BuildPreviewList();
            }
        });
    }

    // The saved default profile is what the "During games" page edits.
    private GameProfile QuickOptimizeProfile() => _profileManager?.GetDefaultProfile() ?? GameProfile.CreateDefault();

    private void BuildPreviewList()
    {
        var profile = QuickOptimizeProfile();
        var items = new List<string>();

        foreach (var opt in _optimizations.Where(o => o.IsAvailable))
        {
            if (profile.IsOptimizationEnabled(opt.Name))
                items.Add($"• {SessionModuleText.For(opt.Name, opt.Description).Title}");
        }

        HeroPreviewText = items.Count == 1
            ? "Optimize now applies 1 change"
            : $"Optimize now applies {items.Count} changes";

        // Refreshed on the home timer, so an unchanged list is left alone instead of redrawn.
        if (HeroPreviewItems.SequenceEqual(items)) return;

        HeroPreviewItems.Clear();
        foreach (var item in items)
            HeroPreviewItems.Add(item);
    }

    private async void ExecuteOptimizeNow()
    {
        if (_isApplyingHero) return;
        IsApplyingHero = true;

        try
        {
            if (_orchestrator.IsSessionActive)
            {
                HeroButtonText = "Undoing...";
                // Through the orchestrator: during a game this ends the game session as a whole,
                // including the game's own changes; a Quick Optimize session reverts through the engine.
                await _orchestrator.EndSessionAsync();
            }
            else
            {
                HeroButtonText = "Applying...";
                ShowHeroPreview = false;
                var profile = QuickOptimizeProfile();
                profile.GameName = "Quick Optimize";
                await _orchestrator.QuickOptimizeAsync(profile);
            }
        }
        catch (Exception ex)
        {
            // An async void handler must never let an exception escape - that would crash the
            // whole app on a button click. The finally block restores the button to a correct,
            // usable state via RefreshHeroState().
            Serilog.Log.Error(ex, "Quick Optimize (hero button) failed");
        }
        finally
        {
            IsApplyingHero = false;
            RefreshHeroState();
        }
    }

    /// <summary>
    /// Called by DashboardViewModel during page teardown. This ViewModel
    /// does not subscribe to engine events itself (DashboardViewModel drives
    /// refresh via RefreshHeroState), so there is nothing to release here.
    /// Kept as a stable hook in case future subscriptions are added.
    /// </summary>
    public void Cleanup()
    {
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
