using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GameShift.Core.Optimization;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for OptimizationsPage.
/// Displays all 11 optimizations grouped by category with live status updates.
/// Groups: Core v1.0 (indices 0-7), Competitive v2.0 (indices 8-9), System Advisory (index 10).
/// </summary>
public class OptimizationsViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<IOptimization> _optimizations;
    private readonly OptimizationEngine _engine;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The three optimization categories displayed on the page.
    /// </summary>
    public ObservableCollection<OptimizationGroup> Groups { get; } = new();

    /// <summary>
    /// Creates the ViewModel, categorizes optimizations, and subscribes to engine events.
    /// </summary>
    /// <param name="optimizations">All optimization modules from App.Services.Optimizations.</param>
    /// <param name="engine">Optimization engine for applying/reverting optimizations.</param>
    public OptimizationsViewModel(IReadOnlyList<IOptimization> optimizations, OptimizationEngine engine)
    {
        _optimizations = optimizations;
        _engine = engine;

        // Build groups with hardcoded index ranges (per 13-CONTEXT.md)
        var coreGroup = new OptimizationGroup
        {
            CategoryName = "Core v1.0",
            CategoryDescription = "Foundational system optimizations for reduced latency and improved frame delivery",
            CategoryColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) // Green
        };

        var competitiveGroup = new OptimizationGroup
        {
            CategoryName = "Competitive v2.0",
            CategoryDescription = "Advanced optimizations for competitive gaming scenarios",
            CategoryColor = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)) // Purple
        };

        var advisoryGroup = new OptimizationGroup
        {
            CategoryName = "System Advisory",
            CategoryDescription = "System-level advisories and driver optimizations",
            CategoryColor = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)) // Yellow
        };

        // Core v1.0: indices 0-7
        for (int i = 0; i <= 7 && i < _optimizations.Count; i++)
        {
            coreGroup.Items.Add(CreateItem(_optimizations[i]));
        }

        // Competitive v2.0: indices 8-9
        for (int i = 8; i <= 9 && i < _optimizations.Count; i++)
        {
            competitiveGroup.Items.Add(CreateItem(_optimizations[i]));
        }

        // System Advisory: index 10
        if (_optimizations.Count > 10)
        {
            advisoryGroup.Items.Add(CreateItem(_optimizations[10]));
        }

        Groups.Add(coreGroup);
        Groups.Add(competitiveGroup);
        Groups.Add(advisoryGroup);

        // Set initial statuses
        RefreshStatuses();

        // Subscribe to engine events for live updates
        _engine.OptimizationApplied += OnOptimizationApplied;
        _engine.OptimizationReverted += OnOptimizationReverted;
    }

    /// <summary>
    /// Creates an OptimizationItem from an IOptimization (without setting live status — RefreshStatuses handles that).
    /// </summary>
    private static OptimizationItem CreateItem(IOptimization opt)
    {
        return new OptimizationItem
        {
            Name = opt.Name,
            Description = opt.Description
        };
    }

    /// <summary>
    /// Iterates all groups and items, refreshing Status/StatusBrush/IsApplied/IsAvailable
    /// from the corresponding IOptimization's live state.
    /// Dispatches to UI thread since this is called from background engine events.
    /// </summary>
    private void RefreshStatuses()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Rebuild a flat index mapping across all groups
            int index = 0;

            foreach (var group in Groups)
            {
                foreach (var item in group.Items)
                {
                    if (index < _optimizations.Count)
                    {
                        var opt = _optimizations[index];
                        UpdateItem(item, opt);
                    }
                    index++;
                }
            }
        });
    }

    /// <summary>
    /// Updates a single OptimizationItem from the live IOptimization state.
    /// Applied → green (#4ADE80), Standby → light gray (#E0E0E0), Unavailable → dark gray (#666666).
    /// </summary>
    private static void UpdateItem(OptimizationItem item, IOptimization opt)
    {
        item.IsApplied = opt.IsApplied;
        item.IsAvailable = opt.IsAvailable;

        if (opt.IsApplied)
        {
            item.Status = "Applied";
            item.StatusBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // #4ADE80 green
        }
        else if (!opt.IsAvailable)
        {
            item.Status = "Unavailable";
            item.StatusBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)); // #666666 dark gray
        }
        else
        {
            item.Status = "Standby";
            item.StatusBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)); // #E0E0E0 light gray
        }
    }

    private void OnOptimizationApplied(object? sender, OptimizationAppliedEventArgs e)
    {
        RefreshStatuses();
    }

    private void OnOptimizationReverted(object? sender, OptimizationRevertedEventArgs e)
    {
        RefreshStatuses();
    }

    /// <summary>
    /// Unsubscribes from engine events. Call when the page is unloaded.
    /// </summary>
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

/// <summary>
/// A named category group containing a list of optimization items.
/// </summary>
public class OptimizationGroup : INotifyPropertyChanged
{
    private string _categoryName = "";
    private string _categoryDescription = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Display name for the category (e.g., "Core v1.0").
    /// </summary>
    public string CategoryName
    {
        get => _categoryName;
        set { _categoryName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Brief description of what this category does.
    /// </summary>
    public string CategoryDescription
    {
        get => _categoryDescription;
        set { _categoryDescription = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Category color brush for the left border strip.
    /// Core = #4ADE80 green, Competitive = #A78BFA purple, Advisory = #FBBF24 yellow.
    /// </summary>
    public Brush CategoryColor
    {
        get => _categoryColor;
        set { _categoryColor = value; OnPropertyChanged(); }
    }
    private Brush _categoryColor = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));

    /// <summary>
    /// Optimization items belonging to this category.
    /// </summary>
    public ObservableCollection<OptimizationItem> Items { get; } = new();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Display model for a single optimization in the categorized list.
/// Status and brush are updated live from IOptimization state via RefreshStatuses().
/// </summary>
public class OptimizationItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _description = "";
    private string _status = "Standby";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private bool _isAvailable = true;
    private bool _isApplied;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Optimization display name from IOptimization.Name.
    /// </summary>
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Optimization description from IOptimization.Description.
    /// </summary>
    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Current status text: "Applied", "Standby", or "Unavailable".
    /// </summary>
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Status indicator color brush.
    /// Applied = green (#4ADE80), Standby = light gray (#E0E0E0), Unavailable = dark gray (#666666).
    /// </summary>
    public Brush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether the optimization is available on this system (from IOptimization.IsAvailable).
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set { _isAvailable = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether the optimization is currently active (from IOptimization.IsApplied).
    /// </summary>
    public bool IsApplied
    {
        get => _isApplied;
        set { _isApplied = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
