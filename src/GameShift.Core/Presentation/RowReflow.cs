namespace GameShift.Core.Presentation;

/// <summary>When a list row puts its badge and switch under its title instead of beside it.</summary>
public static class RowReflow
{
    /// <summary>
    /// Below this row width (inside the row's padding) the badge and switch move under the title.
    /// Beside the title they take about 220 DIP, so this keeps at least about 340 DIP for the title.
    /// </summary>
    public const double StackBelowWidth = 560;

    /// <summary>A width of 0, NaN or infinity (not laid out yet, or unconstrained) keeps the row side by side.</summary>
    public static bool Stacks(double rowWidth) =>
        rowWidth > 0 && rowWidth < StackBelowWidth;
}
