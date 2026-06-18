using System.Diagnostics;

namespace GameShift.Core.System;

/// <summary>
/// Result of running an external process via <see cref="ProcessRunner.Run"/>.
/// </summary>
/// <param name="ExitCode">Process exit code. Valid ONLY when <see cref="Exited"/> is true; 0 otherwise.</param>
/// <param name="StdOut">Captured standard output, raw (not trimmed). May be partial if the run timed out.</param>
/// <param name="StdErr">Captured standard error, raw (not trimmed). May be partial if the run timed out.</param>
/// <param name="TimedOut">True if the process did not exit within the timeout and was killed.</param>
/// <param name="Exited">
/// True if the process started and exited on its own within the timeout. False means either the
/// process failed to start (Process.Start returned null) or it timed out (see <see cref="TimedOut"/>).
/// Callers must branch on this before trusting <see cref="ExitCode"/>.
/// </param>
public readonly record struct ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Exited);

/// <summary>
/// Single shared runner for short-lived external command-line tools (powercfg, netsh, schtasks,
/// bcdedit, fsutil, sc, PowerShell, ...). Replaces the ~dozen near-identical private runner methods
/// that each did "drain stderr on a worker, ReadToEnd stdout on the calling thread, WaitForExit".
///
/// It drains BOTH stdout and stderr on worker threads so a child that floods either pipe cannot
/// deadlock, waits up to <paramref name="timeoutMs"/> for exit, and kills the whole process tree on
/// timeout so a wedged child can neither hang the caller nor be orphaned. Reading stdout off-thread
/// (rather than synchronously on the calling thread, as the old idiom did) is what closes the
/// real hang: WaitForExit is then the only thing that gates the timeout, so a child that holds
/// stdout open without exiting is still killed at the deadline.
///
/// The caller passes an ALREADY-RESOLVED absolute file path (e.g. NativeInterop.SystemExePath(...)):
/// the runner never resolves paths, so each caller's anti-PATH-hijack choice is preserved. The fixed
/// startup options match every call site it replaces (UseShellExecute=false, CreateNoWindow=true,
/// both pipes redirected, default encoding, no stdin/working-dir/env). Output is returned raw, so
/// each caller keeps its own trimming/parsing. Exceptions from Process.Start / WaitForExit propagate
/// to the caller (each call site keeps its existing try/catch); a pipe-read error degrades that
/// stream to empty (best effort) rather than faulting the run.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, capturing stdout and
    /// stderr, waiting up to <paramref name="timeoutMs"/> for exit and tree-killing on timeout.
    /// </summary>
    /// <param name="fileName">Absolute path to the executable (callers resolve it themselves).</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="timeoutMs">Maximum time to wait for the process to exit, in milliseconds.</param>
    public static ProcessRunResult Run(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
            return new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false, Exited: false);

        // Drain both pipes on worker threads. A read error degrades that stream to empty so the
        // drain tasks never fault (and Task.WaitAll never throws for that reason).
        string stdout = string.Empty, stderr = string.Empty;
        var outTask = Task.Run(() => { try { stdout = process.StandardOutput.ReadToEnd(); } catch { /* best effort */ } });
        var errTask = Task.Run(() => { try { stderr = process.StandardError.ReadToEnd(); } catch { /* best effort */ } });

        if (!process.WaitForExit(timeoutMs))
        {
            // Wedged child: kill the whole tree so nothing is orphaned, then collect whatever the
            // drain tasks captured before the pipes closed. Never block past a short grace.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            Task.WaitAll(new[] { outTask, errTask }, 2000);
            return new ProcessRunResult(0, stdout, stderr, TimedOut: true, Exited: false);
        }

        // Exited within the timeout: let the drain tasks finish so the captured output is complete,
        // then read ExitCode (safe now that the process has exited).
        Task.WaitAll(new[] { outTask, errTask }, 2000);
        return new ProcessRunResult(process.ExitCode, stdout, stderr, TimedOut: false, Exited: true);
    }
}
