using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>
/// Pairs the end-of-session toast with the deactivation that ended the game session's own
/// activation. The toast's figures (taken when the last game exits) and that deactivation arrive in
/// either order, so each waits here for the other. Not thread-safe; the caller serializes calls.
/// </summary>
/// <typeparam name="T">The game session's figures shown on the toast.</typeparam>
public sealed class SessionToastPairing<T> where T : class
{
    private DateTime? _key;
    private SessionOutcome? _deactivation;
    private T? _pending;

    /// <summary>A toast ready to show: the game session's figures and its own deactivation.</summary>
    public sealed record Ready(T Session, SessionOutcome Deactivation);

    /// <summary>
    /// A game started a new session. A toast still waiting with no game activation to pair with
    /// belongs to an earlier game and is dropped.
    /// </summary>
    public void OnGameStarted()
    {
        if (_key is null)
            _pending = null;
    }

    /// <param name="isGameSession">
    /// Whether a game session raised this activation. A Quick Optimize run during a game is not one,
    /// so it neither replaces the key nor clears the game's stored deactivation.
    /// </param>
    public void OnActivation(SessionOutcome activation, bool isGameSession)
    {
        if (!isGameSession)
            return;

        _key = activation.StartedUtc;
        if (_deactivation is not null && _deactivation.StartedUtc != _key)
            _deactivation = null;
    }

    /// <summary>Accepts only the deactivation of the game session's activation.</summary>
    public Ready? OnDeactivation(SessionOutcome deactivation)
    {
        if (_key is null || deactivation.EndedUtc is null || deactivation.StartedUtc != _key)
            return null;

        _deactivation = deactivation;
        return TryPair();
    }

    /// <param name="isGameSession">
    /// Whether a game session is still open. With none and no accepted deactivation, none is coming
    /// and the toast is skipped; otherwise it waits for the deactivation.
    /// </param>
    public Ready? OnAllGamesStopped(T session, bool isGameSession)
    {
        _pending = session;
        var ready = TryPair();
        if (ready is null && !isGameSession)
        {
            _pending = null;
            _key = null;
        }

        return ready;
    }

    private Ready? TryPair()
    {
        if (_pending is not { } session || _deactivation is not { } deactivation)
            return null;

        _pending = null;
        _deactivation = null;
        _key = null;
        return new Ready(session, deactivation);
    }
}
