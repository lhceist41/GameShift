using System.Globalization;

namespace GameShift.Core.Monitoring;

/// <summary>
/// DPC load: the share of CPU time spent in deferred procedure calls, from the
/// "Processor\% DPC Time" counter. Values are stored as that percent x 10000, the scale
/// earlier versions labelled microseconds, so saved thresholds and session history keep
/// their numbers and only the display converts.
/// </summary>
public static class DpcLoad
{
    public const double StoredUnitsPerPercent = 10000;

    // The largest threshold a player can enter: all of the CPU's time.
    private const double MaxPercent = 100;

    public static double FromCounterPercent(double counterPercent) => counterPercent * StoredUnitsPerPercent;

    public static double ToPercent(double stored) => stored / StoredUnitsPerPercent;

    /// <summary>
    /// The alert rule: a sample strictly above the threshold is a spike.
    /// </summary>
    public static bool ExceedsThreshold(double stored, int threshold) => stored > threshold;

    /// <summary>
    /// Formats a stored value for display, for example "0.05%". Up to four decimals, the
    /// threshold's precision, so a value just over a threshold never reads as equal to it.
    /// Invariant culture so it matches the threshold boxes, which WPF binds with en-US.
    /// </summary>
    public static string Format(double stored) =>
        ToPercent(stored).ToString("0.00##", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Formats a stored threshold for an edit box, as a plain percent without the sign.
    /// </summary>
    public static string FormatThreshold(int threshold) =>
        ToPercent(threshold).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a threshold typed as a percent of CPU time into the stored value.
    /// Accepts a decimal comma. Returns false for text that is not a number from 0 to 100.
    /// </summary>
    public static bool TryParseThreshold(string? text, out int threshold)
    {
        threshold = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || double.IsNaN(percent) || percent < 0 || percent > MaxPercent)
            return false;

        threshold = (int)Math.Round(percent * StoredUnitsPerPercent, MidpointRounding.AwayFromZero);
        return true;
    }
}
