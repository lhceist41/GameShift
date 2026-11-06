using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

public class ShellLayoutTests
{
    private static NoticeSeverity[] Shown(int count) =>
        Enumerable.Repeat(NoticeSeverity.Info, count).ToArray();

    [Theory]
    [InlineData(1280, false)]
    [InlineData(1000, false)]
    [InlineData(999, true)]
    [InlineData(780, true)]
    public void IconRail_OnlyBelowItsThreshold(double width, bool expected)
    {
        Assert.Equal(expected, ShellLayout.UseIconRail(width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void WindowNotLaidOut_KeepsTheFullPaneAndUnfoldedNotices(double width)
    {
        Assert.False(ShellLayout.UseIconRail(width));
        Assert.False(ShellLayout.FoldNotices(width));
        Assert.Equal(
            new NoticeAreaView(false, true, "3 notices", NoticeSeverity.Info),
            ShellLayout.Notices(Shown(3), width, expanded: false));
    }

    [Fact]
    public void NoticesFold_BelowANarrowerWidthThanTheRail()
    {
        Assert.True(ShellLayout.FoldNoticesBelowWidth < ShellLayout.IconRailBelowWidth);
        Assert.False(ShellLayout.FoldNotices(860));
        Assert.True(ShellLayout.FoldNotices(859));
        // Between the two thresholds the pane is already a rail but the notices still show.
        Assert.True(ShellLayout.UseIconRail(900));
        Assert.False(ShellLayout.FoldNotices(900));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void TwoOrMoreNotices_OnANarrowWindow_FoldIntoTheSummary(int count)
    {
        var view = ShellLayout.Notices(Shown(count), 800, expanded: false);

        Assert.True(view.ShowSummary);
        Assert.False(view.ShowNotices);
        Assert.Equal($"{count} notices", view.SummaryText);
    }

    [Fact]
    public void OpenedSummary_ShowsTheNoticesAndKeepsTheSummaryToCloseThemAgain()
    {
        var view = ShellLayout.Notices(Shown(3), 800, expanded: true);

        Assert.True(view.ShowSummary);
        Assert.True(view.ShowNotices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FewerThanTwoNotices_NeverFold(int count)
    {
        var view = ShellLayout.Notices(Shown(count), 800, expanded: false);

        Assert.False(view.ShowSummary);
        Assert.True(view.ShowNotices);
    }

    [Fact]
    public void WideWindow_ShowsEveryNoticeWithoutASummary()
    {
        var view = ShellLayout.Notices(Shown(4), 1100, expanded: false);

        Assert.False(view.ShowSummary);
        Assert.True(view.ShowNotices);
    }

    [Theory]
    [InlineData(new[] { NoticeSeverity.Info, NoticeSeverity.Info }, NoticeSeverity.Info)]
    [InlineData(new[] { NoticeSeverity.Info, NoticeSeverity.Warning }, NoticeSeverity.Warning)]
    [InlineData(new[] { NoticeSeverity.Warning, NoticeSeverity.Error, NoticeSeverity.Info }, NoticeSeverity.Error)]
    [InlineData(new[] { NoticeSeverity.Error, NoticeSeverity.Warning }, NoticeSeverity.Error)]
    public void FoldBar_TakesTheMostUrgentFoldedNotice(NoticeSeverity[] shown, NoticeSeverity expected)
    {
        var view = ShellLayout.Notices(shown, 800, expanded: false);

        Assert.True(view.ShowSummary);
        Assert.Equal(expected, view.SummarySeverity);
    }

    [Fact]
    public void NoNotices_SummarySeverityIsInfo()
    {
        Assert.Equal(NoticeSeverity.Info, ShellLayout.Notices([], 800, expanded: false).SummarySeverity);
    }

    [Theory]
    [InlineData(1, "1 notice")]
    [InlineData(2, "2 notices")]
    [InlineData(1000, "1000 notices")]
    public void NoticeCountText_IsSingularForOneAndUsesNoGroupSeparator(int count, string expected)
    {
        Assert.Equal(expected, ShellLayout.NoticeCountText(count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoLastingChanges_AndEveryRecordRead_MeansNoNotice(int count)
    {
        Assert.Null(ShellLayout.LastingChangesNotice(count, someRecordsUnreadable: false));
    }

    [Fact]
    public void LastingChanges_AllRead_IsAnInfoNoticeWithTheCount()
    {
        var one = ShellLayout.LastingChangesNotice(1, someRecordsUnreadable: false)!;
        var three = ShellLayout.LastingChangesNotice(3, someRecordsUnreadable: false)!;

        Assert.Equal(NoticeSeverity.Info, one.Severity);
        Assert.StartsWith("1 lasting change from GameShift is in place.", one.Message);
        Assert.Equal(NoticeSeverity.Info, three.Severity);
        Assert.StartsWith("3 lasting changes from GameShift are in place.", three.Message);
    }

    [Fact]
    public void UnreadableRecords_WithNothingListed_StillWarn()
    {
        var notice = ShellLayout.LastingChangesNotice(0, someRecordsUnreadable: true);

        Assert.NotNull(notice);
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.Equal(
            "GameShift could not read every lasting change it recorded. Review them on Lasting changes.",
            notice.Message);
    }

    [Theory]
    [InlineData(1, "1 lasting change from GameShift is in place, and")]
    [InlineData(4, "4 lasting changes from GameShift are in place, and")]
    public void UnreadableRecords_WithChangesListed_WarnThatThereMayBeMore(int count, string start)
    {
        var notice = ShellLayout.LastingChangesNotice(count, someRecordsUnreadable: true)!;

        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.StartsWith(start, notice.Message);
        Assert.Contains("could not read every record, so there may be more", notice.Message);
        Assert.EndsWith("Review them on Lasting changes.", notice.Message);
    }
}
