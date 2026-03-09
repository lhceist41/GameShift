namespace GameShift.Core.Optimization;

/// <summary>
/// Represents the CPU core topology detected via GetSystemCpuSetInformation.
/// Categorizes cores by EfficiencyClass for hybrid CPU support:
///   0 = Performance cores (P-cores)
///   1 = Efficiency cores (E-cores)
///   2+ = Low-Power Efficiency cores (LP-E-cores, Panther Lake and beyond)
/// Also detects AMD X3D V-Cache CCD for targeted pinning.
/// </summary>
public class CpuTopology
{
    /// <summary>Performance cores (EfficiencyClass = 0).</summary>
    public List<CpuCore> PerformanceCores { get; set; } = new();

    /// <summary>Efficiency cores (EfficiencyClass = 1).</summary>
    public List<CpuCore> EfficiencyCores { get; set; } = new();

    /// <summary>Low-Power Efficiency cores (EfficiencyClass >= 2, e.g., Intel Panther Lake).</summary>
    public List<CpuCore> LowPowerCores { get; set; } = new();

    /// <summary>True if this CPU has distinct core types (P+E or P+E+LP).</summary>
    public bool IsHybrid => EfficiencyCores.Count > 0 || LowPowerCores.Count > 0;

    /// <summary>True if three or more distinct core tiers exist (Panther Lake architecture).</summary>
    public bool HasThreeTiers => LowPowerCores.Count > 0;

    /// <summary>
    /// Index of the CCD containing V-Cache (AMD X3D processors).
    /// Null if not an X3D processor or if V-Cache CCD cannot be determined.
    /// Cores are grouped by LastLevelCacheIndex to identify CCDs.
    /// </summary>
    public int? VCacheCcdIndex { get; set; }

    /// <summary>
    /// Returns all cores across all tiers.
    /// </summary>
    public IEnumerable<CpuCore> AllCores =>
        PerformanceCores.Concat(EfficiencyCores).Concat(LowPowerCores);
}

/// <summary>
/// Represents a single logical processor with its CPU Set ID and topology metadata.
/// </summary>
public class CpuCore
{
    /// <summary>Unique CPU Set ID used by SetProcessDefaultCpuSets().</summary>
    public uint CpuSetId { get; set; }

    /// <summary>Efficiency class: 0 = P-core, 1 = E-core, 2+ = LP-E-core.</summary>
    public byte EfficiencyClass { get; set; }

    /// <summary>Physical core index within the processor.</summary>
    public byte CoreIndex { get; set; }

    /// <summary>Last-level cache index — used for CCD/L3 cache grouping (AMD X3D).</summary>
    public byte LastLevelCacheIndex { get; set; }

    /// <summary>Processor group (0 for most consumer CPUs, >0 for >64 logical processor systems).</summary>
    public ushort Group { get; set; }

    /// <summary>Logical processor index within the group (used for legacy affinity mask fallback).</summary>
    public byte LogicalProcessorIndex { get; set; }
}
