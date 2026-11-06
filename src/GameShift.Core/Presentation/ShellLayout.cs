using System.Globalization;

namespace GameShift.Core.Presentation;

/// <summary>How a Home notice is coloured. Declared from least to most urgent: the fold bar takes the maximum.</summary>
public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>What the Home notice area shows: the notices themselves, the "N notices" bar, or both.</summary>
/// <param name="SummarySeverity">The most urgent of the notices the bar folds.</param>
public sealed record NoticeAreaView(bool ShowSummary, bool ShowNotices, string SummaryText, NoticeSeverity SummarySeverity);

public sealed record HomeNotice(NoticeSeverity Severity, string Message);

/// <summary>
/// How the main window narrows, by window width in DIP. Pure: the window reports its width and
/// applies the result. Both thresholds live here and nowhere else.
/// </summary>
public static class ShellLayout
{
    /// <summary>Below this width the navigation pane becomes an icon rail.</summary>
    public const double IconRailBelowWidth = 1000;

    /// <summary>Below this width two or more Home notices fold into one "N notices" bar.</summary>
    public const double FoldNoticesBelowWidth = 860;

    /// <summary>A width of 0 means the window has not been laid out yet, so it keeps the full pane.</summary>
    public static bool UseIconRail(double windowWidth) =>
        windowWidth > 0 && windowWidth < IconRailBelowWidth;

    public static bool FoldNotices(double windowWidth) =>
        windowWidth > 0 && windowWidth < FoldNoticesBelowWidth;

    /// <param name="shown">The severity of each notice currently shown on Home.</param>
    /// <param name="windowWidth">The main window's width in DIP.</param>
    /// <param name="expanded">The user opened the folded notices.</param>
    public static NoticeAreaView Notices(IReadOnlyCollection<NoticeSeverity> shown, double windowWidth, bool expanded)
    {
        // A single notice stays as it is: folding it would hide it behind a click and save no room.
        bool fold = shown.Count >= 2 && FoldNotices(windowWidth);
        return new NoticeAreaView(
            ShowSummary: fold,
            ShowNotices: !fold || expanded,
            SummaryText: NoticeCountText(shown.Count),
            SummarySeverity: shown.Count == 0 ? NoticeSeverity.Info : shown.Max());
    }

    public static string NoticeCountText(int count) =>
        count == 1 ? "1 notice" : count.ToString(CultureInfo.InvariantCulture) + " notices";

    /// <summary>
    /// The Home notice about lasting changes, or null when there are none. Records that could not be
    /// read may hide changes, so they always get a warning, even when nothing could be listed.
    /// </summary>
    public static HomeNotice? LastingChangesNotice(int itemCount, bool someRecordsUnreadable)
    {
        if (someRecordsUnreadable)
        {
            return new HomeNotice(NoticeSeverity.Warning, itemCount switch
            {
                <= 0 => "GameShift could not read every lasting change it recorded. Review them on Lasting changes.",
                1 => "1 lasting change from GameShift is in place, and GameShift could not read every record, " +
                     "so there may be more. Review them on Lasting changes.",
                _ => itemCount.ToString(CultureInfo.InvariantCulture) +
                     " lasting changes from GameShift are in place, and GameShift could not read every record, " +
                     "so there may be more. Review them on Lasting changes.",
            });
        }

        return itemCount switch
        {
            <= 0 => null,
            1 => new HomeNotice(NoticeSeverity.Info,
                "1 lasting change from GameShift is in place. It stays after games close until you restore it."),
            _ => new HomeNotice(NoticeSeverity.Info, itemCount.ToString(CultureInfo.InvariantCulture) +
                " lasting changes from GameShift are in place. They stay after games close until you restore them."),
        };
    }
}
