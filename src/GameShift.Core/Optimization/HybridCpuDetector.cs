using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Microsoft.Win32;

namespace GameShift.Core.Optimization;

/// <summary>
/// Detects P-cores vs E-cores on Intel hybrid CPUs and sets game process affinity to P-cores only.
/// On non-hybrid CPUs, IsAvailable returns false and optimization is skipped gracefully.
/// </summary>
public class HybridCpuDetector : IOptimization
{
    private bool _detectionComplete;
    private bool _isHybridCpu;
    private IntPtr _pCoreMask;
    private int _pCoreCount;
    private int _eCoreCount;
    private int _affinityProcessId;

    public string Name => "Hybrid CPU Optimizer";

    public string Description => "Detects P-cores on hybrid CPUs and sets game process affinity for optimal performance";

    public bool IsApplied { get; private set; }

    /// <summary>
    /// Returns true only if this is a hybrid CPU with distinct P-cores and E-cores.
    /// Caches the detection result to avoid re-reading registry.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (!_detectionComplete)
            {
                (var isHybrid, var pCoreMask, var pCount, var eCount) = DetectPCores();
                _isHybridCpu = isHybrid;
                _pCoreMask = pCoreMask;
                _pCoreCount = pCount;
                _eCoreCount = eCount;
                _detectionComplete = true;
            }
            return _isHybridCpu;
        }
    }

    /// <summary>
    /// Detects P-cores and E-cores by reading EfficiencyClass from processor registry keys.
    /// Returns (isHybrid, pCoreMask, pCoreCount, eCoreCount).
    /// </summary>
    private (bool isHybrid, IntPtr pCoreMask, int pCoreCount, int eCoreCount) DetectPCores()
    {
        try
        {
            const string baseKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor";
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cpuKey = hklm.OpenSubKey(baseKey);

            if (cpuKey == null)
            {
                SettingsManager.Logger.Warning("HybridCpuDetector: Cannot open processor registry key");
                return (false, IntPtr.Zero, 0, 0);
            }

            var pCoreIndices = new List<int>();
            var eCoreIndices = new List<int>();
            var processorNames = cpuKey.GetSubKeyNames();

            foreach (var procName in processorNames)
            {
                if (!int.TryParse(procName, out int coreIndex))
                    continue; // Skip non-numeric keys

                using var procKey = cpuKey.OpenSubKey(procName);
                if (procKey == null)
                    continue;

                // Read EfficiencyClass (DWORD value)
                // 0 = P-core (performance), >0 = E-core (efficiency)
                // Missing or null = assume P-core (legacy non-hybrid CPUs)
                var efficiencyClass = procKey.GetValue("EfficiencyClass");

                if (efficiencyClass == null || (efficiencyClass is int intValue && intValue == 0))
                {
                    pCoreIndices.Add(coreIndex);
                }
                else if (efficiencyClass is int ecValue && ecValue > 0)
                {
                    eCoreIndices.Add(coreIndex);
                }
                else
                {
                    // Unknown type or value - assume P-core for safety
                    pCoreIndices.Add(coreIndex);
                }
            }

            // If we have both P-cores and E-cores, it's a hybrid CPU
            bool isHybrid = pCoreIndices.Count > 0 && eCoreIndices.Count > 0;

            if (!isHybrid)
            {
                SettingsManager.Logger.Information(
                    "HybridCpuDetector: Non-hybrid CPU detected (P-cores: {PCount}, E-cores: {ECount})",
                    pCoreIndices.Count,
                    eCoreIndices.Count);
                return (false, IntPtr.Zero, pCoreIndices.Count, eCoreIndices.Count);
            }

            // Build P-core affinity mask
            long mask = 0;
            foreach (var coreIndex in pCoreIndices)
            {
                if (coreIndex < 64) // Ensure we don't overflow 64-bit mask
                {
                    mask |= (1L << coreIndex);
                }
            }

            SettingsManager.Logger.Information(
                "HybridCpuDetector: Hybrid CPU detected - P-cores: {PCount}, E-cores: {ECount}, P-core mask: 0x{Mask:X}",
                pCoreIndices.Count,
                eCoreIndices.Count,
                mask);

            return (true, new IntPtr(mask), pCoreIndices.Count, eCoreIndices.Count);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "HybridCpuDetector: Failed to detect CPU topology");
            return (false, IntPtr.Zero, 0, 0);
        }
    }

    /// <summary>
    /// Sets game process affinity to P-cores only.
    /// Records original affinity in snapshot before changing.
    /// </summary>
    public async Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        try
        {
            if (profile.ProcessId <= 0)
            {
                SettingsManager.Logger.Warning("HybridCpuDetector: No valid process ID in profile");
                return false;
            }

            Process? process;
            try
            {
                process = Process.GetProcessById(profile.ProcessId);
            }
            catch (ArgumentException)
            {
                SettingsManager.Logger.Warning(
                    "HybridCpuDetector: Game process {ProcessId} not found — may have exited",
                    profile.ProcessId);
                return false;
            }

            // Record original affinity before changing
            var originalAffinity = process.ProcessorAffinity;
            snapshot.RecordProcessAffinity(profile.ProcessId, originalAffinity);

            // Set affinity to P-cores only
            process.ProcessorAffinity = _pCoreMask;

            SettingsManager.Logger.Information(
                "HybridCpuDetector: Set process {ProcessName} (PID: {ProcessId}) affinity to P-cores only (mask: 0x{Mask:X}). P-cores: {PCount}, E-cores: {ECount}",
                process.ProcessName,
                profile.ProcessId,
                _pCoreMask.ToInt64(),
                _pCoreCount,
                _eCoreCount);

            _affinityProcessId = profile.ProcessId;
            IsApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "HybridCpuDetector: Failed to set process affinity");
            return false;
        }
    }

    /// <summary>
    /// Reverts process affinity to original value.
    /// Handles case where process has already exited gracefully.
    /// </summary>
    public async Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        await Task.CompletedTask; // Make async to satisfy interface

        try
        {
            if (_affinityProcessId <= 0)
            {
                return true; // Nothing to revert
            }

            // Check if we have the recorded original affinity
            if (!snapshot.ProcessAffinities.TryGetValue(_affinityProcessId, out var originalAffinity))
            {
                SettingsManager.Logger.Warning(
                    "HybridCpuDetector: No recorded affinity for PID {ProcessId}",
                    _affinityProcessId);
                return true; // Not a fatal error
            }

            Process? process;
            try
            {
                process = Process.GetProcessById(_affinityProcessId);
            }
            catch (ArgumentException)
            {
                SettingsManager.Logger.Information(
                    "HybridCpuDetector: Process {ProcessId} already exited, no revert needed",
                    _affinityProcessId);
                IsApplied = false;
                return true; // Clean exit - process is gone, nothing to revert
            }

            // Restore original affinity
            process.ProcessorAffinity = originalAffinity;

            SettingsManager.Logger.Information(
                "HybridCpuDetector: Reverted process {ProcessName} affinity to original mask (0x{Mask:X})",
                process.ProcessName,
                originalAffinity.ToInt64());

            IsApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "HybridCpuDetector: Failed to revert affinity");
            IsApplied = false; // Mark as not applied even on failure
            return false;
        }
    }
}
