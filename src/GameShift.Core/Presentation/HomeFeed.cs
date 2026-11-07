namespace GameShift.Core.Presentation;

public enum FeedChange
{
    None,
    Stop,
    Resume,
}

/// <summary>
/// Whether Home's live feed (its refresh timer and the tiles' sample subscriptions) runs: only while
/// the page is loaded, visible and its window is not minimized. Each change of that answer is
/// reported once, so a subscription is never added twice and a resume refreshes once.
/// </summary>
public sealed class HomeFeedGate
{
    private bool _loaded;
    private bool _visible;
    private bool _minimized;

    public bool IsRunning { get; private set; }

    public FeedChange SetLoaded(bool loaded)
    {
        _loaded = loaded;
        return Apply();
    }

    public FeedChange SetVisible(bool visible)
    {
        _visible = visible;
        return Apply();
    }

    public FeedChange SetMinimized(bool minimized)
    {
        _minimized = minimized;
        return Apply();
    }

    private FeedChange Apply()
    {
        bool run = _loaded && _visible && !_minimized;
        if (run == IsRunning) return FeedChange.None;
        IsRunning = run;
        return run ? FeedChange.Resume : FeedChange.Stop;
    }
}

public enum HomeButtonAction
{
    QuickOptimize,
    EndSession,
    RefreshOnly,
}

/// <summary>What a click on Home's "Optimize now" / "End session" button does.</summary>
public static class HomeButton
{
    /// <summary>
    /// The label can lag the session by one refresh. When it disagrees with the session the click
    /// only refreshes, so a stale "End session" never starts Quick Optimize and a stale
    /// "Optimize now" never ends a game's session.
    /// </summary>
    public static HomeButtonAction OnClick(bool labelSaysEndSession, bool sessionActive)
    {
        if (labelSaysEndSession != sessionActive) return HomeButtonAction.RefreshOnly;
        return sessionActive ? HomeButtonAction.EndSession : HomeButtonAction.QuickOptimize;
    }
}
