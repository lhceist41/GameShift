using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameShift.Core.GameProfiles;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;

namespace GameShift.App.ViewModels;

/// <summary>
/// Manages the hero one-click optimize section on the dashboard.
/// Listens to engine events to refresh state after apply/revert.
/// </summary>
public class HeroOptimizeViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<IOptimization> _optimizations;
    private readonly OptimizationEngine _engine;

    private string _heroStatusText = "Ready to Optimize";
    private string _heroSubtitleText = "Apply recommended system optimizations for gaming";
    private string _heroButtonText = "Optimize Now";
    private Brush _heroStatusBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private bool _isApplyingHero;
    private bool _showHeroPreview = true;
    private string _heroPreviewText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HeroStatusText
    {
        get => _heroStatusText;
        private set { _heroStatusText = value; OnPropertyChanged(); }
    }

    public string HeroSubtitleText
    {
        get => _heroSubtitleText;
        private set { _heroSubtitleText = value; OnPropertyChanged(); }
    }

    public string HeroButtonText
    {
        get => _heroButtonText;
        private set { _heroButtonText = value; OnPropertyChanged(); }
    }

    public Brush HeroStatusBrush
    {
        get => _heroStatusBrush;
        private set { _heroStatusBrush = value; OnPropertyChanged(); }
    }

    public bool IsApplyingHero
    {
        get => _isApplyingHero;
        private set { _isApplyingHero = value; OnPropertyChanged(); }
    }

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

    private bool IsHeroOptimized => _optimizations.Any(o => o.IsApplied);

    public HeroOptimizeViewModel(IReadOnlyList<IOptimization> optimizations, OptimizationEngine engine)
    {
        _optimizations = optimizations;
        _engine = engine;
        OptimizeNowCommand = new RelayCommand(ExecuteOptimizeNow);
        RefreshHeroState();
    }

    public void RefreshHeroState()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_isApplyingHero) return;

            if (IsHeroOptimized)
            {
                int appliedCount = _optimizations.Count(o => o.IsApplied);
                HeroStatusText = "Optimized";
                HeroSubtitleText = $"{appliedCount} optimization{(appliedCount != 1 ? "s" : "")} currently active";
                HeroButtonText = "Revert Optimizations";
                HeroStatusBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                ShowHeroPreview = false;
            }
            else
            {
                HeroStatusText = "Ready to Optimize";
                HeroSubtitleText = "Apply recommended system optimizations for gaming";
                HeroButtonText = "Optimize Now";
                HeroStatusBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
                ShowHeroPreview = true;
                BuildPreviewList();
            }
        });
    }

    private void BuildPreviewList()
    {
        var profile = GameProfile.CreateDefault();
        var items = new List<string>();

        foreach (var opt in _optimizations.Where(o => o.IsAvailable))
        {
            if (profile.IsOptimizationEnabled(opt.Name))
                items.Add(opt.Name);
        }

        HeroPreviewText = $"{items.Count} optimizations will be applied";

        HeroPreviewItems.Clear();
        foreach (var item in items)
            HeroPreviewItems.Add($"\u2022 {item}");
    }

    private async void ExecuteOptimizeNow()
    {
        if (_isApplyingHero) return;
        IsApplyingHero = true;

        try
        {
            if (IsHeroOptimized)
            {
                HeroStatusText = "Reverting...";
                HeroButtonText = "Reverting...";
                HeroStatusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                await _engine.DeactivateProfileAsync();
            }
            else
            {
                HeroStatusText = "Optimizing...";
                HeroButtonText = "Optimizing...";
                HeroStatusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                ShowHeroPreview = false;
                var profile = GameProfile.CreateDefault();
                profile.GameName = "Quick Optimize";
                await _engine.ActivateProfileAsync(profile);
            }
        }
        finally
        {
            IsApplyingHero = false;
            RefreshHeroState();
        }
    }

    private void OnOptimizationApplied(object? sender, OptimizationAppliedEventArgs e) => RefreshHeroState();
    private void OnOptimizationReverted(object? sender, OptimizationRevertedEventArgs e) => RefreshHeroState();

    public void Cleanup()
    {
        _engine.OptimizationApplied -= OnOptimizationApplied;
        _engine.OptimizationReverted -= OnOptimizationReverted;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
