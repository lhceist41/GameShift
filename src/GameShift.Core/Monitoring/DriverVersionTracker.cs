using System.Management;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Detects installed driver versions via WMI and checks them against
/// a curated advisory database of known-bad driver versions.
/// Runs asynchronously on startup and provides advisory data for
/// Dashboard warnings and DPC Doctor cross-referencing.
/// </summary>
public class DriverVersionTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Primary GPU driver info.</summary>
    public InstalledDriverInfo? GpuDriver { get; private set; }

    /// <summary>Primary network adapter driver info.</summary>
    public InstalledDriverInfo? NetworkDriver { get; private set; }

    /// <summary>Primary audio driver info.</summary>
    public InstalledDriverInfo? AudioDriver { get; private set; }

    /// <summary>Active advisories matching installed drivers.</summary>
    public List<DriverAdvisory> ActiveAdvisories { get; private set; } = new();

    /// <summary>Whether scan has completed at least once.</summary>
    public bool HasScanned { get; private set; }

    /// <summary>Fired when advisories are updated after scan + check.</summary>
    public event Action<List<DriverAdvisory>>? AdvisoriesUpdated;

    // ── Scan ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full scan: detect drivers via WMI, then check against advisory database.
    /// Safe to call from any thread. WMI queries run synchronously internally.
    /// </summary>
    public void Scan()
    {
        try
        {
            ScanGpuDriver();
            ScanNetworkDriver();
            ScanAudioDriver();
            HasScanned = true;

            Log.Information(
                "DriverVersionTracker: Scan complete — GPU={Gpu}, Net={Net}, Audio={Audio}",
                GpuDriver?.FriendlyVersion ?? "N/A",
                NetworkDriver?.DriverVersion ?? "N/A",
                AudioDriver?.DriverVersion ?? "N/A");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverVersionTracker: Scan failed");
        }
    }

    /// <summary>
    /// Checks installed drivers against the advisory database and updates ActiveAdvisories.
    /// </summary>
    public void CheckForAdvisories()
    {
        ActiveAdvisories.Clear();

        try
        {
            var database = LoadAdvisoryDatabase();
            if (database == null || database.Advisories.Count == 0) return;

            int windowsBuild = Environment.OSVersion.Version.Build;

            foreach (var advisory in database.Advisories)
            {
                var relevantDriver = GetDriverForVendor(advisory.Vendor);
                if (relevantDriver == null) continue;

                // Vendor must match
                if (!relevantDriver.Vendor.Equals(advisory.Vendor, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check version range
                if (!IsVersionInRange(relevantDriver.FriendlyVersion, advisory.AffectedVersionRange))
                    continue;

                // Check Windows version condition (if any)
                if (advisory.WindowsVersionCondition != null &&
                    !EvaluateWindowsCondition(advisory.WindowsVersionCondition, windowsBuild))
                    continue;

                ActiveAdvisories.Add(advisory);
            }

            if (ActiveAdvisories.Count > 0)
            {
                Log.Information("DriverVersionTracker: Found {Count} active advisories", ActiveAdvisories.Count);
                AdvisoriesUpdated?.Invoke(ActiveAdvisories);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverVersionTracker: Advisory check failed");
        }
    }

    /// <summary>
    /// Runs scan + advisory check asynchronously. Suitable for calling from app startup.
    /// </summary>
    public async Task ScanAndCheckAsync()
    {
        await Task.Run(() =>
        {
            Scan();
            CheckForAdvisories();
        });
    }

    // ── GPU driver detection ────────────────────────────────────────────

    private void ScanGpuDriver()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, DriverDate, AdapterCompatibility FROM Win32_VideoController");

            foreach (ManagementObject gpu in searcher.Get())
            {
                string name = gpu["Name"]?.ToString() ?? "";

                // Skip virtual/basic display adapters
                if (name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
                    continue;

                string driverVersion = gpu["DriverVersion"]?.ToString() ?? "";
                string vendor = DetectGpuVendor(name, gpu["AdapterCompatibility"]?.ToString());

                GpuDriver = new InstalledDriverInfo
                {
                    DeviceName = name,
                    DriverVersion = driverVersion,
                    DriverDate = ParseWmiDate(gpu["DriverDate"]?.ToString()),
                    Vendor = vendor,
                    Category = DriverCategory.GPU,
                    FriendlyVersion = ConvertToFriendlyVersion(driverVersion, vendor)
                };

                break; // Primary GPU only
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverVersionTracker: GPU driver scan failed");
        }
    }

    // ── Network driver detection ────────────────────────────────────────

    private void ScanNetworkDriver()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass='NET'");

            foreach (ManagementObject net in searcher.Get())
            {
                string desc = net["Description"]?.ToString() ?? "";

                // Skip virtual, VPN, and Hyper-V adapters
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase))
                    continue;

                string version = net["DriverVersion"]?.ToString() ?? "";

                NetworkDriver = new InstalledDriverInfo
                {
                    DeviceName = desc,
                    DriverVersion = version,
                    DriverDate = ParseWmiDate(net["DriverDate"]?.ToString()),
                    Category = DriverCategory.Network,
                    Vendor = DetectNetworkVendor(desc),
                    FriendlyVersion = version
                };

                break; // Primary physical adapter only
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverVersionTracker: Network driver scan failed");
        }
    }

    // ── Audio driver detection ──────────────────────────────────────────

    private void ScanAudioDriver()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass='MEDIA'");

            foreach (ManagementObject audio in searcher.Get())
            {
                string desc = audio["Description"]?.ToString() ?? "";
                string version = audio["DriverVersion"]?.ToString() ?? "";

                AudioDriver = new InstalledDriverInfo
                {
                    DeviceName = desc,
                    DriverVersion = version,
                    DriverDate = ParseWmiDate(audio["DriverDate"]?.ToString()),
                    Category = DriverCategory.Audio,
                    Vendor = DetectAudioVendor(desc),
                    FriendlyVersion = version
                };

                break; // Primary audio device only
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverVersionTracker: Audio driver scan failed");
        }
    }

    // ── NVIDIA version conversion ───────────────────────────────────────

    /// <summary>
    /// Converts internal driver versions to user-facing format.
    /// NVIDIA: "32.0.15.6094" → "560.94" (last 5 digits of last two octets, insert decimal).
    /// Other vendors: pass through unchanged.
    /// </summary>
    private static string ConvertToFriendlyVersion(string driverVersion, string vendor)
    {
        if (!vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(driverVersion))
            return driverVersion;

        // NVIDIA internal format: "32.0.15.6094"
        // Public format: take last two octets → "15" + "6094" = "156094"
        // Take last 5 chars → "56094" → insert dot after 3rd char → "560.94"
        var parts = driverVersion.Split('.');
        if (parts.Length < 4) return driverVersion;

        string combined = parts[2].PadLeft(2, '0') + parts[3].PadLeft(4, '0');

        if (combined.Length >= 5)
        {
            string last5 = combined.Substring(combined.Length - 5);
            return last5.Insert(3, ".");
        }

        return driverVersion;
    }

    // ── Advisory database ───────────────────────────────────────────────

    /// <summary>
    /// Loads the advisory database from embedded resource.
    /// </summary>
    private static DriverAdvisoryDatabase? LoadAdvisoryDatabase()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("driver_advisories.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Log.Warning("DriverVersionTracker: driver_advisories.json embedded resource not found");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            return JsonSerializer.Deserialize<DriverAdvisoryDatabase>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverVersionTracker: Failed to load advisory database");
            return null;
        }
    }

    // ── Version comparison ──────────────────────────────────────────────

    /// <summary>
    /// Checks if a version string falls within a min/max range (inclusive).
    /// </summary>
    private static bool IsVersionInRange(string version, VersionRange range)
    {
        if (string.IsNullOrEmpty(version)) return false;

        if (!Version.TryParse(NormalizeVersion(version), out var ver)) return false;
        if (!Version.TryParse(NormalizeVersion(range.Min), out var min)) return false;
        if (!Version.TryParse(NormalizeVersion(range.Max), out var max)) return false;

        return ver >= min && ver <= max;
    }

    /// <summary>
    /// Ensures a version string has at least 2 parts for System.Version parsing.
    /// </summary>
    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return "0.0";

        var parts = v.Split('.');
        return parts.Length switch
        {
            1 => $"{v}.0",
            _ => v
        };
    }

    /// <summary>
    /// Evaluates a Windows version condition string (e.g., ">=26100").
    /// </summary>
    private static bool EvaluateWindowsCondition(string condition, int windowsBuild)
    {
        if (string.IsNullOrEmpty(condition)) return true;

        // Parse operator and value
        string op;
        string valueStr;

        if (condition.StartsWith(">="))
        {
            op = ">=";
            valueStr = condition.Substring(2);
        }
        else if (condition.StartsWith("<="))
        {
            op = "<=";
            valueStr = condition.Substring(2);
        }
        else if (condition.StartsWith(">"))
        {
            op = ">";
            valueStr = condition.Substring(1);
        }
        else if (condition.StartsWith("<"))
        {
            op = "<";
            valueStr = condition.Substring(1);
        }
        else if (condition.StartsWith("=="))
        {
            op = "==";
            valueStr = condition.Substring(2);
        }
        else
        {
            return true; // Unknown format — don't filter
        }

        if (!int.TryParse(valueStr.Trim(), out int requiredBuild))
            return true;

        return op switch
        {
            ">=" => windowsBuild >= requiredBuild,
            "<=" => windowsBuild <= requiredBuild,
            ">" => windowsBuild > requiredBuild,
            "<" => windowsBuild < requiredBuild,
            "==" => windowsBuild == requiredBuild,
            _ => true
        };
    }

    // ── Vendor detection helpers ────────────────────────────────────────

    private InstalledDriverInfo? GetDriverForVendor(string vendor)
    {
        return vendor.ToUpperInvariant() switch
        {
            "NVIDIA" or "AMD" or "INTEL" => GpuDriver,
            "REALTEK" => AudioDriver,
            _ => null
        };
    }

    private static string DetectGpuVendor(string name, string? adapterCompat)
    {
        if (ContainsIgnoreCase(adapterCompat, "NVIDIA") || ContainsIgnoreCase(name, "NVIDIA"))
            return "NVIDIA";
        if (ContainsIgnoreCase(adapterCompat, "AMD") ||
            ContainsIgnoreCase(adapterCompat, "Advanced Micro Devices") ||
            ContainsIgnoreCase(name, "Radeon"))
            return "AMD";
        if (ContainsIgnoreCase(adapterCompat, "Intel") || ContainsIgnoreCase(name, "Intel"))
            return "Intel";
        return "Unknown";
    }

    private static string DetectNetworkVendor(string description)
    {
        if (ContainsIgnoreCase(description, "Intel")) return "Intel";
        if (ContainsIgnoreCase(description, "Realtek")) return "Realtek";
        if (ContainsIgnoreCase(description, "Qualcomm")) return "Qualcomm";
        if (ContainsIgnoreCase(description, "Killer")) return "Killer";
        if (ContainsIgnoreCase(description, "Broadcom")) return "Broadcom";
        if (ContainsIgnoreCase(description, "Marvell")) return "Marvell";
        if (ContainsIgnoreCase(description, "MediaTek")) return "MediaTek";
        return "Unknown";
    }

    private static string DetectAudioVendor(string description)
    {
        if (ContainsIgnoreCase(description, "Realtek")) return "Realtek";
        if (ContainsIgnoreCase(description, "NVIDIA")) return "NVIDIA";
        if (ContainsIgnoreCase(description, "AMD")) return "AMD";
        if (ContainsIgnoreCase(description, "Intel")) return "Intel";
        if (ContainsIgnoreCase(description, "Nahimic")) return "Nahimic";
        return "Unknown";
    }

    private static DateTime ParseWmiDate(string? wmiDate)
    {
        if (string.IsNullOrEmpty(wmiDate)) return DateTime.MinValue;

        try
        {
            return ManagementDateTimeConverter.ToDateTime(wmiDate);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return source != null && source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a combined summary of driver advisories suitable for DPC Doctor cross-referencing.
    /// Matches advisory vendor against a DPC offender driver filename.
    /// </summary>
    /// <param name="driverFileName">The DPC offender driver filename (e.g., "nvlddmkm.sys").</param>
    /// <returns>Matching advisories, or empty list if none.</returns>
    public List<DriverAdvisory> GetAdvisoriesForDriver(string driverFileName)
    {
        if (ActiveAdvisories.Count == 0 || string.IsNullOrEmpty(driverFileName))
            return new List<DriverAdvisory>();

        // Map known driver filenames to vendors for cross-referencing
        string? vendor = driverFileName.ToLowerInvariant() switch
        {
            "nvlddmkm.sys" or "nvhda64v.sys" or "nvldumdx.sys" => "NVIDIA",
            "amdkmdag.sys" or "amdkmdap.sys" or "atikmpag.sys" => "AMD",
            "igdkmd64.sys" or "igd12umd64.sys" => "Intel",
            "rtkvhd64.sys" or "rtkaudbus64.sys" => "Realtek",
            _ => null
        };

        if (vendor == null) return new List<DriverAdvisory>();

        return ActiveAdvisories
            .Where(a => a.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
