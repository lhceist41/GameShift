using System;
using System.Collections.Generic;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Severity level for a known DPC offender.
/// </summary>
public enum DpcSeverity
{
    Low,
    Medium,
    High
}

/// <summary>
/// Category of the driver component.
/// </summary>
public enum DriverCategory
{
    Audio,
    Network,
    GPU,
    Storage,
    USB,
    Framework,
    Other
}

/// <summary>
/// A known DPC-problematic driver with fix recommendations.
/// </summary>
public class DpcOffenderEntry
{
    public string DriverFileName { get; init; } = "";
    public string ComponentName { get; init; } = "";
    public DriverCategory Category { get; init; }
    public DpcSeverity Severity { get; init; }
    public string Problem { get; init; } = "";
    public string[] FixSteps { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Static database of known DPC-problematic drivers with fix recommendations.
/// Cross-referenced by DpcTroubleshooter during driver analysis.
/// </summary>
public static class KnownDpcOffenders
{
    private static readonly Dictionary<string, DpcOffenderEntry> _offenders =
        new(StringComparer.OrdinalIgnoreCase);

    static KnownDpcOffenders()
    {
        Register(new DpcOffenderEntry
        {
            DriverFileName = "nvlddmkm.sys",
            ComponentName = "NVIDIA Display Driver",
            Category = DriverCategory.GPU,
            Severity = DpcSeverity.High,
            Problem = "NVIDIA kernel driver with known DPC latency issues, especially with HDCP and older versions",
            FixSteps = new[]
            {
                "Update to latest NVIDIA driver from nvidia.com/drivers",
                "Disable HDCP in NVIDIA Control Panel if not needed",
                "Disable Hardware Accelerated GPU Scheduling in Windows settings",
                "Set Power Management Mode to 'Prefer Maximum Performance' in NVIDIA Control Panel"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "amdkmdap.sys",
            ComponentName = "AMD Display Driver",
            Category = DriverCategory.GPU,
            Severity = DpcSeverity.High,
            Problem = "AMD kernel-mode driver can cause DPC spikes, especially with Radeon Software features enabled",
            FixSteps = new[]
            {
                "Update to latest AMD driver from amd.com/drivers",
                "Disable Radeon Anti-Lag, Radeon Chill, and Radeon Boost if not needed",
                "Disable Enhanced Sync in Radeon Software",
                "Run DDU (Display Driver Uninstaller) for a clean driver install"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "atikmdag.sys",
            ComponentName = "AMD/ATI Display Driver (Legacy)",
            Category = DriverCategory.GPU,
            Severity = DpcSeverity.High,
            Problem = "Legacy AMD display driver with known latency issues",
            FixSteps = new[]
            {
                "Update to latest AMD driver from amd.com/drivers",
                "Run DDU and do a clean install",
                "Disable Crossfire if using multiple GPUs"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "igdkmd64.sys",
            ComponentName = "Intel Graphics Driver",
            Category = DriverCategory.GPU,
            Severity = DpcSeverity.Medium,
            Problem = "Intel integrated graphics driver can cause periodic DPC spikes",
            FixSteps = new[]
            {
                "Update Intel graphics driver from Intel Download Center",
                "If using a dedicated GPU, disable Intel iGPU in BIOS",
                "Disable Intel Display Power Saving Technology in Intel Graphics settings"
            }
        });

        // ── Network Drivers ──────────────────────────────────────────────

        Register(new DpcOffenderEntry
        {
            DriverFileName = "ndis.sys",
            ComponentName = "Windows Network Driver Interface",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Medium,
            Problem = "Network stack driver, latency often caused by network adapter offloading features",
            FixSteps = new[]
            {
                "Update network adapter drivers from manufacturer website",
                "Disable TCP/IP offloading in adapter advanced settings",
                "Disable Large Send Offload (LSO) and Checksum Offload",
                "Disable Interrupt Moderation in adapter settings"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "tcpip.sys",
            ComponentName = "Windows TCP/IP Stack",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Medium,
            Problem = "TCP/IP driver latency, often from VPN software or misconfigured offloading",
            FixSteps = new[]
            {
                "Disable TCP/IP Checksum Offloading in adapter settings",
                "Remove or disable VPN software during gaming",
                "Update network adapter drivers",
                "Run 'netsh int tcp set global autotuninglevel=normal' in admin command prompt"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "rtwlanu.sys",
            ComponentName = "Realtek Wireless LAN Driver",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.High,
            Problem = "Realtek WiFi drivers are notorious for high DPC latency",
            FixSteps = new[]
            {
                "Update Realtek WiFi driver from your motherboard manufacturer",
                "Switch to wired Ethernet for gaming (strongly recommended)",
                "Disable power saving in WiFi adapter advanced settings",
                "Disable Interrupt Moderation in adapter properties"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "rt640x64.sys",
            ComponentName = "Realtek Ethernet Driver",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Medium,
            Problem = "Realtek Ethernet driver can cause intermittent DPC spikes",
            FixSteps = new[]
            {
                "Update Realtek Ethernet driver from your motherboard manufacturer",
                "Disable Green Ethernet and Energy Efficient Ethernet in adapter settings",
                "Disable Interrupt Moderation",
                "Set Speed & Duplex to '1.0 Gbps Full Duplex' instead of Auto"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "e1d65x64.sys",
            ComponentName = "Intel Ethernet Driver",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Low,
            Problem = "Intel Ethernet can cause minor DPC latency with interrupt coalescing",
            FixSteps = new[]
            {
                "Update Intel Ethernet drivers from Intel Download Center",
                "Disable Interrupt Moderation in adapter advanced settings",
                "Disable Energy Efficient Ethernet"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "Netwtw10.sys",
            ComponentName = "Intel WiFi 6/6E Driver",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Medium,
            Problem = "Intel WiFi driver with known interrupt moderation issues",
            FixSteps = new[]
            {
                "Update Intel WiFi driver from Intel Download Center",
                "Disable Interrupt Moderation in adapter advanced settings",
                "Switch to wired Ethernet for competitive gaming",
                "Disable power saving in WiFi adapter settings"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "Netwtw08.sys",
            ComponentName = "Intel WiFi Driver (Older)",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Medium,
            Problem = "Older Intel WiFi driver with DPC latency issues",
            FixSteps = new[]
            {
                "Update to latest Intel WiFi driver from Intel Download Center",
                "Disable Interrupt Moderation in adapter advanced settings",
                "Switch to wired Ethernet for gaming"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "BfLwf.sys",
            ComponentName = "Killer Networking Suite Filter",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.High,
            Problem = "Killer Networking software filter driver adds significant DPC overhead",
            FixSteps = new[]
            {
                "Uninstall Killer Networking Suite completely",
                "Install only the bare Killer network driver (no suite)",
                "Or replace with standard Intel/Qualcomm driver from Device Manager"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "KillerEth.sys",
            ComponentName = "Killer Ethernet Driver",
            Category = DriverCategory.Network,
            Severity = DpcSeverity.Medium,
            Problem = "Killer Ethernet driver with QoS overhead causing DPC spikes",
            FixSteps = new[]
            {
                "Replace Killer driver with standard Intel driver",
                "Disable Killer Prioritization Engine in Killer Control Center",
                "Uninstall Killer Suite and use Windows built-in driver"
            }
        });

        // ── Audio Drivers ────────────────────────────────────────────────

        Register(new DpcOffenderEntry
        {
            DriverFileName = "HDAudBus.sys",
            ComponentName = "Microsoft HD Audio Bus Driver",
            Category = DriverCategory.Audio,
            Severity = DpcSeverity.Medium,
            Problem = "HD Audio bus driver, latency from audio subsystem contention",
            FixSteps = new[]
            {
                "Update Realtek/audio drivers from your motherboard manufacturer",
                "Increase audio buffer size if using a DAW or audio software",
                "Disable unused audio devices in Device Manager",
                "Try disabling audio enhancements in Sound settings"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "RTKVHD64.sys",
            ComponentName = "Realtek HD Audio Driver",
            Category = DriverCategory.Audio,
            Severity = DpcSeverity.High,
            Problem = "Realtek audio driver frequently causes DPC spikes with power management",
            FixSteps = new[]
            {
                "Update Realtek audio driver from your motherboard manufacturer (not Windows Update)",
                "Disable power saving on the audio device in Device Manager",
                "Disable all audio enhancements in Sound settings",
                "Consider disabling onboard audio and using a USB DAC"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "NahimicService.sys",
            ComponentName = "Nahimic Audio Service",
            Category = DriverCategory.Audio,
            Severity = DpcSeverity.High,
            Problem = "Nahimic audio processing software causes significant DPC overhead",
            FixSteps = new[]
            {
                "Uninstall Nahimic from Settings > Apps",
                "Disable Nahimic service in services.msc",
                "Remove Nahimic from startup in Task Manager",
                "Check for MSI/Lenovo pre-installed audio software and remove it"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "SS3DeviceService.sys",
            ComponentName = "Sonic Studio 3 / Sonic Suite",
            Category = DriverCategory.Audio,
            Severity = DpcSeverity.High,
            Problem = "ASUS Sonic Studio/Suite audio processing adds heavy DPC overhead",
            FixSteps = new[]
            {
                "Uninstall Sonic Studio / Sonic Suite from Settings > Apps",
                "Disable related ASUS audio services in services.msc",
                "Use standard Realtek driver without ASUS audio software"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "dtsapo4service.sys",
            ComponentName = "DTS Audio Processing Object",
            Category = DriverCategory.Audio,
            Severity = DpcSeverity.Medium,
            Problem = "DTS audio processing adds DPC overhead",
            FixSteps = new[]
            {
                "Disable DTS Audio in Sound settings > Spatial Sound",
                "Uninstall DTS Audio from Settings > Apps",
                "Disable DTS service in services.msc"
            }
        });

        // ── Storage Drivers ──────────────────────────────────────────────

        Register(new DpcOffenderEntry
        {
            DriverFileName = "storport.sys",
            ComponentName = "Windows Storage Port Driver",
            Category = DriverCategory.Storage,
            Severity = DpcSeverity.Medium,
            Problem = "Storage port driver latency, often from controller driver issues",
            FixSteps = new[]
            {
                "Update storage controller drivers (Intel RST, AMD StoreMI)",
                "Check SSD firmware updates from manufacturer",
                "Disable write caching if using multiple drives",
                "Switch from RAID to AHCI mode if not using RAID"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "iaStorAC.sys",
            ComponentName = "Intel Rapid Storage Technology",
            Category = DriverCategory.Storage,
            Severity = DpcSeverity.Medium,
            Problem = "Intel RST driver can cause DPC latency, especially older versions",
            FixSteps = new[]
            {
                "Update Intel RST driver from Intel Download Center",
                "Consider switching to standard Microsoft AHCI driver (storahci.sys)",
                "Disable Intel Optane Memory if not using it"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "iaStorV.sys",
            ComponentName = "Intel RST VMD Controller",
            Category = DriverCategory.Storage,
            Severity = DpcSeverity.Low,
            Problem = "Intel VMD storage controller can cause minor DPC overhead",
            FixSteps = new[]
            {
                "Update Intel RST/VMD driver from Intel Download Center",
                "Consider disabling VMD in BIOS if not required"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "stornvme.sys",
            ComponentName = "Windows NVMe Storage Driver",
            Category = DriverCategory.Storage,
            Severity = DpcSeverity.Low,
            Problem = "NVMe driver can cause brief DPC spikes during heavy I/O",
            FixSteps = new[]
            {
                "Update NVMe driver from SSD manufacturer (Samsung Magician, WD Dashboard, etc.)",
                "Check SSD firmware updates",
                "Ensure NVMe power state transitions are not aggressive in power plan"
            }
        });

        // ── USB Drivers ──────────────────────────────────────────────────

        Register(new DpcOffenderEntry
        {
            DriverFileName = "USBPORT.sys",
            ComponentName = "USB Port Driver",
            Category = DriverCategory.USB,
            Severity = DpcSeverity.Medium,
            Problem = "USB controller driver, latency from polling or power management",
            FixSteps = new[]
            {
                "Try different USB ports (USB 3.0 vs 2.0)",
                "Update USB controller drivers from motherboard manufacturer",
                "Disable USB Selective Suspend in power plan advanced settings",
                "Disable 'Allow the computer to turn off this device' for USB root hubs"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "USBXHCI.sys",
            ComponentName = "USB 3.0 eXtensible Host Controller",
            Category = DriverCategory.USB,
            Severity = DpcSeverity.Low,
            Problem = "USB 3.x controller can cause minor DPC overhead with many connected devices",
            FixSteps = new[]
            {
                "Disconnect unnecessary USB devices during gaming",
                "Disable USB Selective Suspend in power plan",
                "Update USB controller drivers from motherboard manufacturer"
            }
        });

        // ── Framework / System Drivers ───────────────────────────────────

        Register(new DpcOffenderEntry
        {
            DriverFileName = "Wdf01000.sys",
            ComponentName = "Windows Driver Framework",
            Category = DriverCategory.Framework,
            Severity = DpcSeverity.Low,
            Problem = "WDF framework driver, high latency usually caused by a driver using this framework",
            FixSteps = new[]
            {
                "Update Windows to latest version",
                "Update all device drivers to latest versions",
                "Check for conflicting third-party kernel drivers"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "dxgkrnl.sys",
            ComponentName = "DirectX Graphics Kernel",
            Category = DriverCategory.GPU,
            Severity = DpcSeverity.Medium,
            Problem = "DirectX graphics kernel, latency from GPU driver interaction",
            FixSteps = new[]
            {
                "Update GPU drivers (NVIDIA/AMD/Intel)",
                "Disable Hardware Accelerated GPU Scheduling",
                "Disable Multiplane Overlay (MPO) via registry"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "ntoskrnl.exe",
            ComponentName = "Windows NT Kernel",
            Category = DriverCategory.Framework,
            Severity = DpcSeverity.Low,
            Problem = "Kernel-level DPC overhead, often from timer resolution or interrupt routing",
            FixSteps = new[]
            {
                "Ensure Windows is fully updated",
                "Check for core isolation / VBS impact (GameShift can detect this)",
                "Verify BIOS settings: disable C-States if latency-sensitive"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "ACPI.sys",
            ComponentName = "ACPI Driver (Power Management)",
            Category = DriverCategory.Framework,
            Severity = DpcSeverity.Low,
            Problem = "ACPI power management driver, latency from C-state transitions",
            FixSteps = new[]
            {
                "Set Windows power plan to High Performance or Ultimate Performance",
                "Disable CPU C-States in BIOS for lowest latency",
                "Disable PCI Express Link State Power Management in power plan"
            }
        });

        // ── Bloatware / OEM Drivers ──────────────────────────────────────

        Register(new DpcOffenderEntry
        {
            DriverFileName = "AsusHwIO.sys",
            ComponentName = "ASUS Hardware I/O Driver",
            Category = DriverCategory.Other,
            Severity = DpcSeverity.Medium,
            Problem = "ASUS Armoury Crate / AI Suite hardware monitoring driver adds DPC overhead",
            FixSteps = new[]
            {
                "Uninstall ASUS Armoury Crate and AI Suite",
                "Use ASUS BIOS for fan control instead of software",
                "Remove via Armoury Crate Uninstall Tool from ASUS support site"
            }
        });

        Register(new DpcOffenderEntry
        {
            DriverFileName = "NTIOLib_X64.sys",
            ComponentName = "MSI/OEM Hardware Monitor Driver",
            Category = DriverCategory.Other,
            Severity = DpcSeverity.Medium,
            Problem = "Hardware monitoring driver used by MSI Dragon Center, Afterburner, etc.",
            FixSteps = new[]
            {
                "Close MSI Dragon Center / MSI Center",
                "Close hardware monitoring tools (HWiNFO, MSI Afterburner) during gaming",
                "Reduce sensor polling rate if using monitoring software"
            }
        });
    }

    private static void Register(DpcOffenderEntry entry)
    {
        _offenders[entry.DriverFileName] = entry;
    }

    /// <summary>
    /// Looks up a driver by filename. Returns null if not a known offender.
    /// </summary>
    public static DpcOffenderEntry? GetOffender(string driverFileName)
    {
        return _offenders.TryGetValue(driverFileName, out var entry) ? entry : null;
    }

    /// <summary>
    /// Returns a one-line fix suggestion for a driver. Backward-compatible with
    /// DpcLatencyMonitor.GetFixSuggestion().
    /// </summary>
    public static string? GetFixSuggestion(string driverFileName)
    {
        var entry = GetOffender(driverFileName);
        return entry != null ? entry.FixSteps[0] : null;
    }

    /// <summary>
    /// Returns all known offender entries.
    /// </summary>
    public static IReadOnlyCollection<DpcOffenderEntry> GetAll()
    {
        return _offenders.Values;
    }
}
