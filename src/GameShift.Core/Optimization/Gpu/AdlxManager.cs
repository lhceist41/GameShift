using System.Runtime.InteropServices;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Optimization.Gpu;

// Stub: ADLX integration not yet implemented. IsAvailable is always false.
/// <summary>
/// AMD ADLX integration for Anti-Lag and GPU metrics.
///
/// Uses a graceful-degradation approach:
///   1. Try to load <c>amdadlx64.dll</c> and call ADLX APIs for Anti-Lag control
///   2. If ADLX is unavailable (DLL missing, driver too old), fall back to
///      registry-based Anti-Lag control in the caller (<see cref="GpuDriverOptimizer"/>)
///
/// <see cref="IsAvailable"/> is false when the DLL cannot be loaded. All methods
/// return gracefully without throwing when unavailable.
/// </summary>
public sealed class AdlxManager : IDisposable
{
    private readonly ILogger _logger = SettingsManager.Logger;

    // ADLX function IDs (via ADLXHelper COM-like interface)
    // The ADLX SDK exposes IADLXSystem → IADLX3DSettingsServices → IADLX3DAntiLag

    private IntPtr _adlxModule;
    private bool _initialized;

    /// <summary>True if amdadlx64.dll loaded and ADLX initialized.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Whether Anti-Lag was enabled by this instance (for revert).</summary>
    public bool AntiLagApplied { get; private set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    public AdlxManager()
    {
        try
        {
            // Attempt to load the ADLX runtime library
            _adlxModule = LoadLibraryW("amdadlx64.dll");
            if (_adlxModule == IntPtr.Zero)
            {
                _logger.Debug("[AdlxManager] amdadlx64.dll not found — AMD ADLX not available. " +
                              "Falling back to registry-based AMD optimizations.");
                return;
            }

            // ADLX initialization would proceed here:
            // 1. GetProcAddress for ADLXHelper_Initialize
            // 2. Initialize ADLX system interface
            // 3. Get IADLX3DSettingsServices
            //
            // Full ADLX COM-like interface binding is deferred to a future sprint.
            // For now, the presence of the DLL is detected but operations use registry fallback.

            _logger.Information(
                "[AdlxManager] amdadlx64.dll loaded. Full ADLX API binding is pending — " +
                "using registry-based AMD optimizations for now.");

            _initialized = true;
            // IsAvailable remains false until full ADLX initialization is implemented
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[AdlxManager] ADLX initialization failed");
        }
    }

    /// <summary>
    /// Enables AMD Anti-Lag via ADLX. Returns false if ADLX is unavailable.
    /// When false, the caller should fall back to the registry-based approach.
    /// </summary>
    public bool EnableAntiLag()
    {
        if (!IsAvailable)
        {
            _logger.Debug("[AdlxManager] EnableAntiLag skipped — ADLX not available, caller should use registry fallback");
            return false;
        }

        // Full implementation:
        // var system = adlxHelper.GetSystem();
        // var settingsServices = system.Get3DSettingsServices();
        // var antiLag = settingsServices.Get3DAntiLag();
        // antiLag.SetEnabled(true);

        AntiLagApplied = true;
        return true;
    }

    /// <summary>
    /// Disables AMD Anti-Lag via ADLX.
    /// </summary>
    public bool DisableAntiLag()
    {
        if (!IsAvailable || !AntiLagApplied)
            return false;

        AntiLagApplied = false;
        return true;
    }

    public void Dispose()
    {
        if (_initialized && _adlxModule != IntPtr.Zero)
        {
            try { FreeLibrary(_adlxModule); }
            catch { /* Best-effort */ }
            _adlxModule = IntPtr.Zero;
            _initialized = false;
            IsAvailable = false;
        }
    }
}
