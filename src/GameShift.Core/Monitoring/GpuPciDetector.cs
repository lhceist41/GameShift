using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Result of GPU PCI device detection for MSI mode configuration.
/// </summary>
public class GpuMsiInfo
{
    public string Vendor { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public bool MsiEnabled { get; init; }
    public bool KeyExists { get; init; }
}

/// <summary>
/// Detects GPU PCI devices and reads their MSI mode state from the registry.
/// Enumerates HKLM\SYSTEM\CurrentControlSet\Enum\PCI for NVIDIA (VEN_10DE)
/// and AMD (VEN_1002) devices.
/// </summary>
public static class GpuPciDetector
{
    private const string PciEnumPath = @"SYSTEM\CurrentControlSet\Enum\PCI";
    private const string MsiSubPath = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    /// <summary>
    /// Detects the primary GPU and returns its MSI mode state.
    /// Returns null if no NVIDIA or AMD GPU is found.
    /// </summary>
    public static GpuMsiInfo? DetectGpuMsiState()
    {
        try
        {
            using var pciKey = Registry.LocalMachine.OpenSubKey(PciEnumPath);
            if (pciKey == null) return null;

            foreach (var deviceKeyName in pciKey.GetSubKeyNames())
            {
                var upperName = deviceKeyName.ToUpperInvariant();
                string? vendor = null;

                if (upperName.Contains("VEN_10DE"))
                    vendor = "NVIDIA";
                else if (upperName.Contains("VEN_1002"))
                    vendor = "AMD";

                if (vendor == null) continue;

                using var deviceKey = pciKey.OpenSubKey(deviceKeyName);
                if (deviceKey == null) continue;

                // Each PCI device can have multiple instances (e.g., "0000")
                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    var fullPath = $@"{PciEnumPath}\{deviceKeyName}\{instanceName}";

                    // Check if this instance has the MSI properties key
                    var msiRegPath = $@"{fullPath}\{MsiSubPath}";

                    using var msiKey = Registry.LocalMachine.OpenSubKey(msiRegPath);
                    bool keyExists = msiKey != null;
                    bool msiEnabled = false;

                    if (msiKey != null)
                    {
                        var val = msiKey.GetValue("MSISupported");
                        if (val is int intVal)
                            msiEnabled = intVal == 1;
                    }

                    Log.Debug("GpuPciDetector: found {Vendor} GPU at {Path}, MSI key exists: {Exists}, enabled: {Enabled}",
                        vendor, fullPath, keyExists, msiEnabled);

                    return new GpuMsiInfo
                    {
                        Vendor = vendor,
                        DeviceId = deviceKeyName,
                        RegistryPath = $@"HKLM\{msiRegPath}",
                        MsiEnabled = msiEnabled,
                        KeyExists = keyExists
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GpuPciDetector: failed to enumerate PCI devices");
        }

        return null;
    }
}
