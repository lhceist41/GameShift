using System.Diagnostics;
using GameShift.Core.System;

namespace GameShift.Core.Detection;

/// <summary>
/// What a single observation of a process ID established.
/// </summary>
internal enum ProcessLiveness
{
    /// <summary>
    /// The rooted image path was read. Tier 1 also proves liveness; Tier 2 only saw the PID exist
    /// moments earlier, so the process may have exited since (the detector's liveness sweep heals that).
    /// </summary>
    Resolved,

    /// <summary>
    /// Liveness was proven for the exact process object, but its image path could not be read.
    /// Only ever produced by the same-handle Tier-1 path, so it is never an inference from an
    /// earlier existence observation.
    /// </summary>
    AliveNoPath,

    /// <summary>The process provably no longer exists (or already terminated).</summary>
    Gone,

    /// <summary>Nothing could be established. Callers must not register anything on this.</summary>
    Unknown
}

/// <summary>
/// Result of one <see cref="ProcessProbe"/> observation.
///
/// Invariants (enforced by the factory helpers below and asserted by ProcessProbeTests):
/// <list type="bullet">
/// <item><see cref="ExecutablePath"/> is non-empty only for <see cref="ProcessLiveness.Resolved"/>,
/// and is always rooted.</item>
/// <item><see cref="ObservedProcessName"/> is non-null only for <see cref="ProcessLiveness.Resolved"/>,
/// and is derived from that same rooted path - never from a PID snapshot or a cache.</item>
/// </list>
/// A detector-level <see cref="ProcessLiveness.AliveNoPath"/> fallback may use the live start
/// event's own process name; that name is deliberately not carried here, because the classifier
/// never observed it.
/// </summary>
internal readonly record struct ProcessProbeResult(
    ProcessLiveness Status,
    string? ExecutablePath,
    string? ObservedProcessName)
{
    internal static ProcessProbeResult Resolved(string rootedPath) =>
        new(ProcessLiveness.Resolved, rootedPath, Path.GetFileName(rootedPath));

    internal static readonly ProcessProbeResult AliveNoPath =
        new(ProcessLiveness.AliveNoPath, null, null);

    internal static readonly ProcessProbeResult Gone =
        new(ProcessLiveness.Gone, null, null);

    internal static readonly ProcessProbeResult Unknown =
        new(ProcessLiveness.Unknown, null, null);
}

/// <summary>
/// Whether a process ID is currently present in the process table.
/// </summary>
internal enum PidExistence
{
    Exists,
    Gone,
    Unknown
}

/// <summary>
/// The narrow native surface <see cref="ProcessProbe"/> drives. Implementations only translate
/// native/managed observations - all liveness policy lives in <see cref="ProcessProbe"/>, so the
/// state machine is testable without a fake process table.
/// </summary>
internal interface IProcessProbeBackend
{
    /// <summary>
    /// Opens a process handle. Returns false on failure, with the Win32 error in
    /// <paramref name="lastError"/> and <paramref name="handle"/> set to <see cref="IntPtr.Zero"/>.
    /// </summary>
    bool TryOpen(int processId, uint desiredAccess, out IntPtr handle, out int lastError);

    /// <summary>Zero-timeout wait on a process handle. Returns the raw Win32 wait result.</summary>
    uint Wait(IntPtr handle);

    /// <summary>Reads the full image path for an open handle, or null when unavailable.</summary>
    string? QueryImagePath(IntPtr handle);

    /// <summary>Closes a handle previously returned by <see cref="TryOpen"/>.</summary>
    void Close(IntPtr handle);

    /// <summary>Classifies whether a process ID currently exists.</summary>
    PidExistence CheckPidExists(int processId);
}

/// <summary>
/// Production backend: thin, stateless wrapper over the Win32 calls plus
/// <see cref="Process.GetProcessById(int)"/> for existence classification only.
///
/// Every EXPECTED native or managed failure is translated into a return value rather than an
/// exception, so <see cref="ProcessProbe"/> stays pure policy. That is deliberately not a blanket
/// "never throws": a platform or programming failure (a missing export, a marshalling bug) is left
/// to propagate rather than being swallowed here. <see cref="ProcessProbe"/> closes every handle it
/// opened on the way out, and the detector's call sites contain such an exception as "no match" -
/// never as affirmative detection.
/// </summary>
internal sealed class Win32ProcessProbeBackend : IProcessProbeBackend
{
    /// <summary>Win32 MAX_PATH is 260, but QueryFullProcessImageNameW can return longer paths.</summary>
    private const int PathBufferChars = 1024;

    public bool TryOpen(int processId, uint desiredAccess, out IntPtr handle, out int lastError)
    {
        handle = NativeInterop.OpenProcess(desiredAccess, false, processId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            lastError = global::System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            handle = IntPtr.Zero;
            return false;
        }

        lastError = 0;
        return true;
    }

    public uint Wait(IntPtr handle)
    {
        try
        {
            return NativeInterop.WaitForSingleObject(handle, 0);
        }
        catch
        {
            return NativeInterop.WAIT_FAILED;
        }
    }

    public string? QueryImagePath(IntPtr handle)
    {
        try
        {
            var buffer = new char[PathBufferChars];
            uint size = PathBufferChars;
            if (!NativeInterop.QueryFullProcessImageName(handle, 0, buffer, ref size))
                return null;

            if (size == 0 || size > PathBufferChars)
                return null;

            return new string(buffer, 0, (int)size);
        }
        catch
        {
            return null;
        }
    }

    public void Close(IntPtr handle)
    {
        try
        {
            NativeInterop.CloseHandle(handle);
        }
        catch
        {
            // Nothing useful to do; the probe result must not depend on cleanup succeeding.
        }
    }

    public PidExistence CheckPidExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return PidExistence.Exists;
        }
        catch (ArgumentException)
        {
            // Documented "no process with that ID" signal.
            return PidExistence.Gone;
        }
        catch
        {
            return PidExistence.Unknown;
        }
    }
}

/// <summary>
/// Classifies a process ID into <see cref="ProcessLiveness"/> using the lowest rights that can
/// answer the question, so protected (anti-cheat) game processes are not automatically opaque.
///
/// Replaces the previous <c>Process.MainModule</c> resolution, which requires
/// PROCESS_QUERY_INFORMATION|PROCESS_VM_READ and cannot distinguish "dead" from "unreadable" -
/// the ambiguity that let a dead or unproven process start register a game.
///
/// The policy is deliberately conservative: liveness is only ever established by a zero-timeout
/// wait on a handle to the exact process object, never inferred from an earlier existence check
/// plus later uncertainty.
/// </summary>
internal sealed class ProcessProbe
{
    /// <summary>Shared production instance over an immutable stateless backend.</summary>
    internal static readonly ProcessProbe Default = new(new Win32ProcessProbeBackend());

    private readonly IProcessProbeBackend _backend;

    internal ProcessProbe(IProcessProbeBackend backend) => _backend = backend;

    /// <summary>
    /// Observes <paramref name="processId"/> once.
    ///
    /// Tier 1 - one handle carrying both SYNCHRONIZE and PROCESS_QUERY_LIMITED_INFORMATION:
    /// wait(0) first, then read the image path on that same handle. A signaled handle means the
    /// process terminated; a timeout means it was alive at that instant. A valid handle on its own
    /// is never treated as proof of life. Only this tier can produce
    /// <see cref="ProcessLiveness.AliveNoPath"/>.
    ///
    /// Tier 2 - reached only when Tier 1 could not open the process. It re-establishes existence and
    /// then attempts a query-rights-only read. It can confirm a path or report Gone/Unknown, but it
    /// can never assert liveness, because nothing in it proves the process object observed by
    /// <see cref="IProcessProbeBackend.CheckPidExists"/> is the one still alive at the later open.
    ///
    /// A process can always exit immediately after any successful observation. That ordinary race is
    /// healed by the stop event and the liveness sweep; it is not a reason to weaken this classifier.
    /// </summary>
    internal ProcessProbeResult Probe(int processId)
    {
        // ---- Tier 1: same-handle liveness + image path -------------------------------------
        const uint tier1Access = NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION | NativeInterop.SYNCHRONIZE;

        if (_backend.TryOpen(processId, tier1Access, out var handle, out var openError))
        {
            try
            {
                var wait = _backend.Wait(handle);

                if (wait == NativeInterop.WAIT_OBJECT_0)
                    return ProcessProbeResult.Gone; // signaled process object -> terminated

                if (wait != NativeInterop.WAIT_TIMEOUT)
                    return ProcessProbeResult.Unknown; // WAIT_FAILED or anything unexpected

                // Nonsignaled: liveness is proven for this exact process object.
                var path = _backend.QueryImagePath(handle);
                return IsUsablePath(path)
                    ? ProcessProbeResult.Resolved(path!)
                    : ProcessProbeResult.AliveNoPath;
            }
            finally
            {
                _backend.Close(handle);
            }
        }

        // Open failed. OpenProcess reports ERROR_INVALID_PARAMETER when the PID does not exist;
        // that is the one open failure allowed to short-circuit, and it fails closed (no match).
        if (openError == NativeInterop.ERROR_INVALID_PARAMETER)
            return ProcessProbeResult.Gone;

        // ---- Tier 2: conservative, never AliveNoPath ----------------------------------------
        var existence = _backend.CheckPidExists(processId);
        if (existence == PidExistence.Gone)
            return ProcessProbeResult.Gone;
        if (existence == PidExistence.Unknown)
            return ProcessProbeResult.Unknown;

        if (!_backend.TryOpen(processId, NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION, out var queryHandle, out var queryError))
        {
            // The PID existed a moment ago and now cannot be opened. Only the documented
            // "no such process" error is accepted as proof it is gone; everything else is
            // uncertainty and must never become affirmative liveness.
            return queryError == NativeInterop.ERROR_INVALID_PARAMETER
                ? ProcessProbeResult.Gone
                : ProcessProbeResult.Unknown;
        }

        try
        {
            var path = _backend.QueryImagePath(queryHandle);
            return IsUsablePath(path)
                ? ProcessProbeResult.Resolved(path!)
                : ProcessProbeResult.Unknown;
        }
        finally
        {
            _backend.Close(queryHandle);
        }
    }

    /// <summary>
    /// A path is only usable when it is non-empty and fully qualified. Fully qualified rather than
    /// merely rooted: on Windows a leading backslash is "rooted" too, so a drive-relative or NT
    /// device path (\Device\HarddiskVolume4\...) would pass IsPathRooted while naming no usable
    /// location - and would then be silently rebased onto the current drive by Path.GetFullPath
    /// downstream. QueryFullProcessImageNameW with dwFlags 0 returns Win32 paths, which are always
    /// fully qualified, so nothing legitimate is rejected here.
    /// </summary>
    private static bool IsUsablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false; // invalid characters
        }
    }
}
