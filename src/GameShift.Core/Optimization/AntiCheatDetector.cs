using System.ServiceProcess;
using GameShift.Core.Config;
using Microsoft.Win32;

namespace GameShift.Core.Optimization;

/// <summary>
/// Detects installed anti-cheat systems on the local machine.
/// Used to gate VBS/HVCI disable (Vanguard/FACEIT require it) and inform
/// the IFEO priority/affinity fallback path.
/// All detection methods are non-invasive (read-only service/file/registry checks).
/// </summary>
public static class AntiCheatDetector
{
    // ── Detection results (cached) ──────────────────────────────────

    private static volatile bool _detected;
    private static List<DetectedAntiCheat> _detectedAntiCheats = new();

    /// <summary>
    /// Information about a detected anti-cheat system.
    /// </summary>
    public record DetectedAntiCheat(AntiCheatType Type, string DisplayName, string Evidence);

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Detects all installed anti-cheat systems. Caches results after first call.
    /// Call Refresh() to force re-detection.
    /// </summary>
    public static List<DetectedAntiCheat> DetectAll()
    {
        if (_detected) return _detectedAntiCheats;

        var results = new List<DetectedAntiCheat>();

        // Vanguard (Riot)
        var vanguardEvidence = DetectVanguard();
        if (vanguardEvidence != null)
            results.Add(new DetectedAntiCheat(AntiCheatType.RiotVanguard, "Riot Vanguard", vanguardEvidence));

        // FACEIT
        var faceitEvidence = DetectFaceit();
        if (faceitEvidence != null)
            results.Add(new DetectedAntiCheat(AntiCheatType.FaceitAC, "FACEIT Anti-Cheat", faceitEvidence));

        // EasyAntiCheat
        var eacEvidence = DetectEasyAntiCheat();
        if (eacEvidence != null)
            results.Add(new DetectedAntiCheat(AntiCheatType.EasyAntiCheat, "Easy Anti-Cheat", eacEvidence));

        // BattlEye
        var battleyeEvidence = DetectBattlEye();
        if (battleyeEvidence != null)
            results.Add(new DetectedAntiCheat(AntiCheatType.BattlEye, "BattlEye", battleyeEvidence));

        _detectedAntiCheats = results; // atomic reference swap — safe for concurrent readers
        _detected = true;             // volatile write — publish after list is fully built

        SettingsManager.Logger.Information(
            "AntiCheatDetector: Detected {Count} anti-cheat system(s): [{Names}]",
            results.Count,
            string.Join(", ", results.Select(r => r.DisplayName)));

        return results;
    }

    /// <summary>
    /// Returns true if any installed anti-cheat requires VBS/HVCI to be enabled.
    /// Currently: Riot Vanguard and FACEIT Anti-Cheat.
    /// </summary>
    public static bool IsVbsRequiredByAntiCheat()
    {
        return DetectAll().Any(ac =>
            ac.Type is AntiCheatType.RiotVanguard or AntiCheatType.FaceitAC);
    }

    /// <summary>
    /// Returns the list of anti-cheats that require VBS/HVCI.
    /// Used for UI display (showing which anti-cheats are blocking VBS disable).
    /// </summary>
    public static List<DetectedAntiCheat> GetVbsRequiringAntiCheats()
    {
        return DetectAll()
            .Where(ac => ac.Type is AntiCheatType.RiotVanguard or AntiCheatType.FaceitAC)
            .ToList();
    }

    /// <summary>
    /// Forces re-detection of all anti-cheat systems on next call to DetectAll().
    /// </summary>
    public static void Refresh()
    {
        _detected = false;
        _detectedAntiCheats = new();
    }

    // ── Individual detection methods ────────────────────────────────

    /// <summary>
    /// Detects Riot Vanguard via service, driver file, or registry.
    /// </summary>
    private static string? DetectVanguard()
    {
        try
        {
            // Check for vgc service
            if (ServiceExists("vgc"))
                return "Service 'vgc' found";

            // Check for vgk.sys driver
            var driverPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "vgk.sys");
            if (File.Exists(driverPath))
                return $"Driver found: {driverPath}";

            // Check registry
            using var regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Riot Vanguard");
            if (regKey != null)
                return "Registry key 'SOFTWARE\\Riot Vanguard' found";

            // Check install path
            if (File.Exists(@"C:\Program Files\Riot Vanguard\vgc.exe"))
                return "Install path found: C:\\Program Files\\Riot Vanguard\\vgc.exe";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(ex, "AntiCheatDetector: Error detecting Vanguard");
        }

        return null;
    }

    /// <summary>
    /// Detects FACEIT Anti-Cheat via service or install path.
    /// </summary>
    private static string? DetectFaceit()
    {
        try
        {
            // Check for FACEIT service
            if (ServiceExists("FACEIT"))
                return "Service 'FACEIT' found";

            // Check for FACEITService.exe in common install locations
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var faceitPath = Path.Combine(programFiles, "FACEIT AC", "FACEITService.exe");
            if (File.Exists(faceitPath))
                return $"Install path found: {faceitPath}";

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var faceitPathX86 = Path.Combine(programFilesX86, "FACEIT AC", "FACEITService.exe");
            if (File.Exists(faceitPathX86))
                return $"Install path found: {faceitPathX86}";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(ex, "AntiCheatDetector: Error detecting FACEIT");
        }

        return null;
    }

    /// <summary>
    /// Detects Easy Anti-Cheat via service.
    /// </summary>
    private static string? DetectEasyAntiCheat()
    {
        try
        {
            if (ServiceExists("EasyAntiCheat"))
                return "Service 'EasyAntiCheat' found";

            if (ServiceExists("EasyAntiCheat_EOS"))
                return "Service 'EasyAntiCheat_EOS' found";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(ex, "AntiCheatDetector: Error detecting EasyAntiCheat");
        }

        return null;
    }

    /// <summary>
    /// Detects BattlEye via service or driver file.
    /// </summary>
    private static string? DetectBattlEye()
    {
        try
        {
            if (ServiceExists("BEService"))
                return "Service 'BEService' found";

            var driverPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "BEDaisy.sys");
            if (File.Exists(driverPath))
                return $"Driver found: {driverPath}";
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(ex, "AntiCheatDetector: Error detecting BattlEye");
        }

        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks if a Windows service exists by name.
    /// Does NOT check if the service is running — just whether it's registered.
    /// </summary>
    private static bool ServiceExists(string serviceName)
    {
        try
        {
            var services = ServiceController.GetServices();
            try
            {
                return services.Any(s =>
                    string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                foreach (var s in services) s.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }
}
