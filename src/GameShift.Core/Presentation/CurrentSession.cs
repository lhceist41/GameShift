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
    public static SessionOutcome? Select(SessionOutcome? lastOutcome, DateTime requestedUtc)
    {
        if (lastOutcome is not { EndedUtc: null, StartedUtc: { } started })
            return null;

        return started >= requestedUtc ? lastOutcome : null;
    }
}
