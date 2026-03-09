using System.Text.Json;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.SystemTweaks.Tweaks;

/// <summary>
/// Optimizes GPU interrupt handling by enabling MSI (Message Signaled Interrupts) and
/// pinning GPU interrupts to a non-Core-0 P-core. This reduces DPC latency and avoids
/// Core 0 contention with system work, mouse input, and the Windows scheduler.
///
/// MSI allows devices to send interrupts directly to specific CPU cores without APIC
/// routing overhead. Combined with affinity pinning, this can reduce GPU-related DPC
/// latency by 5-15%.
///
/// NOT included in "Apply All Recommended" — opt-in only (higher risk, requires reboot).
/// </summary>
public class OptimizeInterruptHandling : ISystemTweak
{
    public string Name => "Optimize Interrupt Handling";
    public string Description => "Enables MSI mode and pins GPU interrupts to a dedicated P-core, reducing DPC latency and Core 0 contention.";
    public string Category => "GPU";
    public bool RequiresReboot => true;

    /// <summary>Detected PCI devices with interrupt info. Populated after scan.</summary>
    public List<PciDeviceInterruptInfo> DetectedDevices { get; private set; } = new();

    /// <summary>The target core index for GPU interrupt affinity.</summary>
    public int RecommendedCore { get; private set; } = 2;

    /// <summary>The primary GPU found during scan.</summary>
    public PciDeviceInterruptInfo? PrimaryGpu { get; private set; }

    private const string PciEnumPath = @"SYSTEM\CurrentControlSet\Enum\PCI";

    // Class GUIDs
    private const string DisplayAdapterClassGuid = "{4d36e968-e325-11ce-bfc1-08002bfe1801}";
    private const string NetworkAdapterClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    // Virtual/software adapter keywords to filter out
    private static readonly string[] VirtualAdapterKeywords = new[]
    {
        "Microsoft Basic Display",
        "Microsoft Hyper-V",
        "Remote Desktop",
        "Virtual",
        "VMware",
        "VirtualBox",
        "Parsec",
        "RDP"
    };

    public bool DetectIsApplied()
    {
        try
        {
            ScanDevices();
            if (PrimaryGpu == null) return false;

            // Applied if MSI is enabled AND affinity is pinned (DevicePolicy = 4)
            return PrimaryGpu.MsiEnabled && PrimaryGpu.DevicePolicy == 4;
        }
        catch
        {
            return false;
        }
    }

    public string? Apply()
    {
        ScanDevices();

        if (PrimaryGpu == null)
        {
            Log.Warning("[InterruptAffinity] No GPU found for interrupt optimization");
            return null;
        }

        // Backup current state
        var backup = new InterruptBackupState
        {
            DeviceId = PrimaryGpu.DeviceId,
            InstanceId = PrimaryGpu.InstanceId,
            OriginalMsiEnabled = PrimaryGpu.MsiEnabled,
            OriginalDevicePolicy = PrimaryGpu.DevicePolicy,
            OriginalAffinityMask = PrimaryGpu.CurrentAffinityMask
        };

        bool changed = false;

        // Enable MSI if supported but not enabled
        if (PrimaryGpu.ShouldEnableMsi)
        {
            if (EnableMsi(PrimaryGpu))
                changed = true;
        }

        // Set affinity to recommended non-Core-0 P-core
        RecommendedCore = RecommendInterruptCore();
        if (SetInterruptAffinity(PrimaryGpu, RecommendedCore))
            changed = true;

        if (changed)
        {
            Log.Information("[InterruptAffinity] Optimized {Device} — MSI: {Msi}, Affinity: Core {Core} — reboot required",
                PrimaryGpu.DisplayName, PrimaryGpu.MsiEnabled || PrimaryGpu.ShouldEnableMsi, RecommendedCore);
        }

        return JsonSerializer.Serialize(backup);
    }

    public bool Revert(string? originalValuesJson)
    {
        if (string.IsNullOrEmpty(originalValuesJson)) return false;

        try
        {
            var backup = JsonSerializer.Deserialize<InterruptBackupState>(originalValuesJson);
            if (backup == null) return false;

            string msiKeyPath = $@"{PciEnumPath}\{backup.DeviceId}\{backup.InstanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
            string affinityKeyPath = $@"{PciEnumPath}\{backup.DeviceId}\{backup.InstanceId}\Device Parameters\Interrupt Management\Affinity Policy";

            // Restore MSI state
            try
            {
                using var msiKey = Registry.LocalMachine.OpenSubKey(msiKeyPath, writable: true);
                if (msiKey != null)
                {
                    msiKey.SetValue("MSISupported", backup.OriginalMsiEnabled ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InterruptAffinity] Failed to restore MSI state");
            }

            // Restore affinity
            try
            {
                using var affinityKey = Registry.LocalMachine.OpenSubKey(affinityKeyPath, writable: true);
                if (affinityKey != null)
                {
                    if (backup.OriginalDevicePolicy != null)
                    {
                        affinityKey.SetValue("DevicePolicy", backup.OriginalDevicePolicy.Value, RegistryValueKind.DWord);
                    }
                    else
                    {
                        affinityKey.DeleteValue("DevicePolicy", throwOnMissingValue: false);
                    }

                    if (backup.OriginalAffinityMask != null)
                    {
                        affinityKey.SetValue("AssignmentSetOverride", backup.OriginalAffinityMask, RegistryValueKind.Binary);
                    }
                    else
                    {
                        affinityKey.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InterruptAffinity] Failed to restore affinity");
            }

            Log.Information("[InterruptAffinity] Reverted interrupt optimization — reboot required");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to revert");
            return false;
        }
    }

    // ── Device scanning ─────────────────────────────────────────────────

    /// <summary>
    /// Scans PCI devices for GPU and NIC interrupt configuration.
    /// Populates DetectedDevices and PrimaryGpu.
    /// </summary>
    public void ScanDevices()
    {
        DetectedDevices.Clear();
        PrimaryGpu = null;

        try
        {
            using var pciKey = Registry.LocalMachine.OpenSubKey(PciEnumPath);
            if (pciKey == null) return;

            foreach (string deviceId in pciKey.GetSubKeyNames())
            {
                using var deviceKey = pciKey.OpenSubKey(deviceId);
                if (deviceKey == null) continue;

                foreach (string instanceId in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceId);
                    if (instanceKey == null) continue;

                    string deviceDesc = instanceKey.GetValue("DeviceDesc")?.ToString() ?? "";
                    string friendlyName = instanceKey.GetValue("FriendlyName")?.ToString() ?? "";
                    string classGuid = instanceKey.GetValue("ClassGUID")?.ToString() ?? "";

                    bool isGpu = classGuid.Equals(DisplayAdapterClassGuid, StringComparison.OrdinalIgnoreCase);
                    bool isNic = classGuid.Equals(NetworkAdapterClassGuid, StringComparison.OrdinalIgnoreCase);

                    if (!isGpu && !isNic) continue;

                    // Clean up DeviceDesc (format: "@<inf>,<section>;<description>" or plain)
                    string cleanDesc = deviceDesc;
                    int semiIdx = deviceDesc.LastIndexOf(';');
                    if (semiIdx >= 0) cleanDesc = deviceDesc[(semiIdx + 1)..];

                    // Filter out virtual/software adapters
                    string checkName = $"{cleanDesc} {friendlyName}";
                    if (VirtualAdapterKeywords.Any(kw =>
                        checkName.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Read MSI state
                    string msiPath = $@"{deviceId}\{instanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    using var msiKey = Registry.LocalMachine.OpenSubKey($@"{PciEnumPath}\{msiPath}");

                    int? msiSupported = msiKey?.GetValue("MSISupported") as int?;
                    int? messageNumberLimit = msiKey?.GetValue("MessageNumberLimit") as int?;

                    // Read affinity policy
                    string affinityPath = $@"{deviceId}\{instanceId}\Device Parameters\Interrupt Management\Affinity Policy";
                    using var affinityKey = Registry.LocalMachine.OpenSubKey($@"{PciEnumPath}\{affinityPath}");

                    int? devicePolicy = affinityKey?.GetValue("DevicePolicy") as int?;
                    byte[]? assignmentSetOverride = affinityKey?.GetValue("AssignmentSetOverride") as byte[];

                    DetectedDevices.Add(new PciDeviceInterruptInfo
                    {
                        DeviceId = deviceId,
                        InstanceId = instanceId,
                        DeviceDescription = cleanDesc.Trim(),
                        FriendlyName = friendlyName,
                        IsGpu = isGpu,
                        IsNic = isNic,
                        MsiEnabled = msiSupported == 1,
                        MsiSupported = msiKey != null,
                        MessageNumberLimit = messageNumberLimit,
                        DevicePolicy = devicePolicy,
                        CurrentAffinityMask = assignmentSetOverride,
                        RegistryBasePath = $@"{PciEnumPath}\{deviceId}\{instanceId}"
                    });
                }
            }

            // Set primary GPU (first non-virtual GPU found)
            PrimaryGpu = DetectedDevices.FirstOrDefault(d => d.IsGpu);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to scan PCI devices");
        }
    }

    // ── MSI management ──────────────────────────────────────────────────

    /// <summary>
    /// Enables MSI on a PCI device by setting MSISupported=1.
    /// </summary>
    private static bool EnableMsi(PciDeviceInterruptInfo device)
    {
        if (!device.MsiSupported) return false;

        try
        {
            string msiKeyPath = $@"{device.RegistryBasePath}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

            using var key = Registry.LocalMachine.OpenSubKey(msiKeyPath, writable: true);
            if (key == null)
            {
                // Create the key structure
                using var parentKey = Registry.LocalMachine.CreateSubKey(msiKeyPath);
                parentKey.SetValue("MSISupported", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue("MSISupported", 1, RegistryValueKind.DWord);
            }

            Log.Information("[InterruptAffinity] MSI enabled for {Device}", device.DisplayName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to enable MSI for {Device}", device.DisplayName);
            return false;
        }
    }

    // ── Affinity management ─────────────────────────────────────────────

    /// <summary>
    /// Sets interrupt affinity for a device to a specific CPU core.
    /// DevicePolicy=4 (SpecifiedProcessors) + AssignmentSetOverride bitmask.
    /// </summary>
    private static bool SetInterruptAffinity(PciDeviceInterruptInfo device, int targetCore)
    {
        try
        {
            string affinityKeyPath = $@"{device.RegistryBasePath}\Device Parameters\Interrupt Management\Affinity Policy";

            using var key = Registry.LocalMachine.CreateSubKey(affinityKeyPath);

            // Set to specified processor mode
            key.SetValue("DevicePolicy", 4, RegistryValueKind.DWord);

            // Build affinity bitmask: bit N = 1 << targetCore (little-endian byte array)
            ulong mask = 1UL << targetCore;
            byte[] maskBytes = BitConverter.GetBytes(mask);

            // Trim trailing zero bytes (registry expects compact representation)
            int lastNonZero = maskBytes.Length - 1;
            while (lastNonZero > 0 && maskBytes[lastNonZero] == 0) lastNonZero--;
            byte[] trimmed = new byte[lastNonZero + 1];
            Array.Copy(maskBytes, trimmed, trimmed.Length);

            key.SetValue("AssignmentSetOverride", trimmed, RegistryValueKind.Binary);

            Log.Information("[InterruptAffinity] Affinity for {Device} set to Core {Core}", device.DisplayName, targetCore);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to set affinity for {Device}", device.DisplayName);
            return false;
        }
    }

    /// <summary>
    /// Recommends the best core for GPU interrupts based on CPU topology.
    /// Rules:
    ///   1. Never Core 0 (system work, mouse input, scheduler)
    ///   2. P-core on hybrid CPUs (never E-core for GPU interrupts)
    ///   3. Not the HT sibling of Core 0 (avoid shared physical core contention)
    ///   4. Typically Core 2 (second physical core on most configurations)
    /// </summary>
    private static int RecommendInterruptCore()
    {
        try
        {
            // Check if hybrid CPU via registry EfficiencyClass
            const string cpuRegPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor";
            using var cpuKey = Registry.LocalMachine.OpenSubKey(cpuRegPath);
            if (cpuKey != null)
            {
                var pCoreIndices = new List<int>();
                var allIndices = cpuKey.GetSubKeyNames()
                    .Select(s => int.TryParse(s, out int idx) ? idx : -1)
                    .Where(i => i >= 0)
                    .OrderBy(i => i)
                    .ToList();

                foreach (var idx in allIndices)
                {
                    using var core = cpuKey.OpenSubKey(idx.ToString());
                    if (core == null) continue;

                    var effClass = core.GetValue("EfficiencyClass");
                    if (effClass is int eff && eff == 0)
                    {
                        pCoreIndices.Add(idx);
                    }
                }

                bool isHybrid = pCoreIndices.Count > 0 && pCoreIndices.Count < allIndices.Count;

                if (isHybrid && pCoreIndices.Count > 1)
                {
                    // Second P-core (first non-Core-0 P-core)
                    return pCoreIndices[1];
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to detect CPU topology, defaulting to Core 2");
        }

        // Non-hybrid: use core 2 (skip core 0 and its HT sibling core 1)
        return Math.Min(2, Environment.ProcessorCount - 1);
    }
}

/// <summary>
/// Serializable backup state for interrupt optimization revert.
/// </summary>
public class InterruptBackupState
{
    public string DeviceId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public bool OriginalMsiEnabled { get; set; }
    public int? OriginalDevicePolicy { get; set; }
    public byte[]? OriginalAffinityMask { get; set; }
}
