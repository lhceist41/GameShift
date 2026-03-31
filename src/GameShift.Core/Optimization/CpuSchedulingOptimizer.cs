using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// Assigns background processes to E-cores via <c>SetProcessDefaultCpuSetMasks</c> and
/// controls power throttling (HighQoS for the game, EcoQoS for background) during gaming.
///
/// <para>On hybrid CPUs (Intel 12th–15th gen, AMD Zen 5c):</para>
/// <list type="bullet">
///   <item>Game process → P-core CPU Sets via GROUP_AFFINITY masks (soft affinity)</item>
///   <item>Background processes → E-core CPU Sets via GROUP_AFFINITY masks</item>
///   <item>Game process → HighQoS (StateMask = 0, disable power throttling)</item>
///   <item>Background processes → EcoQoS (StateMask = EXECUTION_SPEED)</item>
/// </list>
///
/// <para>On non-hybrid CPUs (all EfficiencyClass values identical):</para>
/// <list type="bullet">
///   <item>CPU Set assignment is skipped entirely — existing parking/affinity logic applies</item>
///   <item>HighQoS on the game process still applies (works on any modern CPU)</item>
/// </list>
///
/// Requires Windows 11 22H2+ (build 22621) for <c>SetProcessDefaultCpuSetMasks</c>.
/// Falls back to power-throttling-only on older builds.
/// </summary>
public class CpuSchedulingOptimizer : IOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    public const string OptimizationId = "CPU Scheduling Optimizer";
    public string Name => OptimizationId;
    public string Description =>
        "Routes background processes to E-cores and ensures the game process gets HighQoS scheduling";

    public bool IsApplied { get; private set; }
    public bool IsAvailable => true; // HighQoS works on all modern CPUs

    // ── Topology ──────────────────────────────────────────────────────────────

    private bool _isHybrid;
    private bool _hasCpuSetMasksApi; // Win11 22H2+
    private bool _hasReservedCores; // ReservedCpuSets active (reboot-applied)
    private NativeInterop.GROUP_AFFINITY[]? _pCoreMasks;
    private NativeInterop.GROUP_AFFINITY[]? _eCoreMasks;
    private NativeInterop.GROUP_AFFINITY[]? _reservedMasks; // Cores reserved for game
    private NativeInterop.GROUP_AFFINITY[]? _unreservedMasks; // Non-reserved for background

    // ── Applied state for revert ──────────────────────────────────────────────

    private int _gamePid;
    private bool _gameHighQosApplied;
    private bool _gameCpuSetApplied;
    private readonly List<int> _bgCpuSetPids = new();
    private readonly List<int> _bgThrottlePids = new();
    private readonly object _lock = new();

    // ── IOptimization ─────────────────────────────────────────────────────────

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        try
        {
            DetectTopologyAndCapabilities();

            if (!_isHybrid)
            {
                _logger.Information(
                    "[CpuSchedulingOptimizer] Non-hybrid CPU — skipping CPU Set assignment, applying HighQoS only");
            }
            else if (_hasReservedCores)
            {
                _logger.Information(
                    "[CpuSchedulingOptimizer] Reserved cores active — game→reserved P-cores, background→unreserved cores");
            }
            else
            {
                _logger.Information(
                    "[CpuSchedulingOptimizer] Hybrid CPU detected — assigning game→P-cores, background→E-cores");
            }

            // ── Game process: P-core affinity + HighQoS ───────────────────────
            _gamePid = profile.ProcessId;

            if (_gamePid > 0)
            {
                try
                {
                    using var gameProc = Process.GetProcessById(_gamePid);
                    var hGame = NativeInterop.OpenProcess(
                        NativeInterop.PROCESS_SET_INFORMATION | NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION,
                        false, _gamePid);

                    if (hGame != IntPtr.Zero)
                    {
                        try
                        {
                            // CPU Set assignment (hybrid only)
                            if (_isHybrid && _hasCpuSetMasksApi)
                            {
                                // Use reserved cores if active, otherwise all P-cores
                                var gameMasks = _hasReservedCores && _reservedMasks?.Length > 0
                                    ? _reservedMasks
                                    : _pCoreMasks;

                                if (gameMasks != null && gameMasks.Length > 0 &&
                                    NativeInterop.SetProcessDefaultCpuSetMasks(
                                        hGame, gameMasks, (ushort)gameMasks.Length))
                                {
                                    _gameCpuSetApplied = true;
                                    _logger.Information(
                                        "[CpuSchedulingOptimizer] Game (PID {Pid}) → {Type} CPU Sets ({Count} groups)",
                                        _gamePid,
                                        _hasReservedCores ? "reserved P-core" : "P-core",
                                        gameMasks.Length);
                                }
                                else if (gameMasks?.Length > 0)
                                {
                                    _logger.Warning(
                                        "[CpuSchedulingOptimizer] SetProcessDefaultCpuSetMasks failed for game (error {Err})",
                                        Marshal.GetLastWin32Error());
                                }
                            }

                            // HighQoS: disable power throttling
                            ApplyPowerThrottling(hGame, disableThrottling: true);
                            _gameHighQosApplied = true;
                            _logger.Information(
                                "[CpuSchedulingOptimizer] Game (PID {Pid}) → HighQoS (power throttling disabled)",
                                _gamePid);
                        }
                        finally
                        {
                            NativeInterop.CloseHandle(hGame);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    _logger.Warning("[CpuSchedulingOptimizer] Game process {Pid} not found", _gamePid);
                }
            }

            // ── Background processes: E-core affinity + EcoQoS ────────────────
            var gameProcessNames = ResolveGameProcessNames(profile);
            AssignBackgroundProcesses(gameProcessNames);

            IsApplied = true;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CpuSchedulingOptimizer] Apply failed");
            return Task.FromResult(false);
        }
    }

    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        try
        {
            _logger.Information("[CpuSchedulingOptimizer] Reverting CPU scheduling changes");

            // Revert game CPU Sets
            if (_gameCpuSetApplied && _gamePid > 0)
            {
                try
                {
                    using var gameProc = Process.GetProcessById(_gamePid);
                    var hGame = NativeInterop.OpenProcess(
                        NativeInterop.PROCESS_SET_INFORMATION, false, _gamePid);
                    if (hGame != IntPtr.Zero)
                    {
                        NativeInterop.SetProcessDefaultCpuSetMasks(hGame, null, 0);
                        NativeInterop.CloseHandle(hGame);
                    }
                }
                catch (ArgumentException)
                {
                    // Game already exited
                }
                _gameCpuSetApplied = false;
            }

            // Revert game HighQoS
            if (_gameHighQosApplied && _gamePid > 0)
            {
                try
                {
                    using var gameProc = Process.GetProcessById(_gamePid);
                    var hGame = NativeInterop.OpenProcess(
                        NativeInterop.PROCESS_SET_INFORMATION, false, _gamePid);
                    if (hGame != IntPtr.Zero)
                    {
                        // Reset: clear ControlMask to let OS manage throttling again
                        ResetPowerThrottling(hGame);
                        NativeInterop.CloseHandle(hGame);
                    }
                }
                catch (ArgumentException)
                {
                    // Game already exited
                }
                _gameHighQosApplied = false;
            }

            // Revert background CPU Sets
            lock (_lock)
            {
                foreach (var pid in _bgCpuSetPids)
                {
                    try
                    {
                        var h = NativeInterop.OpenProcess(
                            NativeInterop.PROCESS_SET_INFORMATION, false, pid);
                        if (h != IntPtr.Zero)
                        {
                            NativeInterop.SetProcessDefaultCpuSetMasks(h, null, 0);
                            NativeInterop.CloseHandle(h);
                        }
                    }
                    catch { /* Process may have exited */ }
                }
                _bgCpuSetPids.Clear();

                foreach (var pid in _bgThrottlePids)
                {
                    try
                    {
                        var h = NativeInterop.OpenProcess(
                            NativeInterop.PROCESS_SET_INFORMATION, false, pid);
                        if (h != IntPtr.Zero)
                        {
                            ResetPowerThrottling(h);
                            NativeInterop.CloseHandle(h);
                        }
                    }
                    catch { /* Process may have exited */ }
                }
                _bgThrottlePids.Clear();
            }

            _logger.Information("[CpuSchedulingOptimizer] Revert complete");
            IsApplied = false;
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CpuSchedulingOptimizer] Revert failed");
            IsApplied = false;
            return Task.FromResult(false);
        }
    }

    // ── Topology detection ────────────────────────────────────────────────────

    private void DetectTopologyAndCapabilities()
    {
        // Check if SetProcessDefaultCpuSetMasks is available (Win11 22H2+, build 22621)
        int buildNumber = GetWindowsBuildNumber();
        _hasCpuSetMasksApi = buildNumber >= 22621;

        if (!_hasCpuSetMasksApi)
        {
            _logger.Debug(
                "[CpuSchedulingOptimizer] Build {Build} < 22621 — SetProcessDefaultCpuSetMasks unavailable",
                buildNumber);
        }

        // Enumerate CPU Sets
        NativeInterop.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero, 0);
        if (requiredSize == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            if (!NativeInterop.GetSystemCpuSetInformation(buffer, requiredSize, out _, IntPtr.Zero, 0))
                return;

            var pCores = new List<(ushort Group, byte LogicalIndex, uint CpuSetId)>();
            var eCores = new List<(ushort Group, byte LogicalIndex, uint CpuSetId)>();
            var allEfficiencyClasses = new HashSet<byte>();

            int offset = 0;
            while (offset < (int)requiredSize)
            {
                var info = Marshal.PtrToStructure<NativeInterop.SYSTEM_CPU_SET_INFORMATION>(buffer + offset);

                if (info.Type == 0) // CpuSetInformation
                {
                    allEfficiencyClasses.Add(info.EfficiencyClass);

                    if (info.EfficiencyClass == 0)
                        pCores.Add((info.Group, info.LogicalProcessorIndex, info.Id));
                    else
                        eCores.Add((info.Group, info.LogicalProcessorIndex, info.Id));
                }

                offset += (int)info.Size;
                if (info.Size == 0) break;
            }

            // Non-hybrid: all cores have the same EfficiencyClass → skip CPU Sets
            _isHybrid = allEfficiencyClasses.Count > 1;

            if (_isHybrid)
            {
                _pCoreMasks = BuildGroupAffinityMasks(
                    pCores.Select(c => (c.Group, c.LogicalIndex)).ToList());
                _eCoreMasks = BuildGroupAffinityMasks(
                    eCores.Select(c => (c.Group, c.LogicalIndex)).ToList());

                // Check for active core reservation (Sprint 5C)
                var reservedIds = CoreIsolationManager.ReadCurrentReservation();
                if (reservedIds.Count > 0)
                {
                    // Match by CpuSetId (the bitmask bit positions correspond to CPU Set IDs)
                    var reservedCores = pCores.Where(c => reservedIds.Contains(c.CpuSetId)).ToList();
                    var unreservedCores = pCores.Where(c => !reservedIds.Contains(c.CpuSetId))
                        .Concat(eCores).ToList();

                    if (reservedCores.Count > 0)
                    {
                        _hasReservedCores = true;
                        _reservedMasks = BuildGroupAffinityMasks(
                            reservedCores.Select(c => (c.Group, c.LogicalIndex)).ToList());
                        _unreservedMasks = BuildGroupAffinityMasks(
                            unreservedCores.Select(c => (c.Group, c.LogicalIndex)).ToList());

                        _logger.Information(
                            "[CpuSchedulingOptimizer] Core reservation active: {Reserved} reserved, {Unreserved} unreserved",
                            reservedCores.Count, unreservedCores.Count);
                    }
                }

                _logger.Information(
                    "[CpuSchedulingOptimizer] Topology: {P} P-cores, {E} E-cores, {PGroups} P-groups, {EGroups} E-groups",
                    pCores.Count, eCores.Count, _pCoreMasks.Length, _eCoreMasks.Length);
            }
            else
            {
                _logger.Information(
                    "[CpuSchedulingOptimizer] Non-hybrid CPU ({Count} cores, all EfficiencyClass {Class})",
                    pCores.Count + eCores.Count,
                    allEfficiencyClasses.FirstOrDefault());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Groups cores by their processor group and builds a GROUP_AFFINITY mask per group.
    /// </summary>
    private static NativeInterop.GROUP_AFFINITY[] BuildGroupAffinityMasks(
        List<(ushort Group, byte LogicalIndex)> cores)
    {
        return cores
            .GroupBy(c => c.Group)
            .Select(g => new NativeInterop.GROUP_AFFINITY
            {
                Group = g.Key,
                Mask = (UIntPtr)g.Aggregate(0UL, (mask, c) => mask | (1UL << c.LogicalIndex))
            })
            .ToArray();
    }

    // ── Background process assignment ─────────────────────────────────────────

    private void AssignBackgroundProcesses(string[] gameProcessNames)
    {
        int cpuSetCount = 0;
        int throttleCount = 0;

        foreach (var process in ProcessSnapshotService.GetProcesses())
        {
            try
            {
                if (!BackgroundProcessTargets.ShouldDemote(process.ProcessName, gameProcessNames))
                    continue;

                var hProc = NativeInterop.OpenProcess(
                    NativeInterop.PROCESS_SET_INFORMATION | NativeInterop.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, process.Id);

                if (hProc == IntPtr.Zero)
                    continue;

                try
                {
                    // Background → E-cores (or unreserved cores when reservation active)
                    var bgMasks = _hasReservedCores && _unreservedMasks?.Length > 0
                        ? _unreservedMasks
                        : _eCoreMasks;
                    if (_isHybrid && _hasCpuSetMasksApi && bgMasks != null && bgMasks.Length > 0)
                    {
                        if (NativeInterop.SetProcessDefaultCpuSetMasks(
                                hProc, bgMasks, (ushort)bgMasks.Length))
                        {
                            lock (_lock) { _bgCpuSetPids.Add(process.Id); }
                            cpuSetCount++;
                        }
                    }

                    // EcoQoS: enable power throttling
                    ApplyPowerThrottling(hProc, disableThrottling: false);
                    lock (_lock) { _bgThrottlePids.Add(process.Id); }
                    throttleCount++;
                }
                finally
                {
                    NativeInterop.CloseHandle(hProc);
                }
            }
            catch
            {
                // Access denied on system processes — expected
            }
        }

        _logger.Information(
            "[CpuSchedulingOptimizer] Background processes: {CpuSet} → E-cores, {Throttle} → EcoQoS",
            cpuSetCount, throttleCount);
    }

    // ── Power throttling helpers ──────────────────────────────────────────────

    /// <summary>
    /// Sets power throttling on a process handle.
    /// <paramref name="disableThrottling"/> = true → HighQoS (game); false → EcoQoS (background).
    /// </summary>
    private static void ApplyPowerThrottling(IntPtr hProcess, bool disableThrottling)
    {
        var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
        {
            Version     = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask   = disableThrottling ? 0 : NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        };

        int size = Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            NativeInterop.SetProcessInformation(
                hProcess,
                NativeInterop.ProcessPowerThrottling,
                ptr, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Resets power throttling to OS-managed default (ControlMask = 0).
    /// </summary>
    private static void ResetPowerThrottling(IntPtr hProcess)
    {
        var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
        {
            Version     = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = 0,  // Clear control — let OS manage
            StateMask   = 0
        };

        int size = Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            NativeInterop.SetProcessInformation(
                hProcess,
                NativeInterop.ProcessPowerThrottling,
                ptr, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] ResolveGameProcessNames(GameProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.ExecutableName))
            return new[] { Path.GetFileNameWithoutExtension(profile.ExecutableName) };
        return Array.Empty<string>();
    }

    private static int GetWindowsBuildNumber()
    {
        try
        {
            var val = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuildNumber", "0");
            return int.TryParse(val?.ToString(), out var build) ? build : 0;
        }
        catch
        {
            return 0;
        }
    }
}
