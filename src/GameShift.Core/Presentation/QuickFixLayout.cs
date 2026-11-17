namespace GameShift.Core.Presentation;

/// <summary>How many Latency Doctor quick fix cards share a row.</summary>
public static class QuickFixLayout
{
    public const double MinCardWidth = 220;

    /// <summary>The right and bottom margin each card keeps to its neighbours.</summary>
    public const double CardGap = 8;

    public const int MaxColumns = 3;

    /// <summary>
    /// As many columns as fit cards of at least <see cref="MinCardWidth"/>, one to
    /// <see cref="MaxColumns"/>, and never more than there are cards, so a short list fills the row.
    /// </summary>
    /// <param name="availableWidth">The width of the card grid, gaps included.</param>
    public static int Columns(double availableWidth, int cardCount)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
            return 1;

        int fit = (int)Math.Floor(availableWidth / (MinCardWidth + CardGap));
        return Math.Clamp(Math.Min(fit, cardCount), 1, MaxColumns);
    }
}
