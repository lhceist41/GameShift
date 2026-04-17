using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// State machine for the benchmark capture lifecycle.
/// </summary>
public enum BenchmarkState
{
    /// <summary>No benchmark in progress.</summary>
    Idle,
    /// <summary>Benchmark requested but no game running.</summary>
    WaitingForGame,
    /// <summary>Capturing without optimizations (full benchmark baseline phase).</summary>
    CapturingBaseline,
    /// <summary>Capturing with optimizations applied.</summary>
    CapturingOptimized,
    /// <summary>Processing captured CSV data.</summary>
    Analyzing,
    /// <summary>Results ready for display.</summary>
    Complete
}

/// <summary>
/// PresentMon-based frame time benchmarking service.
/// Launches PresentMon CLI as a subprocess to capture frame timing data
/// from the active game process. Parses the resulting CSV into FPS metrics,
/// frame time percentiles, and latency data.
///
/// Two modes:
/// - Quick Capture: Single capture during active gaming (30s default)
/// - Full Benchmark: Before/after comparison (stretch goal, requires OptimizationEngine integration)
///
/// PresentMon uses ETW to capture Present() calls — fully anti-cheat safe.
/// Requires admin privileges (inherited from GameShift).
/// </summary>
public class BenchmarkService
{
    private Process? _presentMonProcess;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    /// <summary>Current benchmark state.</summary>
    public BenchmarkState State { get; private set; } = BenchmarkState.Idle;

    /// <summary>Last completed benchmark result.</summary>
    public BenchmarkResult? LastResult { get; private set; }

    /// <summary>Whether PresentMon binary is available.</summary>
    public bool IsPresentMonAvailable => GetPresentMonPath() != null;

    // ── Events ──────────────────────────────────────────────────────────

    /// <summary>Fired when benchmark state changes.</summary>
    public event Action<BenchmarkState>? StateChanged;

    /// <summary>Fired when a benchmark completes with results.</summary>
    public event Action<BenchmarkResult>? BenchmarkCompleted;

    /// <summary>Fired during capture with progress (0.0 to 1.0).</summary>
    public event Action<double>? CaptureProgressChanged;

    // ── Quick Capture ───────────────────────────────────────────────────

    /// <summary>
    /// Starts a quick single-pass capture targeting the active game process.
    /// Captures frame timing data for the specified duration with optimizations active.
    /// </summary>
    /// <param name="durationSeconds">Capture duration in seconds (default 30).</param>
    /// <param name="gameProcess">The game process to capture. Null to auto-detect.</param>
    public async Task StartQuickCapture(int durationSeconds = 30, Process? gameProcess = null)
    {
        if (State != BenchmarkState.Idle)
            throw new InvalidOperationException("Benchmark already in progress");

        _cts = new CancellationTokenSource();

        try
        {
            var target = gameProcess ?? GetActiveGameProcess();
            if (target == null)
            {
                Log.Warning("BenchmarkService: No active game detected for benchmark");
                return;
            }

            SetState(BenchmarkState.CapturingOptimized);

            string? csvPath = await CaptureFrames(target.ProcessName, durationSeconds, "quick", _cts.Token);
            if (string.IsNullOrEmpty(csvPath))
            {
                SetState(BenchmarkState.Idle);
                return;
            }

            SetState(BenchmarkState.Analyzing);

            var result = ParsePresentMonCsv(csvPath, target.ProcessName);
            if (result == null)
            {
                Log.Warning("BenchmarkService: CSV parsing produced no results");
                SetState(BenchmarkState.Idle);
                return;
            }

            LastResult = result;
            SetState(BenchmarkState.Complete);
            BenchmarkCompleted?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            Log.Information("BenchmarkService: Capture cancelled by user");
            SetState(BenchmarkState.Idle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BenchmarkService: Capture failed");
            SetState(BenchmarkState.Idle);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Cancels any in-progress benchmark capture.
    /// </summary>
    public void CancelBenchmark()
    {
        _cts?.Cancel();

        lock (_lock)
        {
            if (_presentMonProcess != null && !_presentMonProcess.HasExited)
            {
                try
                {
                    _presentMonProcess.Kill();
                    Log.Information("BenchmarkService: PresentMon process killed");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "BenchmarkService: Failed to kill PresentMon process");
                }
            }
        }

        SetState(BenchmarkState.Idle);
    }

    // ── PresentMon subprocess management ────────────────────────────────

    /// <summary>
    /// Launches PresentMon as a subprocess and captures frame data to CSV.
    /// Returns the CSV file path, or null on failure.
    /// </summary>
    private async Task<string?> CaptureFrames(
        string processName, int durationSeconds, string tag, CancellationToken ct)
    {
        string presentMonPath = GetPresentMonPath()!;
        if (presentMonPath == null)
        {
            Log.Warning("BenchmarkService: PresentMon binary not found");
            return null;
        }

        string outputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameShift", "benchmarks");
        Directory.CreateDirectory(outputDir);

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string csvPath = Path.Combine(outputDir, $"{processName}_{tag}_{timestamp}.csv");

        var psi = new ProcessStartInfo
        {
            FileName = presentMonPath,
            Arguments = $"--process_name {processName}.exe " +
                        $"--output_file \"{csvPath}\" " +
                        $"--timed {durationSeconds} " +
                        $"--no_top " +
                        $"--terminate_after_timed",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        lock (_lock)
        {
            _presentMonProcess = Process.Start(psi);
        }

        if (_presentMonProcess == null)
        {
            Log.Error("BenchmarkService: Failed to start PresentMon process");
            return null;
        }

        Log.Information("BenchmarkService: PresentMon started for {Process}, duration={Duration}s",
            processName, durationSeconds);

        // Track progress until PresentMon exits or we're cancelled
        var startTime = DateTime.Now;
        while (!_presentMonProcess.HasExited)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(500, ct);
            double elapsed = (DateTime.Now - startTime).TotalSeconds;
            CaptureProgressChanged?.Invoke(Math.Min(elapsed / durationSeconds, 1.0));
        }

        CaptureProgressChanged?.Invoke(1.0);

        lock (_lock)
        {
            _presentMonProcess = null;
        }

        if (!File.Exists(csvPath))
        {
            Log.Warning("BenchmarkService: PresentMon finished but CSV not found at {Path}", csvPath);
            return null;
        }

        Log.Information("BenchmarkService: Capture complete, CSV at {Path}", csvPath);
        return csvPath;
    }

    // ── CSV parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses a PresentMon CSV file into a BenchmarkResult.
    /// Handles column index lookup by header name (column order varies between PresentMon versions).
    /// Filters outlier frames (first frame, alt-tab artifacts > 1000ms).
    /// </summary>
    private static BenchmarkResult? ParsePresentMonCsv(string csvPath, string gameName)
    {
        var frameTimes = new List<double>();
        var cpuBusyTimes = new List<double>();
        var gpuTimes = new List<double>();
        var displayLatencies = new List<double>();
        int droppedFrames = 0;

        try
        {
            using var reader = new StreamReader(csvPath);
            string? headerLine = reader.ReadLine();
            if (headerLine == null) return null;

            // Parse header to find column indices (PresentMon column order varies by version)
            string[] headers = headerLine.Split(',');
            int frameTimeIdx = Array.IndexOf(headers, "FrameTime");
            int cpuBusyIdx = Array.IndexOf(headers, "CPUBusy");
            int gpuTimeIdx = Array.IndexOf(headers, "GPUTime");
            int displayLatencyIdx = Array.IndexOf(headers, "DisplayLatency");
            int droppedIdx = Array.IndexOf(headers, "Dropped");

            // Also try alternate column names used by older PresentMon versions
            if (frameTimeIdx < 0) frameTimeIdx = Array.IndexOf(headers, "MsBetweenPresents");
            if (cpuBusyIdx < 0) cpuBusyIdx = Array.IndexOf(headers, "MsUntilRenderComplete");
            if (displayLatencyIdx < 0) displayLatencyIdx = Array.IndexOf(headers, "MsUntilDisplayed");

            if (frameTimeIdx < 0)
            {
                Log.Warning("BenchmarkService: CSV has no FrameTime or MsBetweenPresents column");
                return null;
            }

            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] fields = line.Split(',');

                // FrameTime (ms)
                if (frameTimeIdx < fields.Length &&
                    double.TryParse(fields[frameTimeIdx], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double ft))
                {
                    // Filter outliers: skip first-frame artifacts and alt-tab spikes
                    if (ft > 0 && ft < 1000) // Between 0ms and 1000ms (1 FPS minimum)
                        frameTimes.Add(ft);
                }

                // CPU Busy time
                if (cpuBusyIdx >= 0 && cpuBusyIdx < fields.Length &&
                    double.TryParse(fields[cpuBusyIdx], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double cpu))
                {
                    if (cpu > 0) cpuBusyTimes.Add(cpu);
                }

                // GPU Time
                if (gpuTimeIdx >= 0 && gpuTimeIdx < fields.Length &&
                    double.TryParse(fields[gpuTimeIdx], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double gpu))
                {
                    if (gpu > 0) gpuTimes.Add(gpu);
                }

                // Display Latency
                if (displayLatencyIdx >= 0 && displayLatencyIdx < fields.Length &&
                    double.TryParse(fields[displayLatencyIdx], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double dl))
                {
                    if (dl > 0) displayLatencies.Add(dl);
                }

                // Dropped frames
                if (droppedIdx >= 0 && droppedIdx < fields.Length &&
                    int.TryParse(fields[droppedIdx], out int dropped) && dropped == 1)
                {
                    droppedFrames++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BenchmarkService: Failed to parse CSV at {Path}", csvPath);
            return null;
        }

        if (frameTimes.Count == 0) return null;

        // Sort for percentile calculation
        var sorted = frameTimes.OrderBy(x => x).ToList();
        int count = sorted.Count;

        double avgFrameTime = sorted.Average();
        double p99FrameTime = sorted[Math.Min((int)(count * 0.99), count - 1)];
        double p999FrameTime = sorted[Math.Min((int)(count * 0.999), count - 1)];
        double medianFrameTime = count % 2 == 0
            ? (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0
            : sorted[count / 2];
        double maxFrameTime = sorted[count - 1];
        double minFrameTime = sorted[0];

        // Standard deviation
        double variance = sorted.Sum(ft => Math.Pow(ft - avgFrameTime, 2)) / count;
        double stdDev = Math.Sqrt(variance);

        // Duration from frame time sum
        double totalDuration = frameTimes.Sum() / 1000.0; // ms → seconds

        return new BenchmarkResult
        {
            GameName = gameName,
            CaptureTime = DateTime.Now,
            TotalFrames = count,
            DroppedFrames = droppedFrames,
            CaptureDurationSeconds = totalDuration,

            AverageFps = 1000.0 / avgFrameTime,
            Fps1PercentLow = 1000.0 / p99FrameTime,
            Fps01PercentLow = 1000.0 / p999FrameTime,
            MinFps = 1000.0 / maxFrameTime,
            MaxFps = 1000.0 / minFrameTime,

            AverageFrameTime = avgFrameTime,
            MedianFrameTime = medianFrameTime,
            P99FrameTime = p99FrameTime,
            P999FrameTime = p999FrameTime,
            MaxFrameTime = maxFrameTime,
            FrameTimeStdDev = stdDev,

            AverageCpuBusy = cpuBusyTimes.Count > 0 ? cpuBusyTimes.Average() : 0,
            AverageGpuTime = gpuTimes.Count > 0 ? gpuTimes.Average() : 0,
            AverageDisplayLatency = displayLatencies.Count > 0 ? displayLatencies.Average() : 0,

            FrameTimes = frameTimes,
            CsvFilePath = csvPath
        };
    }

    // ── PresentMon binary management ────────────────────────────────────

    /// <summary>
    /// Locates the PresentMon executable. Checks bundled path first, then AppData download location.
    /// </summary>
    /// <returns>Full path to PresentMon.exe, or null if not found.</returns>
    public static string? GetPresentMonPath()
    {
        // Check bundled location (alongside GameShift.exe)
        string bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "PresentMon.exe");
        if (File.Exists(bundledPath)) return bundledPath;

        // Check downloaded location (%AppData%/GameShift/tools/)
        string downloadedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameShift", "tools", "PresentMon.exe");
        if (File.Exists(downloadedPath)) return downloadedPath;

        return null;
    }

    /// <summary>
    /// Downloads PresentMon from GitHub releases if not already present.
    /// </summary>
    /// <returns>True if PresentMon is available after this call.</returns>
    public static async Task<bool> EnsurePresentMonAvailable()
    {
        if (GetPresentMonPath() != null) return true;

        try
        {
            const string downloadUrl =
                "https://github.com/GameTechDev/PresentMon/releases/latest/download/PresentMon-2.3.0-x64.exe";
            string targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameShift", "tools");
            Directory.CreateDirectory(targetDir);
            string targetPath = Path.Combine(targetDir, "PresentMon.exe");

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(60);

            Log.Information("BenchmarkService: Downloading PresentMon from {Url}", downloadUrl);
            var bytes = await httpClient.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(targetPath, bytes);

            Log.Information("BenchmarkService: PresentMon downloaded to {Path} ({Size} bytes)",
                targetPath, bytes.Length);

            return File.Exists(targetPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BenchmarkService: Failed to download PresentMon");
            return false;
        }
    }

    // ── Game process detection ──────────────────────────────────────────

    /// <summary>
    /// Attempts to find the active game process by checking for known game process names
    /// that consume significant GPU resources (heuristic: high private working set).
    /// </summary>
    private static Process? GetActiveGameProcess()
    {
        try
        {
            // Get processes sorted by working set descending — games typically use the most memory
            var allProcesses = Process.GetProcesses();
            Process? bestMatch = null;
            long bestWorkingSet = 0;

            foreach (var p in allProcesses)
            {
                try
                {
                    // Filter: must have a window, use > 500MB RAM (typical for games)
                    if (p.MainWindowHandle != IntPtr.Zero &&
                        p.WorkingSet64 > 500 * 1024 * 1024 &&
                        !IsSystemProcess(p.ProcessName))
                    {
                        long ws = p.WorkingSet64;
                        if (ws > bestWorkingSet)
                        {
                            // Dispose the previous best match since we're replacing it
                            bestMatch?.Dispose();
                            bestMatch = p;
                            bestWorkingSet = ws;
                            continue; // Don't dispose this one
                        }
                    }
                }
                catch { /* Process may have exited */ }

                // Dispose any process we're not keeping
                if (p != bestMatch)
                    p.Dispose();
            }

            return bestMatch;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BenchmarkService: Failed to detect active game process");
            return null;
        }
    }

    /// <summary>
    /// Returns true for known non-game system processes that should not be benchmarked.
    /// </summary>
    private static bool IsSystemProcess(string processName)
    {
        return processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("dwm", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("csrss", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("Widgets", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("GameShift", StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SetState(BenchmarkState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }
}
