using System.Windows;
using System.Windows.Controls;
using GameShift.Core.Presentation;

namespace GameShift.App.Views.Controls;

/// <summary>
/// A Home notice: severity icon and colour, title, message, and its action buttons as content.
/// </summary>
public partial class NoticeBar : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(NoticeBar), new PropertyMetadata(""));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(NoticeBar), new PropertyMetadata(""));

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(NoticeSeverity), typeof(NoticeBar), new PropertyMetadata(NoticeSeverity.Info));

    public NoticeBar()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public NoticeSeverity Severity
    {
        get => (NoticeSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }
}
