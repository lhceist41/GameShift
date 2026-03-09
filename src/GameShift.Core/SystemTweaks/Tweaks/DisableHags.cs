using System.Text.Json;
using GameShift.Core.Detection;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// HAGS recommendation states for the context-aware toggle.
/// </summary>
public enum HagsRecommendation
{
    /// <summary>Recommend enabling HAGS (Frame Gen capable or well-supported GPU + stable Windows).</summary>
    Enable,
    /// <summary>Recommend disabling HAGS (older GPU, stability concerns).</summary>
    Disable,
    /// <summary>No strong recommendation — user should test both.</summary>
    NoChange
}

/// <summary>
/// Context-aware HAGS (Hardware Accelerated GPU Scheduling) toggle.
///
/// In 2022-2023 HAGS caused more problems than it solved, so this was a simple "disable" tweak.
/// In 2025-2026, HAGS is REQUIRED for Frame Generation (DLSS FG, AFMF 2, XeSS FG).
/// Disabling HAGS now means losing 50-100% FPS uplift from Frame Gen.
///
/// This redesign provides context-aware recommendations based on:
///   1. GPU generation — newer GPUs handle HAGS well
///   2. Frame Generation capability — RTX 40/50, all RDNA2+ support FG
///   3. Windows version — HAGS stabilized by 24H2
///
/// "Apply All Recommended" follows the recommendation engine (not always disable).
/// Requires reboot.
/// </summary>
public class DisableHags : ISystemTweak
{
    public string Name => "HAGS (Hardware Accelerated GPU Scheduling)";
    public string Description => RecommendationReason;
    public string Category => "GPU";
    public bool RequiresReboot => true;

    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string ValueName = "HwSchMode";

    /// <summary>Current recommendation for this system.</summary>
    public HagsRecommendation Recommendation { get; private set; } = HagsRecommendation.NoChange;

    /// <summary>Human-readable reason for the recommendation.</summary>
    public string RecommendationReason { get; private set; } = "Manages HAGS based on your GPU generation and Frame Generation support.";

    /// <summary>Whether the current GPU supports Frame Generation (DLSS FG / AFMF).</summary>
    public bool IsFrameGenCapable { get; private set; }

    /// <summary>Detected GPU generation.</summary>
    public GpuGeneration DetectedGpuGeneration { get; private set; } = GpuGeneration.Unknown;

    /// <summary>
    /// Whether HAGS is currently enabled (HwSchMode = 2).
    /// </summary>
    public bool IsHagsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
                var val = key?.GetValue(ValueName);
                return val is int i && i == 2;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// DetectIsApplied returns true when HAGS matches the recommendation.
    /// For the "Disable" recommendation: applied = HAGS disabled (HwSchMode=1).
    /// For the "Enable" recommendation: applied = HAGS enabled (HwSchMode=2).
    /// For "NoChange": always considered applied (no action needed).
    /// </summary>
    public bool DetectIsApplied()
    {
        EvaluateRecommendation();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            var val = key?.GetValue(ValueName);
            int hwSchMode = val is int i ? i : 2; // Default is enabled

            return Recommendation switch
            {
                HagsRecommendation.Disable => hwSchMode == 1,
                HagsRecommendation.Enable => hwSchMode == 2,
                HagsRecommendation.NoChange => true, // Always "applied" — nothing to do
                _ => false
            };
        }
        catch { return false; }
    }

    public string? Apply()
    {
        EvaluateRecommendation();

        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        var original = key.GetValue(ValueName);

        int targetValue = Recommendation switch
        {
            HagsRecommendation.Disable => 1,
            HagsRecommendation.Enable => 2,
            _ => 2 // NoChange shouldn't get here, but default to enabled
        };

        key.SetValue(ValueName, targetValue, RegistryValueKind.DWord);

        Log.Information("[HAGS] Set HwSchMode to {Value} (recommendation: {Rec})", targetValue, Recommendation);
        return JsonSerializer.Serialize(new { HwSchMode = original });
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(originalValuesJson);
            var val = doc.RootElement.GetProperty("HwSchMode");
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key == null) return false;
            if (val.ValueKind == JsonValueKind.Null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, val.GetInt32(), RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Evaluates the HAGS recommendation based on GPU generation, Frame Gen capability,
    /// and Windows version stability.
    /// </summary>
    public void EvaluateRecommendation()
    {
        string gpuName;
        try
        {
            gpuName = GpuDetector.GetGpuName();
        }
        catch
        {
            gpuName = "Unknown";
        }

        DetectedGpuGeneration = HardwareScanner.DetectGpuGeneration(gpuName);

        // Factor 1: GPU generation — HAGS works best on modern architectures
        bool gpuSupportsHagsWell = DetectedGpuGeneration switch
        {
            GpuGeneration.NvidiaRtx30 or GpuGeneration.NvidiaRtx40 or GpuGeneration.NvidiaRtx50 => true,
            GpuGeneration.AmdRdna2 or GpuGeneration.AmdRdna3 or GpuGeneration.AmdRdna4 => true,
            GpuGeneration.IntelArcAlchemist or GpuGeneration.IntelArcBattlemage => true,
            _ => false
        };

        // Factor 2: Frame Generation capability
        IsFrameGenCapable = DetectedGpuGeneration switch
        {
            GpuGeneration.NvidiaRtx40 or GpuGeneration.NvidiaRtx50 => true,  // DLSS 3+ Frame Generation
            GpuGeneration.AmdRdna2 or GpuGeneration.AmdRdna3 or GpuGeneration.AmdRdna4 => true, // AFMF
            _ => false
        };

        // Factor 3: Windows version stability
        int build = Environment.OSVersion.Version.Build;
        bool windowsHagsStable = build >= 26100; // 24H2+

        // Decision matrix
        if (IsFrameGenCapable)
        {
            Recommendation = HagsRecommendation.Enable;
            RecommendationReason = "Your GPU supports Frame Generation (DLSS FG / AFMF), " +
                "which requires HAGS enabled. Disabling HAGS will prevent Frame Generation from working.";
        }
        else if (gpuSupportsHagsWell && windowsHagsStable)
        {
            Recommendation = HagsRecommendation.Enable;
            RecommendationReason = "HAGS is well-supported on your GPU and Windows version. " +
                "It reduces CPU overhead for GPU scheduling. Recommended to keep enabled.";
        }
        else if (!gpuSupportsHagsWell)
        {
            Recommendation = HagsRecommendation.Disable;
            RecommendationReason = "Your GPU generation may not fully benefit from HAGS. " +
                "Disabling it can improve stability and reduce input lag on older hardware.";
        }
        else
        {
            Recommendation = HagsRecommendation.NoChange;
            RecommendationReason = "No strong recommendation for your configuration. " +
                "Test both settings and use whichever gives better frame consistency.";
        }
    }
}
