using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using GameShift.Core.Config;
using Windows.Management.Deployment;

namespace GameShift.Core.Detection;

/// <summary>
/// Scans for Xbox Game Pass and Microsoft Store games using the WinRT PackageManager API.
/// MSIX/UWP packages are enumerated and filtered using a multi-signal approach to identify games:
///   1. Curated title list (embedded xbox_titles.json)
///   2. Known game publisher certificates
///   3. Game engine file signatures (Unreal .pak, Unity UnityPlayer.dll, etc.)
/// Executables are extracted from AppxManifest.xml.
/// Includes COM activation infrastructure for launching UWP games via AUMID.
/// </summary>
public class XboxLibraryScanner : ILibraryScanner
{
    /// <summary>
    /// Metadata for a known Xbox/Store game from the curated title list.
    /// </summary>
    private class XboxTitleEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Executable { get; set; } = string.Empty;
    }

    private static readonly Lazy<Dictionary<string, XboxTitleEntry>> _knownTitles = new(LoadKnownTitles);

    /// <summary>
    /// Known game publisher certificate subjects. Games from these publishers are auto-detected.
    /// </summary>
    private static readonly HashSet<string> KnownGamePublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
    };

    /// <summary>
    /// File patterns that indicate a game engine installation directory.
    /// </summary>
    private static readonly string[] GameEngineIndicators =
    {
        "UnityPlayer.dll",      // Unity Engine
        "UE4Game.dll",          // Unreal Engine 4
        "*.pak",                // Unreal Engine pak files
        "EOSSDK*.dll",          // Epic Online Services SDK (many cross-platform games)
        "fmod.dll",             // FMOD audio (common in games)
        "XInput1_4.dll",        // DirectInput/XInput (gamepad support)
        "d3d12.dll",            // Custom D3D12 loader (some games)
        "steam_api64.dll",      // Steam API (cross-launcher titles)
        "PhysX*.dll",           // PhysX engine
        "CriWare*.dll",         // CriWare middleware (Japanese games)
    };

    /// <inheritdoc/>
    public string LauncherName => "Xbox / Microsoft Store";

    /// <inheritdoc/>
    public bool IsInstalled
    {
        get
        {
            try
            {
                // PackageManager is always available on Windows 10+
                var pm = new PackageManager();
                return pm != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public List<GameInfo> ScanInstalledGames()
    {
        var games = new List<GameInfo>();

        try
        {
            var packageManager = new PackageManager();
            var packages = packageManager.FindPackagesForUser(string.Empty);

            foreach (var package in packages)
            {
                try
                {
                    // Skip framework packages (runtime dependencies, not apps)
                    if (package.IsFramework) continue;

                    // Skip non-Store packages (sideloaded, system, developer)
                    if (package.SignatureKind != Windows.ApplicationModel.PackageSignatureKind.Store)
                        continue;

                    // Skip resource-only and optional packages
                    if (package.IsResourcePackage) continue;
                    if (package.IsOptional) continue;

                    string familyName = package.Id.FamilyName;

                    // Check curated title list first (most reliable)
                    if (_knownTitles.Value.TryGetValue(familyName, out var knownTitle))
                    {
                        var gameInfo = CreateGameInfoFromKnownTitle(package, knownTitle);
                        if (gameInfo != null)
                        {
                            games.Add(gameInfo);
                            SettingsManager.Logger.Debug(
                                "XboxLibraryScanner: Found known title: {Name} ({FamilyName})",
                                knownTitle.Name, familyName);
                        }
                        continue;
                    }

                    // Heuristic checks for unknown packages
                    if (!IsLikelyGame(package)) continue;

                    var heuristicGameInfo = ExtractGameInfo(package);
                    if (heuristicGameInfo != null)
                    {
                        games.Add(heuristicGameInfo);
                        SettingsManager.Logger.Information(
                            "XboxLibraryScanner: Detected probable game via heuristics: {Name} ({FamilyName})",
                            heuristicGameInfo.GameName, familyName);
                    }
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Debug(
                        "XboxLibraryScanner: Skipped package {FullName}: {Message}",
                        package.Id.FullName, ex.Message);
                }
            }

            SettingsManager.Logger.Information(
                "XboxLibraryScanner: Scan complete — found {Count} games", games.Count);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(
                "XboxLibraryScanner: Library scan failed: {Message}", ex.Message);
        }

        return games;
    }

    // ── Game detection heuristics ─────────────────────────────────────

    /// <summary>
    /// Multi-signal heuristic to determine if an MSIX package is a game.
    /// </summary>
    private bool IsLikelyGame(Windows.ApplicationModel.Package package)
    {
        try
        {
            string publisher = package.Id.Publisher;
            string installPath = package.InstalledPath;

            // Signal 1: Known game publisher
            if (KnownGamePublishers.Contains(publisher))
            {
                // Microsoft publishes both games and apps — need additional check
                // Only consider it a game if it also has game engine files
                if (ContainsGameEngineFiles(installPath))
                    return true;
            }

            // Signal 2: Check for game engine files in the install directory
            if (ContainsGameEngineFiles(installPath))
                return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(
                "XboxLibraryScanner: IsLikelyGame check failed for {Name}: {Message}",
                package.Id.Name, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Checks if the install directory contains known game engine files.
    /// </summary>
    private static bool ContainsGameEngineFiles(string installPath)
    {
        if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
            return false;

        try
        {
            foreach (var pattern in GameEngineIndicators)
            {
                if (pattern.Contains('*'))
                {
                    // Wildcard pattern — check top-level only for performance
                    if (Directory.GetFiles(installPath, pattern, SearchOption.TopDirectoryOnly).Length > 0)
                        return true;
                }
                else
                {
                    if (File.Exists(Path.Combine(installPath, pattern)))
                        return true;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // WindowsApps directory may restrict access — expected
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(
                "XboxLibraryScanner: ContainsGameEngineFiles failed for {Path}: {Message}",
                installPath, ex.Message);
        }

        return false;
    }

    // ── GameInfo extraction ──────────────────────────────────────────

    /// <summary>
    /// Creates a GameInfo from a known curated title entry.
    /// </summary>
    private GameInfo? CreateGameInfoFromKnownTitle(
        Windows.ApplicationModel.Package package,
        XboxTitleEntry knownTitle)
    {
        string processName = Path.GetFileNameWithoutExtension(knownTitle.Executable);
        if (string.IsNullOrEmpty(processName)) return null;

        return new GameInfo
        {
            Id = GameInfo.GenerateId("xbox", package.Id.FamilyName),
            GameName = knownTitle.Name,
            ExecutablePath = string.Empty, // UWP apps live in protected WindowsApps
            InstallDirectory = package.InstalledPath,
            LauncherSource = "Xbox",
            LauncherId = package.Id.FamilyName
        };
    }

    /// <summary>
    /// Extracts game info from a package by parsing its AppxManifest.xml.
    /// </summary>
    private GameInfo? ExtractGameInfo(Windows.ApplicationModel.Package package)
    {
        try
        {
            string manifestPath = Path.Combine(package.InstalledPath, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) return null;

            var doc = XDocument.Load(manifestPath);
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

            // Find the Application element with the executable
            var app = doc.Descendants(ns + "Application").FirstOrDefault();
            if (app == null) return null;

            string? executable = app.Attribute("Executable")?.Value;
            if (string.IsNullOrEmpty(executable)) return null;

            string processName = Path.GetFileNameWithoutExtension(executable);
            string displayName = package.DisplayName;

            return new GameInfo
            {
                Id = GameInfo.GenerateId("xbox", package.Id.FamilyName),
                GameName = !string.IsNullOrEmpty(displayName) ? displayName : package.Id.Name,
                ExecutablePath = string.Empty,
                InstallDirectory = package.InstalledPath,
                LauncherSource = "Xbox",
                LauncherId = package.Id.FamilyName
            };
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Debug(
                "XboxLibraryScanner: ExtractGameInfo failed for {Name}: {Message}",
                package.Id.Name, ex.Message);
            return null;
        }
    }

    // ── Known titles list ────────────────────────────────────────────

    /// <summary>
    /// Loads the curated Xbox title list from embedded resource.
    /// </summary>
    private static Dictionary<string, XboxTitleEntry> LoadKnownTitles()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("xbox_titles.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                SettingsManager.Logger.Warning(
                    "XboxLibraryScanner: xbox_titles.json embedded resource not found");
                return new Dictionary<string, XboxTitleEntry>();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return new Dictionary<string, XboxTitleEntry>();

            var titles = JsonSerializer.Deserialize<Dictionary<string, XboxTitleEntry>>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return titles ?? new Dictionary<string, XboxTitleEntry>();
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(
                "XboxLibraryScanner: Failed to load xbox_titles.json: {Message}", ex.Message);
            return new Dictionary<string, XboxTitleEntry>();
        }
    }

    // ── UWP App Launch Support ───────────────────────────────────────

    /// <summary>
    /// COM interface for launching UWP/MSIX apps by Application User Model ID.
    /// </summary>
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            uint options,
            out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    /// <summary>
    /// Launches a UWP/MSIX game by its Application User Model ID (AUMID).
    /// Format: "PackageFamilyName!ApplicationId"
    /// Returns the launched process ID, or 0 on failure.
    /// </summary>
    /// <param name="aumid">Application User Model ID (e.g., "Microsoft.624F8B84B80_8wekyb3d8bbwe!App")</param>
    /// <returns>Process ID of the launched application, or 0 if launch failed.</returns>
    public static uint LaunchGame(string aumid)
    {
        try
        {
            var aam = (IApplicationActivationManager)new ApplicationActivationManager();
            aam.ActivateApplication(aumid, null, 0, out uint pid);

            SettingsManager.Logger.Information(
                "XboxLibraryScanner: Launched {Aumid} — PID {Pid}", aumid, pid);
            return pid;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex,
                "XboxLibraryScanner: Failed to launch {Aumid}", aumid);
            return 0;
        }
    }
}
