using System.Text.Json.Serialization;

namespace GameShift.Core.Monitoring;

// NOTE: DriverCategory enum is defined in KnownDpcOffenders.cs and reused here.
// It contains: Audio, Network, GPU, Storage, USB, Framework, Other.

/// <summary>
/// Information about an installed system driver detected via WMI.
/// Named "InstalledDriverInfo" to avoid conflict with KnownDriverDatabase.DriverInfo
/// (which describes known problematic drivers from the embedded database).
/// </summary>
public class InstalledDriverInfo
{
    /// <summary>Device name (e.g., "NVIDIA GeForce RTX 4090").</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Internal driver version (e.g., "32.0.15.6094").</summary>
    public string DriverVersion { get; set; } = "";

    /// <summary>User-facing version (e.g., "560.94" for NVIDIA).</summary>
    public string FriendlyVersion { get; set; } = "";

    /// <summary>Driver date from WMI.</summary>
    public DateTime DriverDate { get; set; }

    /// <summary>Vendor name: "NVIDIA", "AMD", "Intel", "Realtek", etc.</summary>
    public string Vendor { get; set; } = "";

    /// <summary>INF file name (e.g., "oem42.inf").</summary>
    public string InfName { get; set; } = "";

    /// <summary>Driver category.</summary>
    public DriverCategory Category { get; set; } = DriverCategory.Other;
}

/// <summary>
/// Severity level for a driver advisory.
/// </summary>
public enum AdvisorySeverity
{
    Info,
    Elevated,
    Warning,
    Critical
}

/// <summary>
/// A known issue or advisory for a specific driver version range.
/// </summary>
public class DriverAdvisory
{
    /// <summary>Unique advisory identifier (e.g., "NV-2024-551-24H2").</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Vendor this advisory applies to ("NVIDIA", "AMD", "Intel", "Realtek").</summary>
    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "";

    /// <summary>Affected driver version range.</summary>
    [JsonPropertyName("affectedVersionRange")]
    public VersionRange AffectedVersionRange { get; set; } = new();

    /// <summary>Severity level.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";

    /// <summary>Short title for the advisory.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Detailed description of the issue.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Recommended action to resolve the issue.</summary>
    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = "";

    /// <summary>
    /// Optional Windows build condition (e.g., ">=26100" for 24H2+).
    /// Null means advisory applies to all Windows versions.
    /// </summary>
    [JsonPropertyName("windowsVersionCondition")]
    public string? WindowsVersionCondition { get; set; }

    /// <summary>
    /// Optional game-specific condition — advisory only relevant when these games are detected.
    /// Null means advisory applies regardless of installed games.
    /// </summary>
    [JsonPropertyName("gameCondition")]
    public List<string>? GameCondition { get; set; }

    /// <summary>Optional URL for more information.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Parsed severity as enum.</summary>
    [JsonIgnore]
    public AdvisorySeverity SeverityLevel => Severity?.ToLowerInvariant() switch
    {
        "critical" => AdvisorySeverity.Critical,
        "warning" => AdvisorySeverity.Warning,
        "elevated" => AdvisorySeverity.Elevated,
        _ => AdvisorySeverity.Info
    };
}

/// <summary>
/// Represents a min/max version range for driver advisory matching.
/// </summary>
public class VersionRange
{
    [JsonPropertyName("min")]
    public string Min { get; set; } = "0.0";

    [JsonPropertyName("max")]
    public string Max { get; set; } = "999.999";
}

/// <summary>
/// Root object for the driver_advisories.json file.
/// </summary>
public class DriverAdvisoryDatabase
{
    [JsonPropertyName("advisories")]
    public List<DriverAdvisory> Advisories { get; set; } = new();
}
