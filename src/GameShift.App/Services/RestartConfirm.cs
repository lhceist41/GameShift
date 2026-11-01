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
/// and focused button, the primary button uses the caution look.
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
            Title = prompt.Title,
            Content = BuildContent(prompt),
            PrimaryButtonText = prompt.PrimaryText,
            PrimaryButtonAppearance = WpfUi.ControlAppearance.Caution,
            SecondaryButtonText = prompt.LaterText ?? string.Empty,
            IsSecondaryButtonEnabled = prompt.AllowsLater,
            CloseButtonText = RestartPrompt.CancelText,
            DefaultButton = WpfUi.ContentDialogButton.Close,
        };
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusCancel(dialog));

        var result = await dialog.ShowAsync();
        return prompt.Choose(result switch
        {
            WpfUi.ContentDialogResult.Primary => RestartDialogButton.Primary,
            WpfUi.ContentDialogResult.Secondary => RestartDialogButton.Secondary,
            _ => RestartDialogButton.None,
        });
    }

    private static UIElement BuildContent(RestartPrompt prompt)
    {
        var panel = new StackPanel { MaxWidth = 460 };
        panel.Children.Add(Paragraph(prompt.WhatChanges));
        if (!string.IsNullOrEmpty(prompt.Command))
        {
            var command = Paragraph(prompt.Command);
            command.FontFamily = new FontFamily("Consolas");
            panel.Children.Add(command);
        }

        var undoHeading = Paragraph("How to undo");
        undoHeading.FontWeight = FontWeights.SemiBold;
        undoHeading.Margin = new Thickness(0, 4, 0, 2);
        panel.Children.Add(undoHeading);
        panel.Children.Add(Paragraph(prompt.HowToUndo));
        return panel;
    }

    private static TextBlock Paragraph(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

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
