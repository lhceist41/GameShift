using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading.Tasks;
using Serilog;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Result of a driver matched against the KnownDpcOffenders database.
/// Contains the offender entry plus runtime info (file version, running state).
/// </summary>
public class DpcOffenderMatch
{
    /// <summary>Driver service name from WMI.</summary>
    public string ServiceName { get; init; } = "";

    /// <summary>Driver file name (e.g. "nvlddmkm.sys").</summary>
    public string DriverFileName { get; init; } = "";

    /// <summary>File version string from the driver binary, or "Unknown" if unavailable.</summary>
    public string FileVersion { get; init; } = "Unknown";

    /// <summary>Full file path of the driver binary.</summary>
    public string FilePath { get; init; } = "";

    /// <summary>The matched KnownDpcOffenders entry with fix recommendations.</summary>
    public DpcOffenderEntry Offender { get; init; } = null!;
}

/// <summary>
/// Result of a full DPC troubleshooter analysis pass.
/// </summary>
public class DpcAnalysisResult
{
    /// <summary>When the analysis was performed.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Matched offenders found among running kernel drivers.</summary>
    public IReadOnlyList<DpcOffenderMatch> Matches { get; init; } = Array.Empty<DpcOffenderMatch>();

    /// <summary>Total number of running kernel drivers scanned.</summary>
    public int DriversScanned { get; init; }

    /// <summary>Current average DPC latency at time of analysis (0 if monitor unavailable).</summary>
    public double AverageLatencyMicroseconds { get; init; }

    /// <summary>Peak DPC latency at time of analysis (0 if monitor unavailable).</summary>
    public double PeakLatencyMicroseconds { get; init; }
}

/// <summary>
/// Enumerates running kernel drivers via WMI and cross-references against
/// KnownDpcOffenders to identify likely DPC latency culprits.
/// One-shot analysis; create a new instance (or call AnalyzeAsync again) to rescan.
/// </summary>
public class DpcTroubleshooter
{
    private readonly DpcLatencyMonitor? _monitor;

    public DpcTroubleshooter(DpcLatencyMonitor? monitor = null)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Queries running kernel drivers and matches them against the known offender database.
    /// Safe to call from any thread. Returns results sorted by severity (High first).
    /// </summary>
    public async Task<DpcAnalysisResult> AnalyzeAsync()
    {
        return await Task.Run(() => Analyze());
    }

    private DpcAnalysisResult Analyze()
    {
        var matches = new List<DpcOffenderMatch>();
        int driversScanned = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PathName, State FROM Win32_SystemDriver " +
                "WHERE State='Running' AND ServiceType='Kernel Driver'");

            using var results = searcher.Get();

            foreach (ManagementObject driver in results)
            {
                driversScanned++;

                var pathName = driver["PathName"]?.ToString();
                if (string.IsNullOrEmpty(pathName)) continue;

                // Extract filename from path
                // WMI paths can be: \SystemRoot\System32\drivers\foo.sys
                //                   C:\Windows\System32\drivers\foo.sys
                //                   \\?\C:\...\foo.sys
                var fileName = Path.GetFileName(pathName);
                if (string.IsNullOrEmpty(fileName)) continue;

                var offender = KnownDpcOffenders.GetOffender(fileName);
                if (offender == null) continue;

                // Try to get file version
                string fileVersion = "Unknown";
                string fullPath = ResolvDriverPath(pathName);
                try
                {
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    {
                        var versionInfo = FileVersionInfo.GetVersionInfo(fullPath);
                        if (!string.IsNullOrEmpty(versionInfo.FileVersion))
                            fileVersion = versionInfo.FileVersion;
                    }
                }
                catch
                {
                    // Can't read version — not critical
                }

                matches.Add(new DpcOffenderMatch
                {
                    ServiceName = driver["Name"]?.ToString() ?? "",
                    DriverFileName = fileName,
                    FileVersion = fileVersion,
                    FilePath = fullPath,
                    Offender = offender
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DpcTroubleshooter: WMI driver enumeration failed");
        }

        // Sort by severity: High first, then Medium, then Low
        matches.Sort((a, b) => b.Offender.Severity.CompareTo(a.Offender.Severity));

        return new DpcAnalysisResult
        {
            Timestamp = DateTime.Now,
            Matches = matches,
            DriversScanned = driversScanned,
            AverageLatencyMicroseconds = _monitor?.AverageLatencyMicroseconds ?? 0,
            PeakLatencyMicroseconds = _monitor?.PeakLatencyMicroseconds ?? 0
        };
    }

    /// <summary>
    /// Resolves WMI driver paths to full filesystem paths.
    /// Handles \SystemRoot\, \??\, and absolute paths.
    /// </summary>
    private static string ResolvDriverPath(string wmiPath)
    {
        if (string.IsNullOrEmpty(wmiPath)) return "";

        // \SystemRoot\System32\drivers\foo.sys → C:\Windows\System32\drivers\foo.sys
        if (wmiPath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windowsDir, wmiPath[@"\SystemRoot\".Length..]);
        }

        // \??\C:\Windows\... → C:\Windows\...
        if (wmiPath.StartsWith(@"\??\"))
        {
            return wmiPath[4..];
        }

        // Already a full path
        if (Path.IsPathRooted(wmiPath))
        {
            return wmiPath;
        }

        // Relative path — assume System32\drivers
        var sysDriversDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "drivers");
        return Path.Combine(sysDriversDir, wmiPath);
    }
}
