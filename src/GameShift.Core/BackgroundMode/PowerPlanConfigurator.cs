using GameShift.Core.Detection;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.BackgroundMode;

/// <summary>
/// CPU profile classification for vendor-aware power plan configuration.
/// Determines which heterogeneous scheduling and core parking values to use.
/// </summary>
public enum CpuProfile
{
    /// <summary>Intel 12th gen+ with P-cores and E-cores.</summary>
    IntelHybrid,

    /// <summary>Intel pre-12th gen (no hybrid architecture).</summary>
    IntelNonHybrid,

    /// <summary>All single-CCD AMD Ryzen (including single-CCD X3D like 7800X3D, 9800X3D).</summary>
    AmdSingleCcd,

    /// <summary>Dual-CCD AMD X3D (7950X3D, 9950X3D) — needs special core parking.</summary>
    AmdDualCcdX3d,

    /// <summary>Dual-CCD AMD non-X3D (7950X, 9950X).</summary>
    AmdDualCcdNonX3d,

    /// <summary>Unknown CPU — apply only universal settings.</summary>
    Unknown
}

/// <summary>
/// Represents a single powercfg override to apply to the GameShift Performance plan.
/// </summary>
/// <param name="SubGroupGuid">Power sub-group GUID.</param>
/// <param name="SettingGuid">Setting GUID within the sub-group.</param>
/// <param name="Value">Value to set.</param>
/// <param name="RevertValue">If session-scoped, value to revert to when session ends.</param>
/// <param name="Description">Human-readable description for logging.</param>
public record PowerOverride(
    string SubGroupGuid,
    string SettingGuid,
    int Value,
    int? RevertValue = null,
    string? Description = null);

/// <summary>
/// Builds the full list of powercfg overrides for the GameShift Performance plan
/// based on hardware detection. Separates permanent plan settings from session-toggled
/// settings (like processor idle disable).
/// </summary>
public class PowerPlanConfigurator
{
    // Sub-group GUIDs
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string SubIdleResiliency = "2e601130-5351-4d9d-8e04-252966bad054";
    private const string SubInterruptSteering = "48672f38-7a9a-4bb2-8bf8-3d85be19de4e";
    private const string SubGlobal = "fea3413e-7e05-4911-9a71-700331f1c294";
    private const string SubIntelGraphics = "44f3beca-a7c0-460e-9df2-bb8b99e0cba6";

    /// <summary>
    /// Returns all powercfg overrides to apply when creating the GameShift Performance plan.
    /// These are permanent plan settings (not session-toggled).
    /// </summary>
    public List<PowerOverride> GetPlanOverrides(CpuProfile cpuProfile, bool hasIntelGpu)
    {
        var overrides = new List<PowerOverride>();

        // Universal processor settings (safe on all hardware)
        overrides.AddRange(GetUniversalProcessorOverrides());

        // Universal storage/USB/wireless/idle resiliency/global settings
        overrides.AddRange(GetUniversalPeripheralOverrides());

        // Vendor-specific heterogeneous scheduling settings
        overrides.AddRange(GetVendorSchedulingOverrides(cpuProfile));

        // Optional Intel Graphics
        if (hasIntelGpu)
            overrides.AddRange(GetIntelGraphicsOverrides());

        return overrides;
    }

    /// <summary>
    /// Returns session-toggled overrides to apply during gaming (and revert after).
    /// Processor idle disable + time check interval adjustment.
    /// </summary>
    public static List<PowerOverride> GetGamingSessionOverrides()
    {
        return new List<PowerOverride>
        {
            // Processor idle disable — forces all cores to C0 state
            new(SubProcessor, "5d76a2ca-e8c0-402f-a133-2158492d58ad", 1, RevertValue: 0,
                Description: "Processor idle disable (C0 forced)"),

            // Time check interval = 5000ms during gaming (CPU locked at max, no need to check often)
            new(SubProcessor, "4d2b0152-7d5c-498b-88e2-34345392a2c5", 5000, RevertValue: 15,
                Description: "Performance time check interval"),
        };
    }

    /// <summary>
    /// Returns AMD dual-CCD X3D specific core parking values.
    /// AMD's PPM driver uses CPMINCORES=50 and ConcurrencyThreshold=67 for X3D GameMode.
    /// </summary>
    public static (int CpMinCores, int ConcurrencyThreshold) GetParkingValuesForProfile(CpuProfile profile)
    {
        return profile switch
        {
            CpuProfile.AmdDualCcdX3d => (50, 67),
            _ => (100, 0)
        };
    }

    // ── Universal Processor Overrides ──────────────────────────────────

    private static List<PowerOverride> GetUniversalProcessorOverrides()
    {
        return new List<PowerOverride>
        {
            // === FREQUENCY SCALING ===

            // Energy Performance Preference (EPP) = 0 (maximum performance)
            new(SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0,
                Description: "EPP default/E-cores"),
            new(SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6864", 0,
                Description: "EPP P-cores (Class 1)"),

            // Processor autonomous mode = Enabled
            new(SubProcessor, "8baa4a8a-14c6-4451-8e8b-14bdbd197537", 1,
                Description: "Processor autonomous mode"),

            // Autonomous activity window = 1000 microseconds
            new(SubProcessor, "cfeda3d0-7697-4566-a922-a9086cd49dfa", 1000,
                Description: "Autonomous activity window"),

            // Performance increase policy = Rocket (2)
            new(SubProcessor, "465e1f50-b610-473a-ab58-00d1077dc418", 2,
                Description: "Perf increase policy default/E-cores"),
            new(SubProcessor, "465e1f50-b610-473a-ab58-00d1077dc419", 2,
                Description: "Perf increase policy P-cores"),

            // Performance decrease policy = Ideal (0)
            new(SubProcessor, "40fbefc7-2e9d-4d25-a185-0cfd8574bac6", 0,
                Description: "Perf decrease policy default/E-cores"),
            new(SubProcessor, "40fbefc7-2e9d-4d25-a185-0cfd8574bac7", 0,
                Description: "Perf decrease policy P-cores"),

            // Processor performance boost policy = 100%
            new(SubProcessor, "45bcc044-d885-43e2-8605-ee0ec6e96b59", 100,
                Description: "Boost policy"),

            // Allow Throttle States = Off (0)
            new(SubProcessor, "3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb", 0,
                Description: "Allow throttle states"),

            // Processor duty cycling = Disabled (0)
            new(SubProcessor, "4e4450b3-6179-4e91-b8f1-5bb9938f81a1", 0,
                Description: "Processor duty cycling"),

            // === EXISTING 3 (already in PowerPlanManager — kept here for completeness but
            //     PowerPlanManager will skip duplicates via the existing ApplyPowerOverrides) ===
            // Boost Mode = 2 (Aggressive) — be337238
            // Min Processor State = 100 — 893dee8e
            // Max Processor State = 100 — bc5038f7

            // === PERFORMANCE THRESHOLDS ===

            // Performance increase threshold = 10%
            new(SubProcessor, "06cadf0e-64ed-448a-8927-ce7bf90eb35d", 10,
                Description: "Perf increase threshold default/E-cores"),
            new(SubProcessor, "06cadf0e-64ed-448a-8927-ce7bf90eb35e", 10,
                Description: "Perf increase threshold P-cores"),

            // Performance decrease threshold = 8%
            new(SubProcessor, "12a0ab44-fe28-4fa9-b3bd-4b64f44960a6", 8,
                Description: "Perf decrease threshold default/E-cores"),
            new(SubProcessor, "12a0ab44-fe28-4fa9-b3bd-4b64f44960a7", 8,
                Description: "Perf decrease threshold P-cores"),

            // Performance time check interval = 5000 ms (desktop default; toggled to 15 when idle re-enabled)
            new(SubProcessor, "4d2b0152-7d5c-498b-88e2-34345392a2c5", 5000,
                Description: "Performance time check interval"),

            // Performance increase time = 1 interval (immediate)
            new(SubProcessor, "984cf492-3bed-4488-a8f9-4286c97bf5aa", 1,
                Description: "Perf increase time default/E-cores"),
            new(SubProcessor, "4009efa7-e72d-4cba-9edf-91084ea8cbc3", 1,
                Description: "Perf increase time P-cores"),

            // Performance history count = 1
            new(SubProcessor, "7d24baa7-0b84-480f-840c-1b0743c00f5f", 1,
                Description: "Performance history count"),

            // === IDLE STATE TUNING ===

            // Idle demote threshold = 100 (never go to deeper C-state)
            new(SubProcessor, "4b92d758-5a24-4851-a470-815d78aee119", 100,
                Description: "Idle demote threshold"),

            // Idle promote threshold = 100
            new(SubProcessor, "7b224883-b3cc-4d79-819f-8374152cbe7c", 100,
                Description: "Idle promote threshold"),

            // Idle threshold scaling = Disabled (0)
            new(SubProcessor, "6c2993b0-8f48-481f-bcc6-00dd2742aa06", 0,
                Description: "Idle threshold scaling"),

            // === CORE PARKING DEEP SETTINGS ===

            // Core parking min cores Class 1 (P-cores) = 100
            new(SubProcessor, "0cc5b647-c1df-4637-891a-dec35c318584", 100,
                Description: "Core parking min cores Class 1"),

            // Core parking overutilization threshold = 5%
            new(SubProcessor, "943c8cb6-6f93-4227-ad87-e9a3feec08d1", 5,
                Description: "Core parking overutilization threshold"),

            // Core parking distribution threshold = 100%
            new(SubProcessor, "4bdaf4e9-d103-46d7-a5f0-6280121616ef", 100,
                Description: "Core parking distribution threshold"),

            // Core parking increase time = 1 (immediate unparking)
            new(SubProcessor, "2ddd5a84-5a71-437e-912a-db0b8c788732", 1,
                Description: "Core parking increase time"),

            // Core parking decrease time = 100 (hold cores unparked)
            new(SubProcessor, "d8edeb9b-95cf-4f95-a73c-b061973693c8", 100,
                Description: "Core parking decrease time"),

            // Core parking increase policy = All cores at once (2)
            new(SubProcessor, "9943e905-9a30-4ec1-9b99-44dd3b76f7a2", 2,
                Description: "Core parking increase policy"),

            // Core parking decrease policy = Single core at a time (1)
            new(SubProcessor, "71021b41-c749-4d21-be74-a00f335d582b", 1,
                Description: "Core parking decrease policy"),

            // Core parking concurrency headroom = 50%
            new(SubProcessor, "dfd10d17-d5eb-45dd-877a-9a34ddd15c82", 50,
                Description: "Core parking concurrency headroom"),

            // === LATENCY HINT RESPONSES ===

            new(SubProcessor, "619b7505-003b-4e82-b7a6-4dd29c300971", 100,
                Description: "Latency hint perf"),
            new(SubProcessor, "619b7505-003b-4e82-b7a6-4dd29c300972", 100,
                Description: "Latency hint perf Class 1"),
            new(SubProcessor, "616cdaa5-695e-4545-97ad-97dc2d1bdd88", 100,
                Description: "Latency hint min unparked cores"),
            new(SubProcessor, "616cdaa5-695e-4545-97ad-97dc2d1bdd89", 100,
                Description: "Latency hint min unparked cores Class 1"),

            // === FREQUENCY CAPS ===

            // No frequency cap (0 = unlimited)
            new(SubProcessor, "75b0ae3f-bce0-45a7-8c89-c9611c25e100", 0,
                Description: "Frequency cap default/E-cores"),
            new(SubProcessor, "75b0ae3f-bce0-45a7-8c89-c9611c25e101", 0,
                Description: "Frequency cap P-cores"),

            // Initial P-core perf when unparked = 100%
            new(SubProcessor, "1facfc65-a930-4bc5-9f38-504ec097bbc0", 100,
                Description: "Initial P-core perf when unparked"),

            // E-core floor performance when P-cores active = 100%
            new(SubProcessor, "fddc842b-8364-4edc-94cf-c17f60de1c80", 100,
                Description: "E-core floor perf when P-cores active"),
        };
    }

    // ── Universal Peripheral Overrides ─────────────────────────────────

    private static List<PowerOverride> GetUniversalPeripheralOverrides()
    {
        return new List<PowerOverride>
        {
            // === STORAGE ===

            // AHCI Link Power Management = Active (0)
            new(SubDisk, "0b2d69d7-a2a1-449c-9680-f91c70521c60", 0,
                Description: "AHCI link power management"),

            // NVMe Primary Idle Timeout = 0 (disabled)
            new(SubDisk, "d639518a-e56d-4345-8af2-b9f32fb26109", 0,
                Description: "NVMe primary idle timeout"),

            // NVMe Secondary Idle Timeout = 0
            new(SubDisk, "d3d55efd-c1ff-424e-9dc3-441be7833010", 0,
                Description: "NVMe secondary idle timeout"),

            // NVMe Primary Latency Tolerance = 0
            new(SubDisk, "fc95af4d-40e7-4b6d-835a-56d131dbc80e", 0,
                Description: "NVMe primary latency tolerance"),

            // NVMe Secondary Latency Tolerance = 0
            new(SubDisk, "dbc9e238-6de9-49e3-92cd-8c2b4946b472", 0,
                Description: "NVMe secondary latency tolerance"),

            // NVMe NOPPME = 0 (Off)
            new(SubDisk, "fc7372b6-ab2d-43ee-8797-15e9841f2cca", 0,
                Description: "NVMe NOPPME"),

            // === USB ===

            // USB 3 Link Power Management = Off (0)
            new(SubUsb, "d4e98f31-5ffe-4ce1-be31-1b38b384c009", 0,
                Description: "USB 3 link power management"),

            // Hub Selective Suspend Timeout = 0 (disabled)
            new(SubUsb, "0853a681-27c8-4100-a2fd-82013e970683", 0,
                Description: "Hub selective suspend timeout"),

            // Setting IOC on all TDs = Enabled (1)
            new(SubUsb, "498c044a-201b-4631-a522-5c744ed4e678", 1,
                Description: "IOC on all TDs"),

            // === WIRELESS ===

            // Power Saving Mode = Maximum Performance (0)
            new(SubWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0,
                Description: "Wireless power saving mode"),

            // === IDLE RESILIENCY ===

            // IO Coalescing Timeout = 0 (disabled)
            new(SubIdleResiliency, "c36f0eb4-2988-4a70-8eee-0884fc2c2433", 0,
                Description: "IO coalescing timeout"),

            // Deep Sleep = Disabled (0)
            new(SubIdleResiliency, "d502f7ee-1dc7-4efd-a55d-f04b6f5c0545", 0,
                Description: "Deep sleep"),

            // === INTERRUPT STEERING ===

            // Interrupt Steering Mode = Any unparked processor (3)
            new(SubInterruptSteering, "2bfc24f9-5ea2-4801-8213-3dbae01aa39d", 3,
                Description: "Interrupt steering mode"),

            // === GLOBAL SETTINGS ===

            // Device idle policy = Performance (0)
            new(SubGlobal, "4faab71a-92e5-4726-b531-224559672d19", 0,
                Description: "Device idle policy"),
        };
    }

    // ── Vendor-Specific Scheduling Overrides ──────────────────────────

    private static List<PowerOverride> GetVendorSchedulingOverrides(CpuProfile profile)
    {
        return profile switch
        {
            CpuProfile.IntelHybrid => new List<PowerOverride>
            {
                // Heterogeneous Policy = Standard Core Parking (0) — prioritizes P-cores
                new(SubProcessor, "7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5", 0,
                    Description: "Intel hybrid: heterogeneous policy"),

                // Thread Scheduling = Prefer performant processors (2)
                new(SubProcessor, "93b8b6dc-0698-4d1c-9ee4-0644e900c85d", 2,
                    Description: "Intel hybrid: thread scheduling policy"),

                // Short Running Thread = Prefer efficient processors (4)
                new(SubProcessor, "bae08b81-2d5e-4688-ad6a-13243356654b", 4,
                    Description: "Intel hybrid: short thread scheduling policy"),
            },

            CpuProfile.AmdSingleCcd or CpuProfile.AmdDualCcdX3d or CpuProfile.AmdDualCcdNonX3d => new List<PowerOverride>
            {
                // Heterogeneous Policy = Full heterogeneous (4) — let CPPC2 preferred core work
                new(SubProcessor, "7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5", 4,
                    Description: "AMD: heterogeneous policy"),

                // Thread Scheduling = Automatic (5) for both long and short threads
                new(SubProcessor, "93b8b6dc-0698-4d1c-9ee4-0644e900c85d", 5,
                    Description: "AMD: thread scheduling policy"),
                new(SubProcessor, "bae08b81-2d5e-4688-ad6a-13243356654b", 5,
                    Description: "AMD: short thread scheduling policy"),
            },

            // IntelNonHybrid, Unknown — no heterogeneous settings (GUIDs may not exist)
            _ => new List<PowerOverride>()
        };
    }

    // ── Intel Graphics Override ────────────────────────────────────────

    private static List<PowerOverride> GetIntelGraphicsOverrides()
    {
        return new List<PowerOverride>
        {
            // Intel Graphics Power Plan = Maximum Performance (2)
            new(SubIntelGraphics, "3619c3f2-afb2-4afc-b0e9-e7fef372de36", 2,
                Description: "Intel Graphics power plan"),
        };
    }

    // ── CPU Profile Detection ─────────────────────────────────────────

    /// <summary>
    /// Detects the CPU profile from WMI CPU name and hybrid detection.
    /// Used to determine vendor-specific scheduling and core parking values.
    /// </summary>
    public static CpuProfile DetectCpuProfile()
    {
        try
        {
            string cpuName = GetCpuName();
            bool isIntel = cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            bool isAmd = cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
            bool isHybrid = DetectIsHybridCpu();

            if (isIntel)
            {
                return isHybrid ? CpuProfile.IntelHybrid : CpuProfile.IntelNonHybrid;
            }

            if (isAmd)
            {
                bool isX3d = cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
                bool isDualCcd = DetectDualCcd();

                if (isDualCcd && isX3d) return CpuProfile.AmdDualCcdX3d;
                if (isDualCcd) return CpuProfile.AmdDualCcdNonX3d;
                return CpuProfile.AmdSingleCcd;
            }

            return CpuProfile.Unknown;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PowerPlanConfigurator] CPU profile detection failed");
            return CpuProfile.Unknown;
        }
    }

    /// <summary>
    /// Detects if the system has an Intel GPU (integrated or discrete).
    /// The Intel Graphics power sub-group only exists when Intel GPU drivers are installed.
    /// </summary>
    public static bool DetectHasIntelGpu()
    {
        try
        {
            using var searcher = new global::System.Management.ManagementObjectSearcher(
                "SELECT Name, AdapterCompatibility FROM Win32_VideoController");

            foreach (global::System.Management.ManagementObject obj in searcher.Get())
            {
                var compat = obj["AdapterCompatibility"]?.ToString() ?? "";
                var name = obj["Name"]?.ToString() ?? "";

                if (name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (compat.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[PowerPlanConfigurator] Intel GPU detection failed");
        }

        return false;
    }

    private static string GetCpuName()
    {
        try
        {
            using var searcher = new global::System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");

            foreach (global::System.Management.ManagementObject obj in searcher.Get())
            {
                return obj["Name"]?.ToString() ?? "Unknown";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PowerPlanConfigurator] CPU name detection failed");
        }

        return "Unknown";
    }

    private static bool DetectIsHybridCpu()
    {
        try
        {
            const string cpuRegPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor";
            using var cpuKey = Registry.LocalMachine.OpenSubKey(cpuRegPath);
            if (cpuKey == null) return false;

            bool hasP = false, hasE = false;

            foreach (var subName in cpuKey.GetSubKeyNames())
            {
                using var core = cpuKey.OpenSubKey(subName);
                if (core == null) continue;

                var effClass = core.GetValue("EfficiencyClass");
                if (effClass is int eff)
                {
                    if (eff == 0) hasP = true;
                    else hasE = true;
                }
            }

            return hasP && hasE;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[PowerPlanConfigurator] Hybrid CPU detection failed");
            return false;
        }
    }

    /// <summary>
    /// Dual-CCD detection via L3 cache grouping.
    /// Groups cores by their processor group / NUMA node — if 2+ L3 cache groups exist, it's dual-CCD.
    /// Falls back to core count heuristic (16+ cores on AMD = likely dual-CCD).
    /// </summary>
    private static bool DetectDualCcd()
    {
        try
        {
            // Use processor group detection: dual-CCD Ryzen has 2 NUMA nodes
            // Each CCD has its own L3 cache, so we count distinct L3 caches
            using var searcher = new global::System.Management.ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");

            foreach (global::System.Management.ManagementObject obj in searcher.Get())
            {
                int cores = Convert.ToInt32(obj["NumberOfCores"]);
                // AMD dual-CCD chips have 12+ cores (6+6, 8+8, or 16 cores)
                // Single-CCD tops out at 8 cores
                if (cores > 8) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[PowerPlanConfigurator] Dual-CCD detection failed");
        }

        return false;
    }
}
