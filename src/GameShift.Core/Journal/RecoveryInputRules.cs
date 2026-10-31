using System.Globalization;
using System.Text.Json;
using GameShift.Core.GameProfiles;
using Microsoft.Win32;

namespace GameShift.Core.Journal;

/// <summary>
/// What a journal record may name before a revert path acts on it. The journal is replayed
/// elevated, so a record is only trusted to describe something GameShift itself wrote: the sets
/// below are what the apply paths of 3.8 and 3.9 recorded, and anything else is refused.
/// </summary>
internal static class RecoveryInputRules
{
    // ── powercfg ──────────────────────────────────────────────────────────────

    /// <summary>A GUID, returned in the one form powercfg receives, so no other token reaches it.</summary>
    public static bool TryPowerGuid(string? value, out string canonical)
    {
        canonical = Guid.TryParse(value, out var guid) ? guid.ToString("D") : string.Empty;
        return canonical.Length > 0;
    }

    /// <summary>
    /// A setting index as 3.9 captured it: a decimal Int32, negative when the hex value had the top
    /// bit set. The caller passes the parsed number on, never the recorded text.
    /// </summary>
    public static bool TryPowerIndex(string? value, out int index) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);

    /// <summary>A string property of a record object that is a GUID, in canonical form.</summary>
    public static bool TryGuidProperty(JsonElement record, string name, out string canonical)
    {
        canonical = string.Empty;
        return record.ValueKind == JsonValueKind.Object &&
               record.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               TryPowerGuid(element.GetString(), out canonical);
    }

    public static bool TryInt32Property(JsonElement record, string name, out int value)
    {
        value = 0;
        return record.ValueKind == JsonValueKind.Object &&
               record.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }

    // ── IFEO PerfOptions (ProcessPriorityBooster, HybridCpuDetector) ──────────

    public const string IfeoBasePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>
    /// <c>IFEO\&lt;image&gt;\PerfOptions</c> for one image file name, the only key either module wrote.
    /// </summary>
    public static bool IsIfeoPerfOptionsPath(string? keyPath)
    {
        const string suffix = @"\PerfOptions";
        if (keyPath == null ||
            !keyPath.StartsWith(IfeoBasePath + @"\", StringComparison.OrdinalIgnoreCase) ||
            !keyPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var image = keyPath[(IfeoBasePath.Length + 1)..^suffix.Length];
        return image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
               image.Length > ".exe".Length &&
               image.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Recorded originals hold at most the one value the module wrote.</summary>
    public static bool AreIfeoOriginals(IReadOnlyDictionary<string, int> originals, string valueName) =>
        originals.Keys.All(name => string.Equals(name, valueName, StringComparison.Ordinal));

    // ── SessionSystemTweaksOptimizer ──────────────────────────────────────────

    private const string MmcssProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmcssGamesPath = MmcssProfilePath + @"\Tasks\Games";
    private const string HidClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}";

    private static readonly Dictionary<string, RegistryValueKind> MmcssProfileValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NetworkThrottlingIndex"] = RegistryValueKind.DWord,
        ["SystemResponsiveness"] = RegistryValueKind.DWord,
    };

    private static readonly Dictionary<string, RegistryValueKind> MmcssGamesValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Affinity"] = RegistryValueKind.DWord,
        ["Background Only"] = RegistryValueKind.String,
        ["Clock Rate"] = RegistryValueKind.DWord,
        ["GPU Priority"] = RegistryValueKind.DWord,
        ["Priority"] = RegistryValueKind.DWord,
        ["Scheduling Category"] = RegistryValueKind.String,
        ["SFIO Priority"] = RegistryValueKind.String,
    };

    private static readonly Dictionary<string, RegistryValueKind> HidDeviceValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SelectiveSuspendEnabled"] = RegistryValueKind.DWord,
        ["EnhancedPowerMgmtEnabled"] = RegistryValueKind.DWord,
    };

    public static bool IsSessionTweak(bool hklm, string? path, string? name, string? kind)
    {
        if (!hklm || path == null || name == null)
            return false;

        var values = string.Equals(path, MmcssProfilePath, StringComparison.OrdinalIgnoreCase) ? MmcssProfileValues
            : string.Equals(path, MmcssGamesPath, StringComparison.OrdinalIgnoreCase) ? MmcssGamesValues
            : IsClassInstance(path, HidClassPath) ? HidDeviceValues
            : null;
        return values != null && values.TryGetValue(name, out var expected) && expected.ToString() == kind;
    }

    // ── NetworkOptimizer ──────────────────────────────────────────────────────

    private const string TcpipInterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string NetworkClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly HashSet<string> TcpipInterfaceValues = new(StringComparer.OrdinalIgnoreCase)
        { "TcpAckFrequency", "TCPNoDelay" };

    private static readonly HashSet<string> NetworkAdapterValues = new(StringComparer.OrdinalIgnoreCase)
        { "*InterruptModeration", "*LsoV2IPv4", "*LsoV2IPv6", "*RscIPv4", "*RscIPv6" };

    /// <summary>
    /// Splits a recorded <c>HKLM\key\value</c> and checks it is one the optimizer wrote. Up to 3.8.6
    /// it also wrote the two MMCSS SystemProfile values that later moved to the session tweaks.
    /// </summary>
    public static bool TryNetworkRegistryValue(string fullPath, out string subKeyPath, out string valueName)
    {
        subKeyPath = valueName = string.Empty;
        const string hklm = @"HKLM\";
        int split = fullPath.LastIndexOf('\\');
        if (!fullPath.StartsWith(hklm, StringComparison.OrdinalIgnoreCase) || split <= hklm.Length)
            return false;

        var key = fullPath[hklm.Length..split];
        var name = fullPath[(split + 1)..];
        bool allowed =
            (IsGuidSubKey(key, TcpipInterfacesPath) && TcpipInterfaceValues.Contains(name)) ||
            (IsClassInstance(key, NetworkClassPath) && NetworkAdapterValues.Contains(name)) ||
            (string.Equals(key, MmcssProfilePath, StringComparison.OrdinalIgnoreCase) && MmcssProfileValues.ContainsKey(name));
        if (!allowed)
            return false;

        (subKeyPath, valueName) = (key, name);
        return true;
    }

    public static bool IsNetworkService(string name) => string.Equals(name, "DoSvc", StringComparison.OrdinalIgnoreCase);

    // ── GpuDriverOptimizer ────────────────────────────────────────────────────

    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Dictionary<string, (string Name, RegistryValueKind Kind)[]> GpuFixedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"HKEY_CURRENT_USER\SOFTWARE\NVIDIA Corporation\Global\FTS"] = new[] { ("EnableRID73779", RegistryValueKind.DWord) },
        [@"HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global\NVTweak"] = new[] { ("NVCplShaderCacheSize", RegistryValueKind.DWord) },
        [@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm"] = new[]
        {
            ("DisableDynamicPstate", RegistryValueKind.DWord),
            ("RmCudaSchedulingMode", RegistryValueKind.DWord),
        },
        [@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"] = new[]
        {
            ("TdrDelay", RegistryValueKind.DWord),
            ("TdrDdiDelay", RegistryValueKind.DWord),
        },
    };

    private static readonly (string Name, RegistryValueKind Kind)[] AmdDriverValues =
    {
        ("EnableUlps", RegistryValueKind.DWord),
        ("PP_SclkDeepSleepDisable", RegistryValueKind.DWord),
    };

    private static readonly (string Name, RegistryValueKind Kind)[] AmdUmdValues =
    {
        ("AntiLag_DevMode", RegistryValueKind.String),
        ("Chill_Enabled", RegistryValueKind.String),
        ("SurfaceFormatReplacements", RegistryValueKind.String),
        ("ShaderCache", RegistryValueKind.String),
        ("FlipQueueSize", RegistryValueKind.Binary),
    };

    /// <summary>
    /// Checks a recorded <c>HKEY_...</c> key path, value name and kind against what the optimizer
    /// wrote, and returns the hive and subkey to open.
    /// </summary>
    public static bool TryGpuDriverValue(
        string? keyPath, string? valueName, string? kind,
        out RecoveryHive hive, out string subKeyPath, out RegistryValueKind valueKind)
    {
        hive = RecoveryHive.LocalMachine;
        subKeyPath = string.Empty;
        valueKind = RegistryValueKind.Unknown;
        if (keyPath == null || valueName == null)
            return false;

        const string hklm = @"HKEY_LOCAL_MACHINE\";
        const string hkcu = @"HKEY_CURRENT_USER\";
        (string Name, RegistryValueKind Kind)[]? values = null;
        if (GpuFixedKeys.TryGetValue(keyPath, out var fixedValues))
            values = fixedValues;
        else if (keyPath.StartsWith(hklm, StringComparison.OrdinalIgnoreCase))
        {
            var key = keyPath[hklm.Length..];
            if (IsClassInstance(key, DisplayClassPath))
                values = AmdDriverValues;
            else if (key.EndsWith(@"\UMD", StringComparison.OrdinalIgnoreCase) && IsClassInstance(key[..^@"\UMD".Length], DisplayClassPath))
                values = AmdUmdValues;
        }

        if (values == null)
            return false;
        var entry = Array.Find(values, v => string.Equals(v.Name, valueName, StringComparison.OrdinalIgnoreCase));
        if (entry.Name == null || entry.Kind.ToString() != kind)
            return false;

        if (keyPath.StartsWith(hkcu, StringComparison.OrdinalIgnoreCase))
            (hive, subKeyPath) = (RecoveryHive.CurrentUser, keyPath[hkcu.Length..]);
        else
            subKeyPath = keyPath[hklm.Length..];
        valueKind = entry.Kind;
        return true;
    }

    // ── ScheduledTaskSuppressor ───────────────────────────────────────────────

    public static readonly IReadOnlySet<string> SuppressedTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Defrag\ScheduledDefrag",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\Maintenance\WinSAT",
        @"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Work",
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
        @"\Microsoft\Windows\.NET Framework\.NET Framework NGEN v4.0.30319",
        @"\Microsoft\Windows\.NET Framework\.NET Framework NGEN v4.0.30319 64",
        @"\Microsoft\Windows\Diagnosis\Scheduled",
        @"\Microsoft\Windows\Registry\RegIdleBackup",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan",
        @"\Microsoft\Windows\Windows Defender\Windows Defender Cache Maintenance",
    };

    // ── ServiceSuppressor ─────────────────────────────────────────────────────

    /// <summary>
    /// Earlier versions also recorded the running dependents they found at stop time, so any service
    /// name may appear; only its syntax is checked.
    /// </summary>
    public static bool IsServiceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && name.IndexOfAny(new[] { '\\', '/' }) < 0;

    // ── CompetitiveMode ───────────────────────────────────────────────────────

    private static readonly HashSet<string> SuspendedOverlays = new(StringComparer.OrdinalIgnoreCase)
        { "Discord", "GameOverlayUI", "NVIDIA Share", "nvcontainer" };

    public static bool IsSuspendedOverlay(string? processName) => processName != null && SuspendedOverlays.Contains(processName);

    // ── Game action records ───────────────────────────────────────────────────

    private const string AppCompatLayersPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>
    /// The two shapes the removed fullscreen-optimization and AMD driver-override actions wrote: a
    /// compatibility layer string for a game executable, and the AntiLag_DevMode value of a display
    /// adapter (under UMD, or the adapter key itself when it had no UMD subkey). The driver override
    /// recorded whatever kind the value already had, so any string or numeric kind is accepted there.
    /// </summary>
    public static bool TryLegacyRegistryAction(
        string? hive, string? path, string? name, string? kind, bool existed, out RecoveryHive target)
    {
        target = RecoveryHive.LocalMachine;
        if (path == null || name == null)
            return false;

        if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase))
        {
            target = RecoveryHive.CurrentUser;
            return string.Equals(path, AppCompatLayersPath, StringComparison.OrdinalIgnoreCase) &&
                   kind == nameof(RegistryValueKind.String) &&
                   Path.IsPathFullyQualified(name) &&
                   name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase))
            return false;

        var adapter = path.EndsWith(@"\UMD", StringComparison.OrdinalIgnoreCase) ? path[..^@"\UMD".Length] : path;
        return IsClassInstance(adapter, DisplayClassPath) &&
               string.Equals(name, "AntiLag_DevMode", StringComparison.Ordinal) &&
               (!existed || kind is nameof(RegistryValueKind.String) or nameof(RegistryValueKind.ExpandString)
                   or nameof(RegistryValueKind.MultiString) or nameof(RegistryValueKind.DWord)
                   or nameof(RegistryValueKind.QWord) or nameof(RegistryValueKind.Binary));
    }

    public static bool IsLegacyFirewallRule(string? ruleName) =>
        ruleName != null &&
        BuiltInProfiles.GetFirewallLeftovers().Any(rule => string.Equals(rule.RuleName, ruleName, StringComparison.Ordinal));

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>A device instance key directly under a device class key, such as <c>...\0000</c>.</summary>
    private static bool IsClassInstance(string path, string classPath) =>
        path.Length == classPath.Length + 5 &&
        path.StartsWith(classPath + @"\", StringComparison.OrdinalIgnoreCase) &&
        path[^4..].All(char.IsAsciiDigit);

    private static bool IsGuidSubKey(string path, string parentPath) =>
        path.StartsWith(parentPath + @"\", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(path[(parentPath.Length + 1)..], "B", out _);
}
