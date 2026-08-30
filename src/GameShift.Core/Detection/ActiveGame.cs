namespace GameShift.Core.Detection;

/// <summary>
/// One tracked game process: the game identity it was matched to, plus the runtime evidence
/// actually observed for that PID.
///
/// The runtime evidence is kept here rather than written back into <see cref="GameInfo"/> because
/// <see cref="GameInfo"/> is persistent/scanned metadata. A launcher entry legitimately carries an
/// empty <see cref="GameInfo.ExecutablePath"/> (Steam/Xbox know only an install directory), and a
/// name-only match legitimately has no path at all - so liveness and event payloads must read this
/// record, not the persistent one.
/// </summary>
/// <param name="Game">The matched game identity. Never mutated with runtime-only evidence.</param>
/// <param name="ObservedProcessName">
/// Filename with extension of the image actually observed for this PID. Always populated - this is
/// what the liveness sweep compares against to detect PID reuse.
/// </param>
/// <param name="ObservedExecutablePath">
/// Rooted full path of the observed image, or null when the process was proven alive but its path
/// could not be read. Never a bare filename.
/// </param>
internal sealed record ActiveGame(
    GameInfo Game,
    string ObservedProcessName,
    string? ObservedExecutablePath)
{
    /// <summary>
    /// Structural enforcement of the "always populated" contract above. An empty observed name
    /// would leave <see cref="GameDetector.IsTrackedGameGone"/> with nothing to compare against, so
    /// its PID-reuse check would return "still alive" forever: a missed stop event would then keep
    /// the game - and its optimizations - tracked for the rest of the session. Rejecting the value
    /// at construction keeps that state unreachable instead of relying on every call site to check.
    /// </summary>
    internal string ObservedProcessName { get; } =
        string.IsNullOrWhiteSpace(ObservedProcessName)
            ? throw new ArgumentException(
                "An active game must carry the process name observed for its PID.",
                nameof(ObservedProcessName))
            : ObservedProcessName;
}
