namespace GameShift.Core.Monitoring;

/// <summary>
/// Results from a PresentMon frame time capture session.
/// Contains FPS metrics, frame time percentiles, latency data,
/// and raw frame times for graphing.
/// </summary>
public class BenchmarkResult
{
    /// <summary>Name of the captured game process.</summary>
    public string GameName { get; set; } = "";

    /// <summary>When the capture was performed.</summary>
    public DateTime CaptureTime { get; set; }

    /// <summary>Total frames captured (after outlier filtering).</summary>
    public int TotalFrames { get; set; }

    /// <summary>Number of dropped frames detected by PresentMon.</summary>
    public int DroppedFrames { get; set; }

    /// <summary>Effective capture duration in seconds.</summary>
    public double CaptureDurationSeconds { get; set; }

    // ── FPS metrics ─────────────────────────────────────────────────────

    /// <summary>Average FPS (1000 / average frame time).</summary>
    public double AverageFps { get; set; }

    /// <summary>1% low FPS (1000 / 99th percentile frame time).</summary>
    public double Fps1PercentLow { get; set; }

    /// <summary>0.1% low FPS (1000 / 99.9th percentile frame time).</summary>
    public double Fps01PercentLow { get; set; }

    /// <summary>Minimum FPS (1000 / maximum frame time).</summary>
    public double MinFps { get; set; }

    /// <summary>Maximum FPS (1000 / minimum frame time).</summary>
    public double MaxFps { get; set; }

    // ── Frame time metrics (milliseconds) ───────────────────────────────

    /// <summary>Average frame time in milliseconds.</summary>
    public double AverageFrameTime { get; set; }

    /// <summary>Median frame time in milliseconds.</summary>
    public double MedianFrameTime { get; set; }

    /// <summary>99th percentile frame time in milliseconds.</summary>
    public double P99FrameTime { get; set; }

    /// <summary>99.9th percentile frame time in milliseconds.</summary>
    public double P999FrameTime { get; set; }

    /// <summary>Maximum frame time in milliseconds.</summary>
    public double MaxFrameTime { get; set; }

    /// <summary>Frame time standard deviation (lower = more consistent).</summary>
    public double FrameTimeStdDev { get; set; }

    // ── Latency metrics ─────────────────────────────────────────────────

    /// <summary>Average CPU busy time per frame in milliseconds.</summary>
    public double AverageCpuBusy { get; set; }

    /// <summary>Average GPU time per frame in milliseconds.</summary>
    public double AverageGpuTime { get; set; }

    /// <summary>Average end-to-end display latency in milliseconds.</summary>
    public double AverageDisplayLatency { get; set; }

    // ── Raw data for graphing ───────────────────────────────────────────

    /// <summary>Every captured frame's FrameTime value in milliseconds (for frame time graph).</summary>
    public List<double> FrameTimes { get; set; } = new();

    // ── Comparison ──────────────────────────────────────────────────────

    /// <summary>
    /// Baseline (optimizations OFF) result for before/after comparison.
    /// Null for single-capture (Quick Capture) results.
    /// </summary>
    public BenchmarkResult? BaselineResult { get; set; }

    /// <summary>Path to the raw PresentMon CSV file.</summary>
    public string CsvFilePath { get; set; } = "";
}
