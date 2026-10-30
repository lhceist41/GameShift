using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShift.App.ViewModels;
using GameShift.Core.Config;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.SystemTweaks;
using GameShift.Core.GameProfiles;

namespace GameShift.App.Views.Pages;

/// <summary>
/// Settings page: global application preferences.
/// Content migrated from SettingsWindow. ViewModel created on Loaded via App static properties.
/// </summary>
public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext != null) return; // already set

        var vm = new SettingsViewModel();
        vm.SetVbsHvciToggle(App.Services.VbsToggle);

        vm.AdvancedModeChanged += advanced =>
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.ApplyAdvancedMode(advanced);
        };

        DataContext = vm;
        vm.LoadSettings();
        PopulateTweaksList();
        PopulateProfilesList();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SaveSettings();
    }

    private void OnReEnableVbsClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will re-enable Memory Integrity (VBS/HVCI) and schedule a reboot in 30 seconds.\n\nContinue?",
            "Re-enable Memory Integrity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            if ((DataContext as SettingsViewModel)?.ReEnableVbsHvci() == true)
            {
                GameShift.Core.Optimization.VbsHvciToggle.ScheduleReboot();
            }
            else
            {
                MessageBox.Show("Failed to re-enable VBS/HVCI. Check logs for details.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.ExportSettings();
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.ImportSettings();
    }

    private void PopulateTweaksList()
    {
        var mgr = App.Services.TweaksMgr;
        if (mgr == null) return;

        TweaksList.Items.Clear();

        string? lastCategory = null;
        foreach (var tweak in mgr.OfferedTweaks)
        {
            // Category header
            if (tweak.Category != lastCategory)
            {
                lastCategory = tweak.Category;
                var header = new TextBlock
                {
                    Text = tweak.Category.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("GS.Text.Secondary"),
                    Margin = new Thickness(0, 8, 0, 4)
                };
                TweaksList.Items.Add(header);
            }

            var status = mgr.GetTweakStatus(tweak);
            var isApplied = tweak.DetectIsApplied();
            var isGameShift = status == "Applied (by GameShift)";

            var card = new Border
            {
                Background = (Brush)FindResource("GS.Surface.Card"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = (Brush)FindResource("GS.Surface.Border"),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel();
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            namePanel.Children.Add(new TextBlock
            {
                Text = tweak.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("GS.Text.Primary"),
                FontSize = 13
            });
            if (tweak.RequiresReboot)
            {
                namePanel.Children.Add(new TextBlock
                {
                    Text = " \u27F3 Reboot",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("GS.Text.Secondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                });
            }
            textStack.Children.Add(namePanel);
            textStack.Children.Add(new TextBlock
            {
                Text = tweak.Description,
                FontSize = 11,
                Foreground = (Brush)FindResource("GS.Text.Secondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 12, 0)
            });
            textStack.Children.Add(new TextBlock
            {
                Text = status,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = isApplied
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
                    : (Brush)FindResource("GS.Text.Secondary"),
                Margin = new Thickness(0, 4, 0, 0)
            });

            Grid.SetColumn(textStack, 0);
            grid.Children.Add(textStack);

            var btn = new Button
            {
                Content = isApplied ? (isGameShift ? "Revert" : "Applied") : "Apply",
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !isApplied || isGameShift,
                Tag = tweak
            };

            if (isApplied && !isGameShift)
            {
                btn.Background = (Brush)FindResource("GS.Surface.Base");
                btn.Foreground = (Brush)FindResource("GS.Text.Secondary");
            }
            else if (isApplied && isGameShift)
            {
                btn.Background = (Brush)FindResource("GS.Surface.Base");
                btn.Foreground = (Brush)FindResource("GS.Text.Primary");
            }
            else
            {
                btn.Background = (Brush)FindResource("GS.Accent.Primary");
                btn.Foreground = Brushes.White;
            }
            btn.BorderThickness = new Thickness(0);
            btn.Click += OnTweakButtonClicked;

            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            card.Child = grid;
            TweaksList.Items.Add(card);
        }
    }

    private void OnTweakButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ISystemTweak tweak) return;
        var mgr = App.Services.TweaksMgr;
        if (mgr == null) return;

        var status = mgr.GetTweakStatus(tweak);
        if (status == "Applied (by GameShift)")
        {
            mgr.RevertTweak(tweak);
        }
        else if (tweak is GameShift.Core.SystemTweaks.Tweaks.DisableHags hags)
        {
            hags.EvaluateRecommendation();
            // Warn when disabling on Frame Gen capable GPU
            if (hags.IsFrameGenCapable && hags.IsHagsEnabled)
            {
                var result = MessageBox.Show(
                    "Your GPU supports Frame Generation (DLSS FG / AFMF), which requires HAGS to be enabled. " +
                    "Disabling HAGS will prevent Frame Generation from working.\n\nAre you sure?",
                    "Frame Generation Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
            mgr.ApplyTweak(tweak);
        }
        else
        {
            mgr.ApplyTweak(tweak);
        }

        // Refresh the list
        PopulateTweaksList();
    }

    private async void OnRestoreAllTweaksClicked(object sender, RoutedEventArgs e)
    {
        var mgr = App.Services.TweaksMgr;
        if (mgr == null) return;

        var inventory = LastingChangesInventory.CreateForThisMachine(
            mgr, App.Services.DpcFix ?? new DpcFixEngine(), App.Services.VbsToggle ?? new VbsHvciToggle());
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            var list = await Task.Run(inventory.List);
            var warnings = list.Warnings.Count > 0 ? "\n\n" + string.Join("\n", list.Warnings) : "";

            if (list.Items.Count == 0)
            {
                MessageBox.Show("GameShift found no lasting changes to undo." + warnings,
                    "Restore All", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var toUndo = new List<LastingChange>();
            var separatelyConfirmed = new List<LastingChange>();

            if (list.Recorded.Count > 0 && MessageBox.Show(
                    "GameShift will undo these changes it recorded:\n\n" +
                    LastingChangesInventory.DescribeItems(list.Recorded) +
                    "\nGameShift does not restart your PC; changes that need a restart finish after you restart." +
                    warnings + "\n\nUndo these changes?",
                    "Restore All", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                toUndo.AddRange(list.Recorded);
            }

            foreach (var group in list.NeedingOwnConfirmation.GroupBy(i => i.Kind))
            {
                var intro = group.Key == LastingChangeKind.CpuCoreReservation
                    ? "A CPU core reservation is set on this PC. Earlier versions of GameShift's DPC Doctor " +
                      "could set one, but so can other tools, and GameShift does not record who set it.\n\n"
                    : "These startup apps are turned off by a \"~\" in front of their name. GameShift turns " +
                      "startup apps off this way, but other tools can too, and GameShift does not record " +
                      "which ones it turned off.\n\n";
                var question = group.Key == LastingChangeKind.CpuCoreReservation
                    ? "Remove the reservation?"
                    : "Turn these startup apps back on?";

                if (MessageBox.Show(intro + LastingChangesInventory.DescribeItems(group) + "\n" + question,
                        "Restore All", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    toUndo.AddRange(group);
                    separatelyConfirmed.AddRange(group);
                }
            }

            if (toUndo.Count == 0) return;

            var results = await Task.Run(() => inventory.Undo(toUndo, separatelyConfirmed));
            PopulateTweaksList();

            int undone = results.Count(r => r.Outcome == LastingChangeOutcome.Undone);
            int failed = results.Count(r => r.Outcome == LastingChangeOutcome.Failed);
            MessageBox.Show(LastingChangesInventory.DescribeResults(results), "Restore All",
                MessageBoxButton.OK, failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            (DataContext as SettingsViewModel)!.StatusMessage = failed > 0
                ? $"{undone} lasting changes undone, {failed} failed."
                : $"{undone} lasting changes undone.";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[Settings] Restore All failed");
            MessageBox.Show($"Restore All stopped with an error: {ex.Message}", "Restore All",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    private void PopulateProfilesList()
    {
        var mgr = App.Services.GameProfileMgr;
        if (mgr == null) return;

        ProfilesList.Items.Clear();

        foreach (var profile in mgr.GetAllProfiles())
        {
            var card = new Border
            {
                Background = (Brush)FindResource("GS.Surface.Card"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = (Brush)FindResource("GS.Surface.Border"),
                BorderThickness = new Thickness(1)
            };

            var mainStack = new StackPanel();

            // Header with name and priority
            var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = profile.DisplayName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = (Brush)FindResource("GS.Text.Primary")
            });
            var priorityText = new TextBlock
            {
                Text = $"Priority: {profile.GamePriority}",
                FontSize = 11,
                Foreground = (Brush)FindResource("GS.Text.Secondary"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(priorityText, Dock.Right);
            headerPanel.Children.Add(priorityText);
            mainStack.Children.Add(headerPanel);

            // Process names
            mainStack.Children.Add(new TextBlock
            {
                Text = $"Processes: {string.Join(", ", profile.ProcessNames)}",
                FontSize = 11,
                Foreground = (Brush)FindResource("GS.Text.Secondary"),
                Margin = new Thickness(0, 0, 0, 2)
            });

            // Features summary
            var features = new List<string>();
            if (profile.IntelHybridPCoreOnly) features.Add("P-Core Only");
            if (profile.LauncherPriority != null) features.Add($"Launcher \u2192 {profile.LauncherPriority}");
            if (features.Count > 0)
            {
                mainStack.Children.Add(new TextBlock
                {
                    Text = string.Join(" \u00B7 ", features),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("GS.Accent.Primary"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            // Notes (collapsible via Expander)
            if (profile.Notes.Length > 0)
            {
                var expander = new Expander
                {
                    Header = $"{profile.Notes.Length} optimization notes",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("GS.Text.Secondary"),
                    Margin = new Thickness(0, 6, 0, 0),
                    IsExpanded = false
                };

                var notesStack = new StackPanel();
                foreach (var note in profile.Notes)
                {
                    notesStack.Children.Add(new TextBlock
                    {
                        Text = $"\u2022 {note}",
                        FontSize = 11,
                        Foreground = (Brush)FindResource("GS.Text.Secondary"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                }
                expander.Content = notesStack;
                mainStack.Children.Add(expander);
            }

            // Recommended tweaks
            if (profile.RecommendedTweaks.Length > 0)
            {
                var tweaksPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                tweaksPanel.Children.Add(new TextBlock
                {
                    Text = "Tweaks: ",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("GS.Text.Secondary"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                foreach (var tweakName in profile.RecommendedTweaks)
                {
                    var tweakMgr = App.Services.TweaksMgr;
                    var tweak = tweakMgr?.GetTweakByClassName(tweakName);
                    bool applied = tweak?.DetectIsApplied() == true;

                    var badge = new Border
                    {
                        Background = applied
                            ? (Brush)new SolidColorBrush(Color.FromArgb(0x30, 0x4A, 0xDE, 0x80))
                            : (Brush)new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(2)
                    };
                    badge.Child = new TextBlock
                    {
                        Text = tweakName,
                        FontSize = 10,
                        Foreground = applied
                            ? (Brush)new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
                            : (Brush)FindResource("GS.Text.Secondary")
                    };
                    tweaksPanel.Children.Add(badge);
                }
                mainStack.Children.Add(tweaksPanel);
            }

            card.Child = mainStack;
            ProfilesList.Items.Add(card);
        }
    }
}
