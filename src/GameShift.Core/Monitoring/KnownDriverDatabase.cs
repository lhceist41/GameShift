using System.Reflection;
using System.Text.Json;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// JSON-backed database of known DPC-problematic drivers with fix recommendations.
/// Loaded once at startup from embedded resource, cached in memory.
/// Thread-safe for concurrent TryGetDriver lookups from the trace engine.
/// </summary>
public class KnownDriverDatabase
{
    private readonly Dictionary<string, DriverInfo> _drivers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the known driver database from the embedded JSON resource.
    /// Call once at startup; the instance is then safe for concurrent reads.
    /// </summary>
    public static KnownDriverDatabase Load()
    {
        var db = new KnownDriverDatabase();

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("known-drivers.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Log.Warning("KnownDriverDatabase: embedded resource not found");
                return db;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var root = JsonSerializer.Deserialize<DriverDatabaseRoot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (root?.Drivers != null)
            {
                foreach (var driver in root.Drivers)
                {
                    db._drivers[driver.FileName] = driver;
                }
            }

            Log.Information("KnownDriverDatabase: loaded {Count} driver entries", db._drivers.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KnownDriverDatabase: failed to load embedded resource");
        }

        return db;
    }

    /// <summary>
    /// Fast lookup for the trace engine hot path. Thread-safe after construction.
    /// </summary>
    public bool TryGetDriver(string fileName, out DriverInfo info)
    {
        return _drivers.TryGetValue(fileName, out info!);
    }

    /// <summary>
    /// Returns all known driver entries.
    /// </summary>
    public IReadOnlyCollection<DriverInfo> GetAll() => _drivers.Values;

    // -- JSON deserialization models --

    private class DriverDatabaseRoot
    {
        public List<DriverInfo> Drivers { get; set; } = new();
    }
}

/// <summary>
/// A known DPC-problematic driver with full fix information.
/// </summary>
public class DriverInfo
{
    public string FileName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Category { get; set; } = "";
    public string KnownIssue { get; set; } = "";
    public string SimpleExplanation { get; set; } = "";
    public string GamingImpact { get; set; } = "";
    public List<DriverAutoFix> AutoFixes { get; set; } = new();
    public List<string> ManualFixes { get; set; } = new();
}

/// <summary>
/// An automated fix for a known DPC-problematic driver.
/// </summary>
public class DriverAutoFix
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string Impact { get; set; } = "";
    public bool RequiresReboot { get; set; }
    public string SimpleExplanation { get; set; } = "";
    public string TechnicalExplanation { get; set; } = "";

    // -- Action-type-specific fields (only relevant ones populated per fix) --
    public string? Property { get; set; }
    public string? Value { get; set; }
    public string? RegistryPath { get; set; }
    public string? RegistryKey { get; set; }
    public string? RegistryValue { get; set; }
    public string? RegistryType { get; set; }
    public string? Command { get; set; }
    public string? RevertCommand { get; set; }
    public string? Subgroup { get; set; }
    public string? Setting { get; set; }
}
