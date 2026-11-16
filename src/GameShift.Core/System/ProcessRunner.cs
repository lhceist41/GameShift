using System.Diagnostics;
using System.Text;

namespace GameShift.Core.System;

/// <summary>
/// Result of running an external process via <see cref="ProcessRunner.Run"/>.
/// </summary>
/// <param name="ExitCode">Process exit code. Valid ONLY when <see cref="Exited"/> is true; 0 otherwise.</param>
/// <param name="StdOut">
/// Captured standard output, raw (not trimmed). May be partial if the run timed out. After a normal
/// exit it is either complete or empty: a stream not read to its end within the short grace (for
/// example a pipe a grandchild still holds) is returned empty, never truncated.
/// </param>
/// <param name="StdErr">Captured standard error, raw (not trimmed). Same rules as StdOut.</param>
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
/// It drains BOTH stdout and stderr on dedicated threads so a child that floods either pipe cannot
/// deadlock, waits up to <paramref name="timeoutMs"/> for exit, and kills the whole process tree on
/// timeout so a wedged child can neither hang the caller nor be orphaned. Reading stdout off-thread
/// (rather than synchronously on the calling thread, as the old idiom did) is what closes the
/// real hang: WaitForExit is then the only thing that gates the timeout, so a child that holds
/// stdout open without exiting is still killed at the deadline.
///
/// The caller passes an ALREADY-RESOLVED absolute file path (e.g. NativeInterop.SystemExePath(...)):
/// the runner never resolves paths, so each caller's anti-PATH-hijack choice is preserved. The fixed
/// startup options match every call site it replaces (UseShellExecute=false, CreateNoWindow=true,
/// both pipes redirected, default encoding, no stdin/working-dir, inherited environment unless
/// <c>environment</c> overrides variables). Output is returned raw, so
/// each caller keeps its own trimming/parsing. Exceptions from Process.Start / WaitForExit propagate
/// to the caller (each call site keeps its existing try/catch); a pipe-read error ends that stream
/// with what it had delivered so far (best effort) rather than faulting the run.
/// </summary>
public static class ProcessRunner
{
    private const int GraceMs = 2000;

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, capturing stdout and
    /// stderr, waiting up to <paramref name="timeoutMs"/> for exit and tree-killing on timeout.
    /// </summary>
    /// <param name="fileName">Absolute path to the executable (callers resolve it themselves).</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="timeoutMs">Maximum time to wait for the process to exit, in milliseconds.</param>
    /// <param name="environment">Variables to set in the child's environment, overriding inherited ones.</param>
    public static ProcessRunResult Run(
        string fileName, string arguments, int timeoutMs, IReadOnlyDictionary<string, string>? environment = null)
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

        if (environment != null)
        {
            foreach (var (name, value) in environment)
                psi.Environment[name] = value;
        }

        return Run(psi, timeoutMs);
    }

    /// <summary>
    /// Runs a caller-built <paramref name="psi"/> under the same drain, timeout and result contract
    /// as the public overload, for callers whose start info a variable list cannot express.
    /// </summary>
    /// <exception cref="ArgumentException">Stdout or stderr is not redirected.</exception>
    internal static ProcessRunResult Run(ProcessStartInfo psi, int timeoutMs)
    {
        if (!psi.RedirectStandardOutput || !psi.RedirectStandardError)
            throw new ArgumentException("Both standard output and standard error must be redirected.", nameof(psi));

        using var process = Process.Start(psi);
        if (process == null)
            return new ProcessRunResult(0, string.Empty, string.Empty, TimedOut: false, Exited: false);

        // Dedicated threads, not the thread pool: a starved pool can delay a queued drain past the
        // grace below, and the run would then report empty output for a child that wrote some.
        var outDrain = new PipeDrain(process.StandardOutput, "ProcessRunner stdout");
        var errDrain = new PipeDrain(process.StandardError, "ProcessRunner stderr");

        if (!process.WaitForExit(timeoutMs))
        {
            // Wedged child: kill the whole tree so nothing is orphaned, then collect whatever the
            // drains captured before the pipes closed. Never block past a short grace.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            PipeDrain.JoinBoth(outDrain, errDrain, GraceMs);
            return new ProcessRunResult(0, outDrain.Text, errDrain.Text, TimedOut: true, Exited: false);
        }

        // Exited within the timeout. The wait stays bounded because a grandchild can hold the pipes
        // open; an unfinished stream then reads as empty, since callers treat empty as "could not
        // be read" but would take truncated text with Exited set as complete.
        PipeDrain.JoinBoth(outDrain, errDrain, GraceMs);
        return new ProcessRunResult(
            process.ExitCode, outDrain.TextIfFinished, errDrain.TextIfFinished, TimedOut: false, Exited: true);
    }

    /// <summary>
    /// Reads one pipe to its end on a background thread, appending in chunks so a reader that is
    /// abandoned after the grace still yields everything delivered up to that point.
    /// </summary>
    internal sealed class PipeDrain
    {
        private readonly StringBuilder _text = new();
        private readonly Thread _thread;
        // Set by the drain thread after its last append; read by the caller after the joins.
        private volatile bool _finished;

        public PipeDrain(StreamReader reader, string name)
        {
            _thread = new Thread(() => Pump(reader)) { IsBackground = true, Name = name };
            _thread.Start();
        }

        /// <summary>Everything read so far, finished or not.</summary>
        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        /// <summary>Everything read if the stream ended (pipe closed or read error), otherwise empty.</summary>
        public string TextIfFinished => _finished ? Text : string.Empty;

        /// <summary>Waits for both drains within one shared grace.</summary>
        public static void JoinBoth(PipeDrain first, PipeDrain second, int graceMs)
        {
            var sw = Stopwatch.StartNew();
            JoinBoth(first, second, graceMs, () => sw.ElapsedMilliseconds);
        }

        internal static void JoinBoth(PipeDrain first, PipeDrain second, int graceMs, Func<long> elapsedMs)
        {
            first._thread.Join(Remaining(graceMs, elapsedMs()));
            second._thread.Join(Remaining(graceMs, elapsedMs()));
        }

        // Clamped: a negative timeout other than -1 throws, and -1 waits forever.
        private static int Remaining(int graceMs, long elapsedMs) => (int)Math.Max(0, graceMs - elapsedMs);

        private void Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (_text) _text.Append(buffer, 0, read);
                }
            }
            catch { /* best effort: keep what was read */ }
            _finished = true;
        }
    }
}
