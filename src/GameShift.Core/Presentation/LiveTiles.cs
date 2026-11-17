using System.Globalization;
using GameShift.Core.Monitoring;

namespace GameShift.Core.Presentation;

/// <summary>How a live value reads. Only <see cref="Raised"/> and <see cref="High"/> are coloured.</summary>
public enum TileTone
{
    Neutral,
    Good,
    Raised,
    High,
}

/// <summary>One live tile: the large value, the line under it and its tone.</summary>
public sealed record TileReading(string Value, string Detail, TileTone Tone)
{
    /// <summary>The value without its unit: "0.03" of "0.03%". A word such as "Measuring" has no unit and stays whole.</summary>
    public string Number => Split().Number;

    /// <summary>"%" or "ms", shown smaller after <see cref="Number"/>; empty for a word.</summary>
    public string Unit => Split().Unit;

    public bool HasUnit => Unit.Length > 0;

    /// <summary>
    /// The tag beside the tile's label for the two coloured tones; empty otherwise. A high tone
    /// without a number (no ping reply) is an alert, not a high value.
    /// </summary>
    public string Tag => Tone switch
    {
        TileTone.Raised => "RAISED",
        TileTone.High => HasUnit ? "HIGH" : "ALERT",
        _ => "",
    };

    private (string Number, string Unit) Split()
    {
        if (Value.Length > 3 && Value.EndsWith(" ms", StringComparison.Ordinal) && char.IsDigit(Value[^4]))
            return (Value[..^3], "ms");
        if (Value.Length > 1 && Value[^1] == '%' && char.IsDigit(Value[^2]))
            return (Value[..^1], "%");
        return (Value, "");
    }
}

/// <summary>
/// The texts of Home's live tiles. A source that cannot be read says "not available" instead of
/// showing 0, and every number uses the invariant culture so it matches the English text around it.
/// </summary>
public static class LiveTiles
{
    public const string NotAvailable = "not available";

    /// <summary>
    /// Under the tiles. The feed runs only while Home is seen (<see cref="HomeFeedGate"/>), and the CPU
    /// and ping monitors pause for a game while DPC is sampled only during one.
    /// </summary>
    public const string Footnote =
        "Live values update about every second while Home is open. They stop while GameShift is minimized " +
        "or in the tray. While a game runs, CPU, GPU, memory and ping pause; DPC load is measured only then.";

    /// <summary>
    /// The tiles' monitors sample every second (DPC every half second); a tile with no sample for
    /// longer than this is no longer live.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>Jitter needs two replies; with fewer the monitor reports 0, which was never measured.</summary>
    public const int RepliesForJitter = 2;

    // DPC load bands in the stored DpcLoad scale: 0.05% and 0.10% of CPU time.
    public const double DpcRaisedFrom = 500;
    public const double DpcHighAbove = 1000;

    public const long PingRaisedFromMs = 50;
    public const long PingHighAboveMs = 100;

    private const string DpcCaption = "CPU time in driver interrupts";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static TileReading Measuring(string detail) => new("Measuring", detail, TileTone.Neutral);

    /// <param name="counterReadable">False when the monitor is missing or this PC has no "% DPC Time" counter.</param>
    /// <param name="monitoring">The monitor samples only during a game session with DPC monitoring on.</param>
    /// <param name="latest">The newest sample since the tile started listening, or null before the first.</param>
    /// <param name="runningGameHasDpcOff">A game runs and its profile has DPC monitoring switched off.</param>
    public static TileReading Dpc(
        bool counterReadable, bool monitoring, double? latest, double average, bool runningGameHasDpcOff = false)
    {
        if (!counterReadable)
            return new(NotAvailable, "This PC reports no DPC time", TileTone.Neutral);
        if (!monitoring)
        {
            return new("Not measuring",
                runningGameHasDpcOff ? "DPC monitoring is off in this game's profile" : "Measured while a game runs",
                TileTone.Neutral);
        }
        if (latest is not double value || !double.IsFinite(value))
            return Measuring(DpcCaption);

        var (word, tone) = value > DpcHighAbove ? ("High", TileTone.High)
            : value >= DpcRaisedFrom ? ("Raised", TileTone.Raised)
            : ("Good", TileTone.Good);
        return new(DpcLoad.Format(value), word + " · average " + DpcLoad.Format(average), tone);
    }

    /// <param name="monitoring">False when there is no ping monitor or it is not running.</param>
    /// <param name="sample">The newest ping since the tile started listening, or null before the first.</param>
    /// <param name="replies">Replies among the samples the tile's line holds, this one included.</param>
    public static TileReading Ping(bool monitoring, string target, PingSample? sample, int replies = 0)
    {
        if (!monitoring)
            return new(NotAvailable, "", TileTone.Neutral);
        if (sample == null)
            return Measuring("to " + target);

        string loss = Number(sample.PacketLossPercent) + "% loss";
        if (!sample.Success)
        {
            // Every ping in the monitor's window failed: there is no latency to show.
            if (sample.PacketLossPercent >= 100)
                return new(NotAvailable, "No reply from " + target, TileTone.High);
            return new("No reply", "to " + target + " · " + loss, TileTone.High);
        }

        long rtt = sample.RttMilliseconds;
        var (word, tone) = rtt > PingHighAboveMs ? ("High", TileTone.High)
            : rtt >= PingRaisedFromMs ? ("Raised", TileTone.Raised)
            : ("Good", TileTone.Good);
        string detail = word + " · to " + target + " · " + loss;
        if (replies >= RepliesForJitter)
            detail += " · jitter " + Number(sample.JitterMs) + " ms";
        return new(rtt.ToString(Inv) + " ms", detail, tone);
    }

    /// <param name="cpu">CPU use in percent, or null when its counter cannot be read.</param>
    /// <param name="gpu">GPU use in percent, or null when the "GPU Engine" counters are missing.</param>
    public static TileReading CpuGpu(double? cpu, double? gpu)
    {
        if (cpu is double c && gpu is double g)
            return new(Number(c) + " / " + Number(g) + "%", "CPU / GPU", TileTone.Neutral);
        if (cpu is double cpuOnly)
            return new(Number(cpuOnly) + "%", "CPU · GPU " + NotAvailable, TileTone.Neutral);
        if (gpu is double gpuOnly)
            return new(Number(gpuOnly) + "%", "GPU · CPU " + NotAvailable, TileTone.Neutral);
        return new(NotAvailable, "No CPU or GPU counter on this PC", TileTone.Neutral);
    }

    /// <param name="usedPercent">Memory in use in percent, or null when its counter cannot be read.</param>
    /// <param name="totalMb">Installed memory in MB; 0 or less when unknown.</param>
    public static TileReading Memory(double? usedPercent, double totalMb)
    {
        if (usedPercent is not double p || !double.IsFinite(p))
            return new(NotAvailable, "", TileTone.Neutral);
        if (!(totalMb > 0))
            return new(Number(p) + "%", "in use", TileTone.Neutral);

        double totalGb = totalMb / 1024;
        double usedGb = totalGb * p / 100;
        return new(
            Number(p) + "%",
            Gb(usedGb) + " of " + Gb(totalGb) + " GB in use · " + Gb(totalGb - usedGb) + " GB available",
            TileTone.Neutral);
    }

    /// <summary>A tile whose samples stopped arriving. Home pauses the CPU and ping readings while a game runs.</summary>
    public static TileReading Stale(bool gameRunning) => gameRunning
        ? new("Paused", "Readings pause while a game runs", TileTone.Neutral)
        : new("No new reading", "", TileTone.Neutral);

    public static bool IsStale(DateTime lastSampleUtc, DateTime nowUtc) => nowUtc - lastSampleUtc > StaleAfter;

    private static string Number(double value) => value.ToString("0", Inv);

    private static string Gb(double value) => value.ToString("0.0", Inv);
}

/// <summary>How many live tiles share a row, by the width Home gives them in DIP.</summary>
public static class LiveTileLayout
{
    /// <summary>Below this width per tile the tiles take fewer columns.</summary>
    public const double MinTileWidth = 170;

    /// <summary>Four, two or one: three would leave one tile alone on a second row.</summary>
    public static int Columns(double availableWidth)
    {
        if (!(availableWidth > 0)) return 4;
        if (availableWidth >= 4 * MinTileWidth) return 4;
        if (availableWidth >= 2 * MinTileWidth) return 2;
        return 1;
    }
}
