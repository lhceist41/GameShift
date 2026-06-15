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
/// NOT included in "Apply All Recommended" - opt-in only (higher risk, requires reboot).
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

    // ── Status summary for DPC Doctor integration ─────────────────────

    /// <summary>
    /// Returns the current GPU interrupt core index, or null if not pinned.
    /// Call <see cref="ScanDevices"/> first to populate <see cref="PrimaryGpu"/>.
    /// </summary>
    public int? CurrentGpuInterruptCore
    {
        get
        {
            if (PrimaryGpu?.CurrentAffinityMask == null) return null;
            return AffinityMaskToCore(PrimaryGpu.CurrentAffinityMask);
        }
    }

    /// <summary>
    /// Returns the current USB controller interrupt core index, or null if not pinned.
    /// </summary>
    public int? CurrentUsbInterruptCore
    {
        get
        {
            if (PrimaryUsb?.CurrentAffinityMask == null) return null;
            return AffinityMaskToCore(PrimaryUsb.CurrentAffinityMask);
        }
    }

    /// <summary>
    /// Returns the GPU MSI mode status: true if enabled, false if disabled, null if no GPU.
    /// </summary>
    public bool? GpuMsiEnabled => PrimaryGpu?.MsiEnabled;

    private static int? AffinityMaskToCore(byte[] mask)
    {
        // Find the lowest set bit in the KAFFINITY bitmask
        for (int byteIdx = 0; byteIdx < mask.Length; byteIdx++)
        {
            if (mask[byteIdx] == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((mask[byteIdx] & (1 << bit)) != 0)
                    return byteIdx * 8 + bit;
            }
        }
        return null;
    }

    /// <summary>
    /// True for discrete GPU vendors (NVIDIA VEN_10DE, AMD VEN_1002). Used to prefer the gaming
    /// dGPU over an integrated adapter (e.g. Intel VEN_8086) when choosing which GPU to optimize.
    /// </summary>
    private static bool IsDiscreteGpu(string deviceId) =>
        deviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
        deviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);

    private const string PciEnumPath = @"SYSTEM\CurrentControlSet\Enum\PCI";

    // Class GUIDs
    private const string DisplayAdapterClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NetworkAdapterClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string UsbControllerClassGuid = "{36fc9e60-c465-11cf-8056-444553540000}";

    /// <summary>The primary USB host controller found during scan.</summary>
    public PciDeviceInterruptInfo? PrimaryUsb { get; private set; }

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

            // Applied if GPU affinity is pinned (DevicePolicy = 4)
            bool gpuApplied = PrimaryGpu.MsiEnabled && PrimaryGpu.DevicePolicy == 4;

            // USB affinity is a bonus - GPU is the primary indicator
            return gpuApplied;
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
            OriginalAffinityMask = PrimaryGpu.CurrentAffinityMask,
            GpuAffinityKeyExisted = PrimaryGpu.AffinityKeyExisted
        };

        bool changed = false;

        // Capture MSI value/key existence so revert can restore-or-delete faithfully.
        backup.GpuMsiValueExisted = PrimaryGpu.MsiValueExisted;
        backup.GpuMsiKeyExisted = PrimaryGpu.MsiSupported;

        // Enable MSI if supported but not enabled
        if (PrimaryGpu.ShouldEnableMsi)
        {
            if (EnableMsi(PrimaryGpu))
            {
                changed = true;
                backup.GpuMsiChanged = true;
            }
        }

        // Set affinity to a recommended non-Core-0 core. Skipped (RecommendedCore < 0) on CPUs
        // where a valid single-group core cannot be chosen (too few cores, or >64 logical
        // processors / multiple processor groups, which this flat KAFFINITY mask cannot express).
        RecommendedCore = RecommendInterruptCore();
        if (RecommendedCore >= 0 && SetInterruptAffinity(PrimaryGpu, RecommendedCore))
            changed = true;

        // ── USB host controller affinity (same core as GPU for input latency) ──
        // We deliberately do NOT force MSI on the USB host controller. A controller that fails to
        // initialize under forced MSI leaves the user with no keyboard/mouse and no way to reach
        // the in-app revert. Affinity pinning does not change interrupt delivery mode and is safe.
        if (PrimaryUsb != null && RecommendedCore >= 0)
        {
            if (SetInterruptAffinity(PrimaryUsb, RecommendedCore))
            {
                changed = true;
                Log.Information(
                    "[InterruptAffinity] USB controller {Device} affinity set to Core {Core}",
                    PrimaryUsb.DisplayName, RecommendedCore);
            }

            backup.UsbDeviceId = PrimaryUsb.DeviceId;
            backup.UsbInstanceId = PrimaryUsb.InstanceId;
            backup.UsbOriginalDevicePolicy = PrimaryUsb.DevicePolicy;
            backup.UsbOriginalAffinityMask = PrimaryUsb.CurrentAffinityMask;
            backup.UsbOriginalMsiEnabled = PrimaryUsb.MsiEnabled;
            backup.UsbAffinityKeyExisted = PrimaryUsb.AffinityKeyExisted;
        }

        if (changed)
        {
            Log.Information(
                "[InterruptAffinity] Optimized GPU ({Gpu}) + USB ({Usb}) - Core {Core} - reboot required",
                PrimaryGpu.DisplayName,
                PrimaryUsb?.DisplayName ?? "none",
                RecommendedCore);
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

            // Restore MSI state - only if Apply actually changed it, and restore-or-delete based on
            // whether the MSISupported value existed before (never write a guessed default).
            try
            {
                if (backup.GpuMsiChanged)
                {
                    using (var msiKey = Registry.LocalMachine.OpenSubKey(msiKeyPath, writable: true))
                    {
                        if (msiKey != null)
                        {
                            if (backup.GpuMsiValueExisted)
                                msiKey.SetValue("MSISupported", backup.OriginalMsiEnabled ? 1 : 0, RegistryValueKind.DWord);
                            else
                                msiKey.DeleteValue("MSISupported", throwOnMissingValue: false);
                        }
                    }

                    // If EnableMsi created the MSI key itself (absent before Apply), remove the orphan.
                    if (!backup.GpuMsiKeyExisted)
                        DeleteKeyIfEmpty(msiKeyPath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InterruptAffinity] Failed to restore MSI state");
            }

            // Restore affinity
            try
            {
                using (var affinityKey = Registry.LocalMachine.OpenSubKey(affinityKeyPath, writable: true))
                {
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

                // If GameShift created the Affinity Policy key (absent before Apply), delete the
                // now-empty key it left behind rather than leaving an orphan.
                if (!backup.GpuAffinityKeyExisted)
                    DeleteKeyIfEmpty(affinityKeyPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[InterruptAffinity] Failed to restore affinity");
            }

            // Restore USB controller state
            if (!string.IsNullOrEmpty(backup.UsbDeviceId) && !string.IsNullOrEmpty(backup.UsbInstanceId))
            {
                string usbAffinityPath = $@"{PciEnumPath}\{backup.UsbDeviceId}\{backup.UsbInstanceId}\Device Parameters\Interrupt Management\Affinity Policy";
                try
                {
                    using (var usbKey = Registry.LocalMachine.OpenSubKey(usbAffinityPath, writable: true))
                    {
                        if (usbKey != null)
                        {
                            if (backup.UsbOriginalDevicePolicy != null)
                                usbKey.SetValue("DevicePolicy", backup.UsbOriginalDevicePolicy.Value, RegistryValueKind.DWord);
                            else
                                usbKey.DeleteValue("DevicePolicy", throwOnMissingValue: false);

                            if (backup.UsbOriginalAffinityMask != null)
                                usbKey.SetValue("AssignmentSetOverride", backup.UsbOriginalAffinityMask, RegistryValueKind.Binary);
                            else
                                usbKey.DeleteValue("AssignmentSetOverride", throwOnMissingValue: false);
                        }
                    }

                    if (!backup.UsbAffinityKeyExisted)
                        DeleteKeyIfEmpty(usbAffinityPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[InterruptAffinity] Failed to restore USB affinity");
                }

                // Restore USB MSI - only if Apply changed it; restore-or-delete by prior existence.
                if (backup.UsbMsiChanged)
                {
                    string usbMsiPath = $@"{PciEnumPath}\{backup.UsbDeviceId}\{backup.UsbInstanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    try
                    {
                        using (var usbMsiKey = Registry.LocalMachine.OpenSubKey(usbMsiPath, writable: true))
                        {
                            if (usbMsiKey != null)
                            {
                                if (backup.UsbMsiValueExisted)
                                    usbMsiKey.SetValue("MSISupported", (backup.UsbOriginalMsiEnabled ?? false) ? 1 : 0, RegistryValueKind.DWord);
                                else
                                    usbMsiKey.DeleteValue("MSISupported", throwOnMissingValue: false);
                            }
                        }

                        if (!backup.UsbMsiKeyExisted)
                            DeleteKeyIfEmpty(usbMsiPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[InterruptAffinity] Failed to restore USB MSI state");
                    }
                }
            }

            Log.Information("[InterruptAffinity] Reverted interrupt optimization - reboot required");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to revert");
            return false;
        }
    }

    /// <summary>
    /// Deletes a registry key only if it exists and is empty (no values, no subkeys). Used to
    /// remove an "Affinity Policy" key that GameShift created during Apply so revert leaves no
    /// orphan. The read handle is disposed before deletion (Windows blocks deleting an open key).
    /// </summary>
    private static void DeleteKeyIfEmpty(string keyPath)
    {
        try
        {
            bool empty;
            using (var k = Registry.LocalMachine.OpenSubKey(keyPath))
                empty = k != null && k.ValueCount == 0 && k.SubKeyCount == 0;
            if (!empty) return;

            int slash = keyPath.LastIndexOf('\\');
            if (slash <= 0) return;
            string parentPath = keyPath[..slash];
            string leaf = keyPath[(slash + 1)..];

            using var parent = Registry.LocalMachine.OpenSubKey(parentPath, writable: true);
            parent?.DeleteSubKey(leaf, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to delete created key {Path}", keyPath);
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
        PrimaryUsb = null;

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
                    bool isUsb = classGuid.Equals(UsbControllerClassGuid, StringComparison.OrdinalIgnoreCase);

                    if (!isGpu && !isNic && !isUsb) continue;

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
                        IsUsb = isUsb,
                        MsiEnabled = msiSupported == 1,
                        MsiSupported = msiKey != null,
                        MsiValueExisted = msiKey?.GetValue("MSISupported") != null,
                        MessageNumberLimit = messageNumberLimit,
                        DevicePolicy = devicePolicy,
                        CurrentAffinityMask = assignmentSetOverride,
                        AffinityKeyExisted = affinityKey != null,
                        RegistryBasePath = $@"{PciEnumPath}\{deviceId}\{instanceId}"
                    });
                }
            }

            // Prefer a discrete GPU (NVIDIA/AMD) over an integrated one. On a hybrid laptop or a
            // desktop with an active iGPU, "first display adapter found" can be the integrated
            // adapter - the actual display path and the worst device to risk an interrupt change on.
            PrimaryGpu = DetectedDevices.FirstOrDefault(d => d.IsGpu && IsDiscreteGpu(d.DeviceId))
                         ?? DetectedDevices.FirstOrDefault(d => d.IsGpu);
            PrimaryUsb = DetectedDevices.FirstOrDefault(d => d.IsUsb);
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
                // The MessageSignaledInterruptProperties node is absent: the device never advertised
                // MSI capability. Do NOT fabricate it. Forcing MSI on a device whose driver/firmware
                // does not support it is a known cause of post-reboot device failure (GPU Code 10 /
                // black screen, or a dead USB host controller). We only ever flip an existing node.
                Log.Information(
                    "[InterruptAffinity] Skipping MSI for {Device}: no MSI capability node present",
                    device.DisplayName);
                return false;
            }

            key.SetValue("MSISupported", 1, RegistryValueKind.DWord);
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
        // Guard the bit shift and the single-group mask: targetCore must be a real core in
        // [1, ProcessorCount) and below 64 (1UL << 64 is undefined and wraps to bit 0, silently
        // pinning to Core 0 - the exact core we are trying to avoid). Out of range -> write nothing.
        if (targetCore < 1 || targetCore >= 64 || targetCore >= Environment.ProcessorCount)
        {
            Log.Warning(
                "[InterruptAffinity] Skipping affinity for {Device}: target core {Core} invalid (logical processors={Lp})",
                device.DisplayName, targetCore, Environment.ProcessorCount);
            return false;
        }

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
    /// Recommends the best core for GPU/USB interrupts based on CPU topology, or -1 when interrupt
    /// pinning should be skipped entirely.
    /// Rules:
    ///   1. Skip (-1) on CPUs with fewer than 4 logical processors (too few to dedicate one) or
    ///      more than 64 (multiple processor groups, which this flat single-group mask cannot
    ///      express - a wrong-group/wrapped mask would silently pin to Core 0).
    ///   2. Never Core 0 (system work, mouse input, scheduler).
    ///   3. Last P-core (highest index) on hybrid CPUs, never an E-core.
    ///   4. Fallback: last core on non-hybrid CPUs.
    /// The returned index is always validated to be in [1, ProcessorCount) and below 64.
    /// </summary>
    private static int RecommendInterruptCore()
    {
        int lp = Environment.ProcessorCount;
        var pCoreIndices = new List<int>();
        int totalDetected = 0;

        try
        {
            const string cpuRegPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor";
            using var cpuKey = Registry.LocalMachine.OpenSubKey(cpuRegPath);
            if (cpuKey != null)
            {
                var allIndices = cpuKey.GetSubKeyNames()
                    .Select(s => int.TryParse(s, out int idx) ? idx : -1)
                    .Where(i => i >= 0)
                    .OrderBy(i => i)
                    .ToList();
                totalDetected = allIndices.Count;

                foreach (var idx in allIndices)
                {
                    using var core = cpuKey.OpenSubKey(idx.ToString());
                    if (core == null) continue;

                    var effClass = core.GetValue("EfficiencyClass");
                    if (effClass is int eff && eff == 0)
                        pCoreIndices.Add(idx);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InterruptAffinity] Failed to detect CPU topology, defaulting to last core");
        }

        return ChooseInterruptCore(lp, pCoreIndices, totalDetected);
    }

    /// <summary>
    /// Pure core-selection policy, separated from registry/topology reads so it is unit-testable.
    /// Returns the target interrupt core, or -1 when pinning should be skipped:
    ///   - fewer than 4 logical processors (too few to dedicate one), or
    ///   - more than 64 (multiple processor groups this flat KAFFINITY mask cannot express).
    /// On hybrid CPUs it prefers the last P-core; otherwise the last core. The result is always a
    /// valid, non-zero core index below 64 and below <paramref name="logicalProcessors"/>.
    /// </summary>
    internal static int ChooseInterruptCore(int logicalProcessors, IReadOnlyList<int> pCoreIndices, int totalDetectedCores)
    {
        if (logicalProcessors < 4 || logicalProcessors > 64) return -1;

        int candidate = logicalProcessors - 1; // default: last core (avoids Core 0 and its HT sibling)

        bool isHybrid = pCoreIndices.Count > 0 && pCoreIndices.Count < totalDetectedCores;
        if (isHybrid && pCoreIndices.Count > 1)
        {
            int lastPCore = pCoreIndices[^1];
            if (lastPCore >= 1) candidate = lastPCore;
        }

        if (candidate < 1 || candidate >= 64 || candidate >= logicalProcessors) return -1;
        return candidate;
    }
}

/// <summary>
/// Serializable backup state for interrupt optimization revert.
/// </summary>
public class InterruptBackupState
{
    // GPU
    public string DeviceId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public bool OriginalMsiEnabled { get; set; }
    public int? OriginalDevicePolicy { get; set; }
    public byte[]? OriginalAffinityMask { get; set; }
    public bool GpuAffinityKeyExisted { get; set; }

    // GPU MSI revert bookkeeping: only undo MSI if Apply actually changed it, and restore-or-delete
    // based on whether the value/key existed before (never write a guessed default).
    public bool GpuMsiChanged { get; set; }
    public bool GpuMsiValueExisted { get; set; }
    public bool GpuMsiKeyExisted { get; set; }

    // USB host controller
    public string? UsbDeviceId { get; set; }
    public string? UsbInstanceId { get; set; }
    public int? UsbOriginalDevicePolicy { get; set; }
    public byte[]? UsbOriginalAffinityMask { get; set; }
    public bool? UsbOriginalMsiEnabled { get; set; }
    public bool UsbAffinityKeyExisted { get; set; }
    public bool UsbMsiChanged { get; set; }
    public bool UsbMsiValueExisted { get; set; }
    public bool UsbMsiKeyExisted { get; set; }
}
