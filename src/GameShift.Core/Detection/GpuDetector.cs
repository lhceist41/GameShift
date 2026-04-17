using System;
using System.Management;
using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// Detects GPU name via WMI query on Win32_VideoController.
/// Single source of truth for GPU identification — used by DashboardViewModel
/// at startup and HardwareScanner for auto-detection.
/// Thread-safe: caches the result after first successful query.
/// </summary>
public static class GpuDetector
{
    private static string? _cachedGpuName;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the primary discrete GPU name via WMI.
    /// Skips Microsoft Basic Display Adapter and similar virtual adapters.
    /// Caches the result after first query. Returns "Unknown" if detection fails.
    /// </summary>
    public static string GetGpuName()
    {
        lock (_lock)
        {
            if (_cachedGpuName != null) return _cachedGpuName;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterCompatibility FROM Win32_VideoController");

                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        var name = obj["Name"]?.ToString();
                        var compat = obj["AdapterCompatibility"]?.ToString();

                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Skip virtual/basic display adapters
                        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                            continue;

                        _cachedGpuName = name;
                        Log.Debug("GpuDetector: Detected GPU — {GpuName} ({Compat})", name, compat ?? "n/a");
                        return name;
                    }
                    finally
                    {
                        obj.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GpuDetector: WMI query for GPU detection failed");
            }

            _cachedGpuName = "Unknown";
            return _cachedGpuName;
        }
    }

}
