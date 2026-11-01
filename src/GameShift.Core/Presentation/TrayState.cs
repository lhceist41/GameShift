using System.Globalization;
using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>
/// The tray icon's states. Each has its own silhouette, so the state reads without its colour.
/// </summary>
public enum TrayIconShape
{
    Idle,
    Active,
    Paused,
    Warning,

    /// <summary>Reserved for a session whose revert failed; nothing resolves to it yet.</summary>
    Error,
}

/// <summary>
/// The tray icon set drawn for the taskbar behind it.
/// </summary>
public enum TrayIconTheme
{
    DarkTaskbar,
    LightTaskbar,
}

/// <summary>
/// What the tray shows is resolved from. <paramref name="LastDpcSpikeAge"/> is measured on a
/// monotonic clock, so a change of the system time cannot keep a notice up or hide it early.
/// </summary>
/// <param name="IsOptimizing">A game session is running.</param>
/// <param name="AppliedCount">Changes in effect now, from a game session or Quick Optimize.</param>
/// <param name="FailedCount">Changes that failed in the run whose changes are in effect.</param>
public readonly record struct TrayStateInput(
    bool IsOptimizing,
    bool IsPaused,
    int AppliedCount,
    int FailedCount,
    DateTimeOffset? LastDpcSpikeAt,
    TimeSpan LastDpcSpikeAge);

/// <summary>
/// The tray icon, tooltip and status menu line for one moment. Pure: the App only maps
/// <see cref="Shape"/> to an icon.
/// </summary>
public sealed record TrayState(TrayIconShape Shape, string ToolTip, string StatusText)
{
    /// <summary>How long a DPC spike stays in the tooltip without a new one.</summary>
    public static readonly TimeSpan DpcNoticeDuration = TimeSpan.FromMinutes(5);

    public static TrayState Resolve(TrayStateInput input)
    {
        // Pausing ends the session even while the game keeps running.
        if (input.IsPaused)
            return new TrayState(TrayIconShape.Paused, "GameShift - Paused", "GameShift: Paused");

        // Quick Optimize applies without a game session; its changes are just as active.
        if (!input.IsOptimizing && input.AppliedCount == 0)
            return new TrayState(TrayIconShape.Idle, "GameShift - Idle", "GameShift: Idle");

        TrayState state;
        if (input.FailedCount > 0)
        {
            var failed = Changes(input.FailedCount);
            var toolTip = input.AppliedCount > 0
                ? $"GameShift - {Changes(input.AppliedCount)} active, {input.FailedCount} failed"
                : $"GameShift - {failed} failed";
            state = new TrayState(TrayIconShape.Warning, toolTip, $"GameShift: {failed} failed");
        }
        else if (input.AppliedCount > 0)
        {
            var active = $"{Changes(input.AppliedCount)} active";
            state = new TrayState(TrayIconShape.Active, $"GameShift - {active}", $"GameShift: {active}");
        }
        else
        {
            // A session with nothing applied (yet, or any more while it ends) has changed nothing.
            state = new TrayState(TrayIconShape.Idle, "GameShift - No changes active", "GameShift: No changes active");
        }

        // A DPC spike is the drivers' doing, not a failed change, so it never replaces the icon.
        if (input.LastDpcSpikeAt is DateTimeOffset spikeAt && input.LastDpcSpikeAge < DpcNoticeDuration)
        {
            var line = "\nHigh DPC load at " + spikeAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            state = state with { ToolTip = state.ToolTip + line, StatusText = state.StatusText + line };
        }

        return state;
    }

    private static string Changes(int count) => count == 1 ? "1 change" : $"{count} changes";
}

/// <summary>
/// Gathers the inputs of <see cref="TrayState"/>. Failures come from the engine's latest outcome, so
/// each belongs to the run that produced it; the last DPC spike is kept here and forgotten once the
/// session is over. Thread-safe: the DPC monitor reports from its own thread while the UI resolves.
/// </summary>
public sealed class TrayStatusTracker
{
    private readonly Func<bool> _isOptimizing;
    private readonly Func<bool> _isPaused;
    private readonly Func<int> _appliedCount;
    private readonly Func<SessionOutcome?> _lastOutcome;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private DateTimeOffset? _spikeAt;
    private long _spikeTimestamp;

    public TrayStatusTracker(
        Func<bool> isOptimizing, Func<bool> isPaused, Func<int> appliedCount, Func<SessionOutcome?> lastOutcome, TimeProvider time)
    {
        _isOptimizing = isOptimizing;
        _isPaused = isPaused;
        _appliedCount = appliedCount;
        _lastOutcome = lastOutcome;
        _time = time;
    }

    /// <summary>
    /// Records a DPC spike. One outside a session is ignored, so it cannot surface when the next
    /// session starts.
    /// </summary>
    public void RecordDpcSpike()
    {
        lock (_lock)
        {
            if (!_isOptimizing())
                return;
            _spikeAt = _time.GetLocalNow();
            _spikeTimestamp = _time.GetTimestamp();
        }
    }

    public TrayState Resolve()
    {
        lock (_lock)
        {
            // Read under the lock: a spike that arrives after the session was seen ending belongs
            // to the next session and must survive this reset.
            var isOptimizing = _isOptimizing();
            if (!isOptimizing)
                _spikeAt = null;

            // A later activation replaces the run, and a deactivation ends it with its failures.
            var outcome = _lastOutcome();
            var failed = outcome == null || outcome.EndedUtc != null
                ? 0
                : outcome.Modules.Count(m => m.Kind == ModuleOutcomeKind.Failed);

            var age = _spikeAt == null ? TimeSpan.Zero : _time.GetElapsedTime(_spikeTimestamp);
            return TrayState.Resolve(new TrayStateInput(isOptimizing, _isPaused(), _appliedCount(), failed, _spikeAt, age));
        }
    }
}

/// <summary>
/// Picks the tray icon set and names its resources.
/// </summary>
public static class TrayIconSets
{
    // The dark-taskbar set draws near-white glyphs and the light set near-black ones; both
    // contrast equally against a background of about this relative luminance.
    private const double ContrastCrossover = 0.2;

    /// <summary>
    /// The light-theme setting decides, except in high contrast, where the theme's own window
    /// background stands in for the taskbar and the set that contrasts more with it wins.
    /// </summary>
    public static TrayIconTheme Choose(bool systemUsesLightTheme, bool highContrast, byte backgroundR, byte backgroundG, byte backgroundB)
    {
        if (!highContrast)
            return systemUsesLightTheme ? TrayIconTheme.LightTaskbar : TrayIconTheme.DarkTaskbar;

        return RelativeLuminance(backgroundR, backgroundG, backgroundB) > ContrastCrossover
            ? TrayIconTheme.LightTaskbar
            : TrayIconTheme.DarkTaskbar;
    }

    /// <summary>The icon's file name under Assets/Icons, without the extension.</summary>
    public static string ResourceName(TrayIconShape shape, TrayIconTheme theme)
    {
        var state = shape switch
        {
            TrayIconShape.Idle => "idle",
            TrayIconShape.Active => "active",
            TrayIconShape.Paused => "paused",
            TrayIconShape.Warning => "warning",
            TrayIconShape.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        return $"tray-{state}-{(theme == TrayIconTheme.LightTaskbar ? "light" : "dark")}";
    }

    // WCAG relative luminance of an sRGB colour.
    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }
}
