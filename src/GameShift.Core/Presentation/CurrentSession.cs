using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>
/// Decides whether an outcome read from <see cref="OptimizationEngine.LastOutcome"/> belongs to the
/// session the caller is showing.
/// </summary>
public static class CurrentSession
{
    /// <summary>
    /// The activation outcome of the session requested at <paramref name="requestedUtc"/>, or null.
    /// An activation that throws before it finishes leaves the previous outcome in place, so an
    /// outcome that started before the request, or one that already ended, is never the current one.
    /// </summary>
    /// <param name="requestedUtc">
    /// <see cref="Detection.DetectionOrchestrator.SessionRequestedUtc"/>, read once. Null means no game
    /// session runs, and an activation that has not ended is then Quick Optimize's: every game session
    /// ends with a deactivation before the orchestrator clears its request time.
    /// </param>
    public static SessionOutcome? Select(SessionOutcome? lastOutcome, DateTime? requestedUtc)
    {
        if (lastOutcome is not { EndedUtc: null, StartedUtc: { } started })
            return null;

        return requestedUtc is not { } requested || started >= requested ? lastOutcome : null;
    }
}
