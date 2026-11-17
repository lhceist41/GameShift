using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GameShift.App.Views;
using GameShift.Core.Presentation;
using Serilog;
using WpfUi = Wpf.Ui.Controls;

namespace GameShift.App.Services;

/// <summary>
/// Shows the one confirm dialog every restart or boot action goes through: Cancel is the default
/// and focused button, the primary button carries a power icon and a warning outline.
/// </summary>
internal static class RestartConfirm
{
    public static async Task<RestartChoice> AskAsync(DependencyObject owner, RestartPrompt prompt)
    {
        if (Window.GetWindow(owner) is not MainWindow { DialogHostPresenter: { } host })
        {
            Log.Warning("Restart confirm: no dialog host for {Title}, nothing was changed", prompt.Title);
            return RestartChoice.Cancel;
        }

        var dialog = new WpfUi.ContentDialog(host)
        {
            Title = BuildTitle(prompt.Title),
            Content = BuildContent(RestartDialogText.For(prompt)),
            PrimaryButtonText = prompt.PrimaryText,
            PrimaryButtonIcon = new WpfUi.SymbolIcon { Symbol = WpfUi.SymbolRegular.Power20 },
            // The caution fill stays as the fallback should the outline below not find its button.
            PrimaryButtonAppearance = WpfUi.ControlAppearance.Caution,
            SecondaryButtonText = prompt.LaterText ?? string.Empty,
            IsSecondaryButtonEnabled = prompt.AllowsLater,
            CloseButtonText = RestartPrompt.CancelText,
            DefaultButton = WpfUi.ContentDialogButton.Close,
        };
        dialog.Loaded += (_, _) =>
        {
            if (FindDialogButton(dialog, WpfUi.ContentDialogButton.Primary) is { } primary)
                UseWarningOutline(primary);
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusCancel(dialog));
        };

        var result = await dialog.ShowAsync();
        return prompt.Choose(result switch
        {
            WpfUi.ContentDialogResult.Primary => RestartDialogButton.Primary,
            WpfUi.ContentDialogResult.Secondary => RestartDialogButton.Secondary,
            _ => RestartDialogButton.None,
        });
    }

    private static UIElement BuildTitle(string title)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var icon = new WpfUi.SymbolIcon
        {
            Symbol = WpfUi.SymbolRegular.Power20,
            FontSize = 20,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Control.ForegroundProperty, "GS.State.Warning");
        row.Children.Add(icon);

        var text = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static UIElement BuildContent(RestartDialogText rows)
    {
        var grid = new Grid { MaxWidth = 460 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        if (rows.ShowsWhatChanges)
        {
            var value = new StackPanel();
            if (rows.WhatChanges.Length > 0)
                value.Children.Add(Paragraph(rows.WhatChanges));
            if (!string.IsNullOrEmpty(rows.Command))
            {
                var command = Paragraph(rows.Command);
                command.FontFamily = new FontFamily("Consolas");
                value.Children.Add(command);
            }
            AddRow(grid, RestartDialogText.WhatChangesLabel, value);
        }
        if (rows.ShowsRestart)
            AddRow(grid, RestartDialogText.RestartLabel, Paragraph(rows.Restart));
        AddRow(grid, RestartDialogText.HowToUndoLabel, Paragraph(rows.HowToUndo));
        return grid;
    }

    private static void AddRow(Grid grid, string label, UIElement value)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = Paragraph(label);
        heading.FontWeight = FontWeights.SemiBold;
        heading.TextWrapping = TextWrapping.NoWrap;
        heading.Margin = new Thickness(0, 0, 16, 10);
        Grid.SetRow(heading, row);
        grid.Children.Add(heading);

        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }

    private static TextBlock Paragraph(string text) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10),
        };

    // ContentDialog has no outline appearance, so the template's primary button gets local brushes,
    // which win over the brushes its Caution appearance sets through style triggers.
    private static void UseWarningOutline(WpfUi.Button button)
    {
        button.Background = Brushes.Transparent;
        button.SetResourceReference(Control.ForegroundProperty, "GS.Fg.Primary");
        button.SetResourceReference(WpfUi.Button.PressedForegroundProperty, "GS.Fg.Primary");
        button.SetResourceReference(Control.BorderBrushProperty, "GS.State.Warning");
        button.SetResourceReference(WpfUi.Button.MouseOverBorderBrushProperty, "GS.State.Warning");
        button.SetResourceReference(WpfUi.Button.PressedBorderBrushProperty, "GS.State.Warning");
        button.SetResourceReference(WpfUi.Button.MouseOverBackgroundProperty, "GS.Tint.Warning");
        button.SetResourceReference(WpfUi.Button.PressedBackgroundProperty, "GS.Tint.Warning");
    }

    private static WpfUi.Button? FindDialogButton(DependencyObject root, WpfUi.ContentDialogButton which)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is WpfUi.Button { CommandParameter: WpfUi.ContentDialogButton parameter } button && parameter == which)
                return button;
            if (FindDialogButton(child, which) is { } found)
                return found;
        }
        return null;
    }

    // Focus is set explicitly rather than relying on how the dialog template treats DefaultButton.
    private static bool FocusCancel(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase { Content: string text } button && text == RestartPrompt.CancelText)
                return button.Focus();
            if (FocusCancel(child))
                return true;
        }
        return false;
    }
}
