using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GameShift.Core.Detection;
using GameShift.Core.System;
using Xunit;

namespace GameShift.Tests.Detection;

/// <summary>
/// Deterministic backend for <see cref="ProcessProbe"/>. Every native observation is scripted in
/// call order, so the classifier's state machine - not a real process table - is what is under test.
/// An unscripted call throws rather than returning a default, so a test cannot pass by taking a
/// branch it never described.
///
/// This is deliberately NOT a fake process table: it has no notion of processes, only of the
/// ordered outcomes the backend contract can produce.
/// </summary>
internal sealed class ScriptedProcessProbeBackend : IProcessProbeBackend
{
    private readonly Queue<(bool Success, int LastError)> _opens = new();
    private readonly Queue<uint> _waits = new();
    private readonly Queue<string?> _paths = new();
    private readonly Queue<PidExistence> _existence = new();
    private int _nextHandle = 0x1000;

    /// <summary>Desired-access mask of every <see cref="TryOpen"/> call, in order.</summary>
    public List<uint> RequestedAccess { get; } = new();

    /// <summary>Handles handed out by a successful open, in order.</summary>
    public List<IntPtr> OpenedHandles { get; } = new();

    /// <summary>Handles passed to <see cref="Close"/>, in order.</summary>
    public List<IntPtr> ClosedHandles { get; } = new();

    /// <summary>How many times the image-path query was attempted.</summary>
    public int PathQueries { get; private set; }

    /// <summary>How many times PID existence was classified.</summary>
    public int ExistenceChecks { get; private set; }

    public ScriptedProcessProbeBackend OpenSucceeds()
    {
        _opens.Enqueue((true, 0));
        return this;
    }

    public ScriptedProcessProbeBackend OpenFails(int lastError)
    {
        _opens.Enqueue((false, lastError));
        return this;
    }

    public ScriptedProcessProbeBackend WaitReturns(uint waitResult)
    {
        _waits.Enqueue(waitResult);
        return this;
    }

    public ScriptedProcessProbeBackend PathQueryReturns(string? path)
    {
        _paths.Enqueue(path);
        return this;
    }

    public ScriptedProcessProbeBackend ExistenceIs(PidExistence existence)
    {
        _existence.Enqueue(existence);
        return this;
    }

    public bool TryOpen(int processId, uint desiredAccess, out IntPtr handle, out int lastError)
    {
        RequestedAccess.Add(desiredAccess);

        if (_opens.Count == 0)
            throw new InvalidOperationException("Unscripted TryOpen - the classifier took an unexpected branch.");

        var (success, error) = _opens.Dequeue();
        if (!success)
        {
            handle = IntPtr.Zero;
            lastError = error;
            return false;
        }

        handle = new IntPtr(_nextHandle++);
        OpenedHandles.Add(handle);
        lastError = 0;
        return true;
    }

    public uint Wait(IntPtr handle)
    {
        if (_waits.Count == 0)
            throw new InvalidOperationException("Unscripted Wait - the classifier took an unexpected branch.");

        Assert.Contains(handle, OpenedHandles);
        return _waits.Dequeue();
    }

    public string? QueryImagePath(IntPtr handle)
    {
        if (_paths.Count == 0)
            throw new InvalidOperationException("Unscripted QueryImagePath - the classifier took an unexpected branch.");

        Assert.Contains(handle, OpenedHandles);
        PathQueries++;
        return _paths.Dequeue();
    }

    public void Close(IntPtr handle) => ClosedHandles.Add(handle);

    public PidExistence CheckPidExists(int processId)
    {
        if (_existence.Count == 0)
            throw new InvalidOperationException("Unscripted CheckPidExists - the classifier took an unexpected branch.");

        ExistenceChecks++;
        return _existence.Dequeue();
    }

    /// <summary>Every handle that was opened was also closed exactly once, and nothing else was.</summary>
    public void AssertAllHandlesClosed() => Assert.Equal(OpenedHandles, ClosedHandles);
}

/// <summary>
/// Contract suite for <see cref="ProcessProbe"/> - the classifier that decides whether a process ID
/// is alive, dead or simply unobservable. It exists because the previous
/// <c>Process.MainModule</c> resolution could not tell those apart, so an unreadable or already-dead
/// process start could still register a game and apply optimizations.
///
/// The policy itself is exercised here through <see cref="ScriptedProcessProbeBackend"/>; the
/// detector-level consequences live in ProtectedProcessDetectionTests.
/// </summary>
public class ProcessProbeTests
{
    private const uint Tier1Access =
        NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION | NativeInterop.SYNCHRONIZE;

    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Contract 1: a handle that opens, whose zero-timeout wait times out (nonsignaled -> the
    /// process object is still alive), and whose image path reads back rooted, resolves - carrying
    /// both the rooted path and the filename derived from that same path.
    ///
    /// The rights requested are the lower-privilege pair, which is the whole point of the repair:
    /// PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE instead of the
    /// PROCESS_QUERY_INFORMATION|PROCESS_VM_READ that Process.MainModule needs.
    /// </summary>
    [Fact]
    public void Probe_Tier1AliveWithReadableRootedPath_Resolves()
    {
        const string imagePath = @"C:\Games\Apex\r5apex_dx12.exe";

        var backend = new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(NativeInterop.WAIT_TIMEOUT)
            .PathQueryReturns(imagePath);

        var result = new ProcessProbe(backend).Probe(4242);

        Assert.Equal(ProcessLiveness.Resolved, result.Status);
        Assert.Equal(imagePath, result.ExecutablePath);
        Assert.Equal("r5apex_dx12.exe", result.ObservedProcessName);

        Assert.Equal(Tier1Access, Assert.Single(backend.RequestedAccess));
        Assert.Equal(0, backend.ExistenceChecks); // Tier 2 never entered
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// Contract 2: a signaled process object means the process has terminated. A valid handle is
    /// never on its own treated as proof of life - the wait decides, and it decides before the path
    /// is ever queried.
    /// </summary>
    [Fact]
    public void Probe_Tier1SignaledProcessObject_IsGone()
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(NativeInterop.WAIT_OBJECT_0);

        var result = new ProcessProbe(backend).Probe(4243);

        Assert.Equal(ProcessLiveness.Gone, result.Status);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.ObservedProcessName);

        Assert.Equal(0, backend.PathQueries); // a dead process is never asked for its path
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// Contract 3: a failed or unexpected wait result establishes nothing. It must not become
    /// liveness, and it must not become death either - callers drop the observation entirely.
    /// (WAIT_ABANDONED is documented for abandoned mutexes, not process handles, so it is not
    /// special-cased; it lands in the same "unexpected" bucket.)
    /// </summary>
    [Theory]
    [InlineData(NativeInterop.WAIT_FAILED)]
    [InlineData(0x00000080u)] // WAIT_ABANDONED - not a process-handle result
    [InlineData(0xDEADBEEFu)] // anything undocumented
    public void Probe_Tier1WaitFailureOrUnexpectedResult_IsUnknown(uint waitResult)
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(waitResult);

        var result = new ProcessProbe(backend).Probe(4244);

        Assert.Equal(ProcessLiveness.Unknown, result.Status);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.ObservedProcessName);

        Assert.Equal(0, backend.PathQueries);
        Assert.Equal(0, backend.ExistenceChecks);
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// Contract 4: when Tier 1 cannot open the process, Tier 2 re-establishes existence and then
    /// tries a query-rights-only open. ERROR_INVALID_PARAMETER from that later open is the
    /// documented "no such process" signal, so the earlier existence observation is superseded and
    /// the result is Gone. An existence check is never allowed to outvote a later proof of absence.
    /// </summary>
    [Fact]
    public void Probe_Tier2ExistsThenLaterOpenReportsNoSuchProcess_IsGone()
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenFails(ErrorAccessDenied)
            .ExistenceIs(PidExistence.Exists)
            .OpenFails(NativeInterop.ERROR_INVALID_PARAMETER);

        var result = new ProcessProbe(backend).Probe(4245);

        Assert.Equal(ProcessLiveness.Gone, result.Status);
        Assert.Equal(1, backend.ExistenceChecks);

        // Tier 1 asked for wait rights; Tier 2 asked for query rights only.
        Assert.Equal(2, backend.RequestedAccess.Count);
        Assert.Equal(Tier1Access, backend.RequestedAccess[0]);
        Assert.Equal(NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION, backend.RequestedAccess[1]);
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// Contract 5: every form of Tier-2 uncertainty resolves to Unknown, never to AliveNoPath.
    ///
    /// This is the load-bearing invariant of the whole classifier. Tier 2 has only an existence
    /// observation followed by a later, separate observation - nothing in it proves the process
    /// seen by the existence check is still the process behind the later one. Letting any of these
    /// become "alive" would resurrect exactly the bug this repair removes: an unproven process
    /// registering a game on its name alone.
    /// </summary>
    [Theory]
    [InlineData("existence-unknown")]
    [InlineData("later-open-denied")]
    [InlineData("path-query-failed")]
    [InlineData("path-not-rooted")]
    public void Probe_Tier2Uncertainty_IsUnknownAndNeverAliveNoPath(string scenario)
    {
        var backend = new ScriptedProcessProbeBackend().OpenFails(ErrorAccessDenied);

        switch (scenario)
        {
            case "existence-unknown":
                backend.ExistenceIs(PidExistence.Unknown);
                break;
            case "later-open-denied":
                backend.ExistenceIs(PidExistence.Exists).OpenFails(ErrorAccessDenied);
                break;
            case "path-query-failed":
                backend.ExistenceIs(PidExistence.Exists).OpenSucceeds().PathQueryReturns(null);
                break;
            case "path-not-rooted":
                // A bare filename is not a location; accepting it would leak into the rooted-path
                // contract downstream.
                backend.ExistenceIs(PidExistence.Exists).OpenSucceeds().PathQueryReturns("r5apex.exe");
                break;
            default:
                throw new InvalidOperationException("Unknown scenario: " + scenario);
        }

        var result = new ProcessProbe(backend).Probe(4246);

        Assert.Equal(ProcessLiveness.Unknown, result.Status);
        Assert.NotEqual(ProcessLiveness.AliveNoPath, result.Status);
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.ObservedProcessName);
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// Tier 2 is unreachable from a Tier-1 open failure that already proves absence:
    /// ERROR_INVALID_PARAMETER is OpenProcess's "no process with that ID". Short-circuiting there is
    /// fail-closed (no match), so it is safe, and it must not be quietly re-classified as an
    /// access problem.
    /// </summary>
    [Fact]
    public void Probe_Tier1OpenReportsNoSuchProcess_IsGoneWithoutTier2()
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenFails(NativeInterop.ERROR_INVALID_PARAMETER);

        var result = new ProcessProbe(backend).Probe(4247);

        Assert.Equal(ProcessLiveness.Gone, result.Status);
        Assert.Equal(0, backend.ExistenceChecks);
        Assert.Single(backend.RequestedAccess);
    }

    /// <summary>
    /// The one route to AliveNoPath: liveness proven on a handle, path unreadable on that same
    /// handle. Both an outright query failure and an unusable (non-rooted) path land here, because
    /// the process is known to be alive either way.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("r5apex.exe")]
    [InlineData(@"\Device\HarddiskVolume4\Games\r5apex.exe")] // rooted, but names no usable location
    public void Probe_Tier1AliveWithUnusablePath_IsAliveNoPath(string? queriedPath)
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenSucceeds()
            .WaitReturns(NativeInterop.WAIT_TIMEOUT)
            .PathQueryReturns(queriedPath);

        var result = new ProcessProbe(backend).Probe(4248);

        Assert.Equal(ProcessLiveness.AliveNoPath, result.Status);

        // The classifier never fabricates a name: AliveNoPath carries no observed name, so a
        // caller that wants one has to supply its own independently-obtained evidence.
        Assert.Null(result.ExecutablePath);
        Assert.Null(result.ObservedProcessName);
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// Tier 2 can still resolve a path for a process that merely refused the combined-rights open
    /// (for example when SYNCHRONIZE is denied but query rights are not).
    /// </summary>
    [Fact]
    public void Probe_Tier2QueryRightsOpenReadsRootedPath_Resolves()
    {
        const string imagePath = @"D:\SteamLibrary\steamapps\common\CS2\game\bin\cs2.exe";

        var backend = new ScriptedProcessProbeBackend()
            .OpenFails(ErrorAccessDenied)
            .ExistenceIs(PidExistence.Exists)
            .OpenSucceeds()
            .PathQueryReturns(imagePath);

        var result = new ProcessProbe(backend).Probe(4249);

        Assert.Equal(ProcessLiveness.Resolved, result.Status);
        Assert.Equal(imagePath, result.ExecutablePath);
        Assert.Equal("cs2.exe", result.ObservedProcessName);
        backend.AssertAllHandlesClosed();
    }

    /// <summary>
    /// A PID the process table reports as absent is gone, without any further probing.
    /// </summary>
    [Fact]
    public void Probe_Tier2PidDoesNotExist_IsGone()
    {
        var backend = new ScriptedProcessProbeBackend()
            .OpenFails(ErrorAccessDenied)
            .ExistenceIs(PidExistence.Gone);

        var result = new ProcessProbe(backend).Probe(4250);

        Assert.Equal(ProcessLiveness.Gone, result.Status);
        Assert.Single(backend.RequestedAccess); // no second open attempted
    }

    /// <summary>
    /// Result-shape invariant across every non-resolved outcome: no path and no name may ever ride
    /// along, so a caller cannot accidentally treat a dead or unknown observation as identifying.
    /// </summary>
    [Fact]
    public void Probe_NonResolvedResults_CarryNoPathOrName()
    {
        var probe = new ProcessProbe(new ScriptedProcessProbeBackend()
            .OpenSucceeds().WaitReturns(NativeInterop.WAIT_OBJECT_0));
        AssertEmptyEvidence(probe.Probe(1));

        probe = new ProcessProbe(new ScriptedProcessProbeBackend()
            .OpenSucceeds().WaitReturns(NativeInterop.WAIT_FAILED));
        AssertEmptyEvidence(probe.Probe(2));

        probe = new ProcessProbe(new ScriptedProcessProbeBackend()
            .OpenSucceeds().WaitReturns(NativeInterop.WAIT_TIMEOUT).PathQueryReturns(null));
        AssertEmptyEvidence(probe.Probe(3));

        static void AssertEmptyEvidence(ProcessProbeResult result)
        {
            Assert.NotEqual(ProcessLiveness.Resolved, result.Status);
            Assert.Null(result.ExecutablePath);
            Assert.Null(result.ObservedProcessName);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Controlled production-backend corroboration.
    //
    // Deterministic backend tests cover the policy; these two cover the native shell itself (the
    // P/Invoke signatures and buffer handling), which a fake cannot exercise. They touch only this
    // test process and a PID proven not to exist - never an arbitrary real process, and never the
    // process table at large.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The real Win32 backend resolves this test process to its own rooted image path, proving
    /// QueryFullProcessImageNameW is declared and marshalled correctly.
    /// </summary>
    [Fact]
    public void Probe_ProductionBackendOnCurrentProcess_ResolvesOwnRootedImagePath()
    {
        var result = ProcessProbe.Default.Probe(Environment.ProcessId);

        Assert.Equal(ProcessLiveness.Resolved, result.Status);
        Assert.True(Path.IsPathRooted(result.ExecutablePath), "Resolved paths must be rooted.");

        using var self = Process.GetCurrentProcess();
        Assert.Equal(
            self.ProcessName,
            Path.GetFileNameWithoutExtension(result.ObservedProcessName),
            ignoreCase: true);
    }

    /// <summary>
    /// The real Win32 backend classifies a PID that provably does not exist as Gone, never as
    /// Unknown - the fail-closed end of the state machine on live APIs.
    /// </summary>
    [Fact]
    public void Probe_ProductionBackendOnNonexistentPid_IsGone()
    {
        var result = ProcessProbe.Default.Probe(FindNonexistentProcessId());

        Assert.Equal(ProcessLiveness.Gone, result.Status);
        Assert.Null(result.ExecutablePath);
    }

    /// <summary>
    /// Finds a PID that provably does not exist. Probes specific IDs far above the practical Windows
    /// PID range; it never calls <see cref="Process.GetProcesses()"/> and never inspects arbitrary
    /// real processes. Mirrors the helper in GameDetectorMatchingTests.
    /// </summary>
    private static int FindNonexistentProcessId()
    {
        for (int candidate = 0x7FFF_FFF0; candidate > 0x7FFF_FF00; candidate -= 4)
        {
            try
            {
                using var probe = Process.GetProcessById(candidate);
            }
            catch (ArgumentException)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No nonexistent PID could be found for the probe test.");
    }
}
