namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Information about a PCI device's interrupt configuration.
/// Populated by InterruptAffinityManager.ScanDevices() from registry enumeration.
/// </summary>
public class PciDeviceInterruptInfo
{
    /// <summary>PCI device ID (e.g., "VEN_10DE&DEV_2684&...").</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>PCI instance ID within the device.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Device description from registry (cleaned of INF prefix).</summary>
    public string DeviceDescription { get; set; } = "";

    /// <summary>Friendly name from registry (may be empty).</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>Whether this device is a display adapter (GPU).</summary>
    public bool IsGpu { get; set; }

    /// <summary>Whether this device is a network adapter (NIC).</summary>
    public bool IsNic { get; set; }

    /// <summary>Whether this device is a USB host controller (xHCI/EHCI).</summary>
    public bool IsUsb { get; set; }

    // ── MSI state ───────────────────────────────────────────────────────

    /// <summary>Hardware supports MSI (MessageSignaledInterruptProperties key exists).</summary>
    public bool MsiSupported { get; set; }

    /// <summary>MSI is currently enabled (MSISupported = 1).</summary>
    public bool MsiEnabled { get; set; }

    /// <summary>Maximum number of MSI messages, if set.</summary>
    public int? MessageNumberLimit { get; set; }

    // ── Affinity state ──────────────────────────────────────────────────

    /// <summary>Current DevicePolicy value (null = not set).</summary>
    public int? DevicePolicy { get; set; }

    /// <summary>Current AssignmentSetOverride bitmask (null = not set).</summary>
    public byte[]? CurrentAffinityMask { get; set; }

    /// <summary>Base registry path for this device instance.</summary>
    public string RegistryBasePath { get; set; } = "";

    // ── Recommendations ─────────────────────────────────────────────────

    /// <summary>Whether MSI should be enabled on this device.</summary>
    public bool ShouldEnableMsi => MsiSupported && !MsiEnabled;

    /// <summary>Whether interrupt affinity should be changed (not already pinned).</summary>
    public bool ShouldChangeAffinity => DevicePolicy == null || DevicePolicy != 4; // 4 = SpecifiedProcessors

    /// <summary>Display name — friendly name if available, otherwise device description.</summary>
    public string DisplayName => !string.IsNullOrEmpty(FriendlyName) ? FriendlyName : DeviceDescription;
}
