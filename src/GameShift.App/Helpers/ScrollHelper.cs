using System.Windows;

namespace GameShift.App.Helpers;

/// <summary>
/// Marker attached property for mouse wheel scrolling in pages hosted inside WPF-UI NavigationView.
/// NavigationView's internal event handling swallows mouse wheel events before they reach
/// page-level ScrollViewers.
///
/// Attach FixScrolling="True" to any ScrollViewer to mark it as the scroll target.
/// The actual mouse wheel handling is done by MainWindow.OnPreviewMouseWheel, which
/// walks up the visual tree from the element under the mouse to find the nearest
/// ScrollViewer with this property set, then scrolls it.
/// </summary>
public static class ScrollHelper
{
    public static readonly DependencyProperty FixScrollingProperty =
        DependencyProperty.RegisterAttached(
            "FixScrolling",
            typeof(bool),
            typeof(ScrollHelper),
            new PropertyMetadata(false));

    public static bool GetFixScrolling(DependencyObject obj) =>
        (bool)obj.GetValue(FixScrollingProperty);

    public static void SetFixScrolling(DependencyObject obj, bool value) =>
        obj.SetValue(FixScrollingProperty, value);
}
