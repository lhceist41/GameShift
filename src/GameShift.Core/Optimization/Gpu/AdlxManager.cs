using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Optimization.Gpu;

/// <summary>
/// AMD ADLX integration point for Anti-Lag. The ADLX API binding is not implemented, so
/// <see cref="IsAvailable"/> is always false and the caller (<see cref="GpuDriverOptimizer"/>)
/// uses its registry-based Anti-Lag control. All methods return gracefully without throwing.
/// </summary>
public sealed class AdlxManager : IDisposable
{
    // amdadlx64.dll is deliberately not loaded: GameShift runs elevated and must not load a
    // native library that no code path uses.

    private readonly ILogger _logger = SettingsManager.Logger;

    /// <summary>True if ADLX initialized. Always false until an ADLX binding exists.</summary>
    public bool IsAvailable => false;

    /// <summary>Whether Anti-Lag was enabled by this instance (for revert).</summary>
    public bool AntiLagApplied => false;

    /// <summary>
    /// Enables AMD Anti-Lag via ADLX. Returns false if ADLX is unavailable.
    /// When false, the caller should fall back to the registry-based approach.
    /// </summary>
    public bool EnableAntiLag()
    {
        _logger.Debug("[AdlxManager] EnableAntiLag skipped - ADLX not available, caller should use registry fallback");
        return false;
    }

    /// <summary>
    /// Disables AMD Anti-Lag via ADLX.
    /// </summary>
    public bool DisableAntiLag() => false;

    public void Dispose()
    {
    }
}
