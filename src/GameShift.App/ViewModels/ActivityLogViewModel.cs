using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace GameShift.App.ViewModels;

/// <summary>
/// ViewModel for ActivityLogPage — provides searchable, filterable view of all activity entries.
/// Sources data from DashboardViewModel.AllActivities (shared static collection).
/// Provides a full activity log with search by text and filter by event type.
/// </summary>
public class ActivityLogViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<ActivityEntry> _allEntries;
    private string _searchText = "";
    private string _selectedFilter = "All";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Text to search across activity descriptions. Empty string shows all entries.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); ApplyFilters(); }
    }

    /// <summary>
    /// Selected event type filter. Valid values: "All", "Games", "Optimizations".
    /// </summary>
    public string SelectedFilter
    {
        get => _selectedFilter;
        set { _selectedFilter = value; OnPropertyChanged(); ApplyFilters(); }
    }

    /// <summary>
    /// Filtered and sorted activity entries for display in the list.
    /// Ordered newest-first. Updated when SearchText, SelectedFilter, or source collection changes.
    /// </summary>
    public ObservableCollection<ActivityEntry> FilteredActivities { get; } = new();

    /// <summary>
    /// Available filter options for the type filter ComboBox.
    /// </summary>
    public string[] FilterOptions { get; } = { "All", "Games", "Optimizations" };

    /// <summary>
    /// Creates an ActivityLogViewModel connected to the provided activity collection.
    /// </summary>
    /// <param name="allEntries">The shared activity entries collection from DashboardViewModel.</param>
    public ActivityLogViewModel(ObservableCollection<ActivityEntry> allEntries)
    {
        _allEntries = allEntries;
        _allEntries.CollectionChanged += (s, e) => ApplyFilters();
        ApplyFilters();
    }

    /// <summary>
    /// Applies current search text and type filter to produce FilteredActivities.
    /// Dispatches to UI thread since it modifies an ObservableCollection.
    /// </summary>
    private void ApplyFilters()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            FilteredActivities.Clear();

            var filtered = _allEntries.AsEnumerable();

            // Type filter
            if (SelectedFilter == "Games")
                filtered = filtered.Where(a => a.Type.StartsWith("Game", StringComparison.Ordinal));
            else if (SelectedFilter == "Optimizations")
                filtered = filtered.Where(a => a.Type.StartsWith("Optimization", StringComparison.Ordinal));

            // Search filter
            if (!string.IsNullOrWhiteSpace(SearchText))
                filtered = filtered.Where(a =>
                    a.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            foreach (var entry in filtered.OrderByDescending(a => a.Timestamp))
                FilteredActivities.Add(entry);
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
