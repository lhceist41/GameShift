using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

public class HomeFeedTests
{
    private static HomeFeedGate Running()
    {
        var gate = new HomeFeedGate();
        gate.SetVisible(true);
        Assert.Equal(FeedChange.Resume, gate.SetLoaded(true));
        return gate;
    }

    [Fact]
    public void NotLoaded_NeverRuns()
    {
        var gate = new HomeFeedGate();
        Assert.Equal(FeedChange.None, gate.SetVisible(true));
        Assert.Equal(FeedChange.None, gate.SetMinimized(false));
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public void HiddenToTheTray_StopsOnce_AndShowingResumesOnce()
    {
        var gate = Running();
        Assert.Equal(FeedChange.Stop, gate.SetVisible(false));
        Assert.Equal(FeedChange.None, gate.SetVisible(false));
        Assert.Equal(FeedChange.Resume, gate.SetVisible(true));
        Assert.Equal(FeedChange.None, gate.SetVisible(true));
    }

    [Fact]
    public void Minimized_Stops_AndRestoredResumes()
    {
        var gate = Running();
        Assert.Equal(FeedChange.Stop, gate.SetMinimized(true));
        Assert.Equal(FeedChange.Resume, gate.SetMinimized(false));
    }

    [Fact]
    public void MinimizeToTray_StaysStoppedUntilTheWindowIsShownAgain()
    {
        // The window raises Minimized, hides, then resets its state to Normal while hidden.
        var gate = Running();
        Assert.Equal(FeedChange.Stop, gate.SetMinimized(true));
        Assert.Equal(FeedChange.None, gate.SetVisible(false));
        Assert.Equal(FeedChange.None, gate.SetMinimized(false));
        Assert.False(gate.IsRunning);
        Assert.Equal(FeedChange.Resume, gate.SetVisible(true));
    }

    [Fact]
    public void NavigatedAway_Stops_AndBackResumes()
    {
        var gate = Running();
        Assert.Equal(FeedChange.Stop, gate.SetLoaded(false));
        Assert.Equal(FeedChange.Resume, gate.SetLoaded(true));
    }

    [Fact]
    public void LoadedWhileHidden_WaitsForTheWindow()
    {
        var gate = new HomeFeedGate();
        gate.SetVisible(false);
        Assert.Equal(FeedChange.None, gate.SetLoaded(true));
        Assert.Equal(FeedChange.Resume, gate.SetVisible(true));
    }

    [Theory]
    [InlineData(false, false, HomeButtonAction.QuickOptimize)]
    [InlineData(true, true, HomeButtonAction.EndSession)]
    [InlineData(true, false, HomeButtonAction.RefreshOnly)]
    [InlineData(false, true, HomeButtonAction.RefreshOnly)]
    public void Button_ActsOnlyWhenItsLabelMatchesTheSession(bool labelSaysEndSession, bool sessionActive, HomeButtonAction expected)
    {
        Assert.Equal(expected, HomeButton.OnClick(labelSaysEndSession, sessionActive));
    }
}
