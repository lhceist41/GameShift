using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>
/// Decides the end-of-session toast. It shows when the game session's own deactivation arrives,
/// whatever ended the session (the game exiting, the user ending it, pausing monitoring), with that
/// session's duration and DPC load; never for a Quick Optimize session or one ended by GameShift
/// exiting. Not thread-safe; the caller serializes calls.
/// </summary>
public sealed class SessionToastTracker
{
    private DateTime? _key;
    private bool _exiting;
    private double _peakDpc;
    private double _dpcSum;
    private int _dpcSampleCount;

    /// <summary>A toast ready to show: the game session's figures and the deactivation that ended it.</summary>
    /// <param name="Duration">From the start of the session's activation to the end of its deactivation.</param>
    public sealed record Toast(string GameName, TimeSpan Duration, double AvgDpc, double PeakDpc, SessionOutcome Deactivation);

    /// <param name="isGameSession">
    /// Whether a game session raised this activation. A Quick Optimize run is not one, so it neither
    /// replaces the game session's key nor resets its DPC figures.
    /// </param>
    public void OnActivation(SessionOutcome activation, bool isGameSession)
    {
        if (!isGameSession || activation.StartedUtc is null)
            return;

        _key = activation.StartedUtc;
        ResetDpc();
    }

    /// <summary>Counts a DPC sample toward the open game session; ignored without one.</summary>
    public void OnLatencySampled(double latencyMicroseconds)
    {
        if (_key is null)
            return;

        if (latencyMicroseconds > _peakDpc)
            _peakDpc = latencyMicroseconds;
        _dpcSum += latencyMicroseconds;
        _dpcSampleCount++;
    }

    /// <summary>GameShift is exiting: the session it ends gets no toast.</summary>
    public void OnExiting() => _exiting = true;

    /// <summary>
    /// Returns the toast for the deactivation of the game session's activation, once. Any other
    /// deactivation returns null and leaves the game session's figures as they are.
    /// </summary>
    public Toast? OnDeactivation(SessionOutcome deactivation)
    {
        if (_key is not { } started || deactivation.StartedUtc != started || deactivation.EndedUtc is not { } ended)
            return null;

        var toast = new Toast(
            string.IsNullOrWhiteSpace(deactivation.GameName) ? "Unknown Game" : deactivation.GameName,
            ended - started,
            _dpcSampleCount > 0 ? _dpcSum / _dpcSampleCount : 0,
            _peakDpc,
            deactivation);

        _key = null;
        ResetDpc();
        return _exiting ? null : toast;
    }

    private void ResetDpc()
    {
        _peakDpc = 0;
        _dpcSum = 0;
        _dpcSampleCount = 0;
    }
}
