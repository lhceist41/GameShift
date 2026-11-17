using System.Windows;
using System.Windows.Controls;
using GameShift.Core.Presentation;

namespace GameShift.App.Helpers;

/// <summary>
/// A list row with two children: the title block first, then the trailing part (badge, switch).
/// Wide, the trailing part sits on the right, both centred vertically; narrower than
/// <see cref="RowReflow.StackBelowWidth"/>, it moves under the title, left aligned.
/// </summary>
public sealed class ReflowRowPanel : Panel
{
    /// <summary>The gap between the title block and the trailing part when they are stacked.</summary>
    private const double StackedGap = 8;

    private bool _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        var title = Child(0);
        var trailing = Child(1);
        double width = availableSize.Width;

        _stacked = RowReflow.Stacks(width);
        trailing?.Measure(new Size(width, double.PositiveInfinity));
        var trailingSize = trailing?.DesiredSize ?? default;

        if (_stacked)
        {
            title?.Measure(new Size(width, double.PositiveInfinity));
            var titleSize = title?.DesiredSize ?? default;
            double gap = titleSize.Height > 0 && trailingSize.Height > 0 ? StackedGap : 0;
            return new Size(
                Math.Max(titleSize.Width, trailingSize.Width),
                titleSize.Height + gap + trailingSize.Height);
        }

        title?.Measure(new Size(Math.Max(0, width - trailingSize.Width), double.PositiveInfinity));
        var sideTitle = title?.DesiredSize ?? default;
        return new Size(
            sideTitle.Width + trailingSize.Width,
            Math.Max(sideTitle.Height, trailingSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var title = Child(0);
        var trailing = Child(1);
        var titleSize = title?.DesiredSize ?? default;
        var trailingSize = trailing?.DesiredSize ?? default;

        if (_stacked)
        {
            title?.Arrange(new Rect(0, 0, finalSize.Width, titleSize.Height));
            double gap = titleSize.Height > 0 && trailingSize.Height > 0 ? StackedGap : 0;
            trailing?.Arrange(new Rect(0, titleSize.Height + gap,
                Math.Min(trailingSize.Width, finalSize.Width), trailingSize.Height));
            return finalSize;
        }

        double trailingWidth = Math.Min(trailingSize.Width, finalSize.Width);
        title?.Arrange(new Rect(0, (finalSize.Height - titleSize.Height) / 2,
            Math.Max(0, finalSize.Width - trailingWidth), titleSize.Height));
        trailing?.Arrange(new Rect(finalSize.Width - trailingWidth, (finalSize.Height - trailingSize.Height) / 2,
            trailingWidth, trailingSize.Height));
        return finalSize;
    }

    private UIElement? Child(int index) => index < InternalChildren.Count ? InternalChildren[index] : null;
}
