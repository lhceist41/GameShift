using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShift.App.Services;
using GameShift.App.ViewModels;
using GameShift.Core.Presentation;
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
        PopulateProfilesList();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SaveSettings();
    }

    private async void OnReEnableVbsClicked(object sender, RoutedEventArgs e)
    {
        var choice = await RestartConfirm.AskAsync(this, RestartPrompts.TurnOnMemoryIntegrity());
        if (choice == RestartChoice.ApplyAndRestartNow)
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

    private void PopulateProfilesList()
    {
        ProfilesList.Items.Clear();

        foreach (var profile in BuiltInProfiles.GetAll())
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

            mainStack.Children.Add(new TextBlock
            {
                Text = profile.DisplayName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = (Brush)FindResource("GS.Text.Primary"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Process names
            mainStack.Children.Add(new TextBlock
            {
                Text = $"Processes: {string.Join(", ", profile.ProcessNames)}",
                FontSize = 11,
                Foreground = (Brush)FindResource("GS.Text.Secondary"),
                Margin = new Thickness(0, 0, 0, 2)
            });

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
