using System.Text.Json;
using GameShift.Core.Journal;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GameShift.Core.Config;

/// <summary>
/// Manages application settings persistence to %AppData%/GameShift/settings.json.
/// Also configures Serilog for structured logging to %ProgramData%/GameShift/logs.
/// </summary>
public static class SettingsManager
{
    private static readonly string DefaultAppDataPath = TestHooks.DefaultAppDataPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameShift");

    /// <summary>
    /// Internal test hook. Null in production; the test assembly sets it before first use to
    /// replace the default %AppData%\GameShift folder. Read once when SettingsManager initializes.
    /// A nested type so setting it does not run SettingsManager's static constructor, which
    /// already resolves the folder and opens the log.
    /// </summary>
    internal static class TestHooks
    {
        internal static string? DefaultAppDataPathOverride { get; set; }

        /// <summary>
        /// Replaces the admin-only %ProgramData%\GameShift\logs folder with a plain folder the
        /// unelevated test run can write. Read once when SettingsManager initializes.
        /// </summary>
        internal static string? LogsPathOverride { get; set; }
    }

    /// <summary>
    /// Internal test hook. Null in production; tests can set this to redirect the settings
    /// file to an isolated location (e.g., a temp directory). When set, the containing
    /// directory is used as <see cref="AppDataPath"/>; the logs stay where they are.
    /// Tests must reset this to null during cleanup.
    /// All reads/writes of this field are guarded by <see cref="_fileLock"/> via the
    /// public Load/Save/Get* methods.
    /// </summary>
    internal static string? SettingsFilePathOverride { get; set; }

    /// <summary>Internal test hook: the clock that names a kept unreadable settings file.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private const string UnreadableSettingsPrefix = "settings.unreadable-";
    private const int MaxKeptNameAttempts = 100;

    private static string SettingsFilePath =>
        SettingsFilePathOverride ?? Path.Combine(DefaultAppDataPath, "settings.json");

    private static string AppDataPath =>
        SettingsFilePathOverride is { } overridePath
            ? (Path.GetDirectoryName(overridePath) ?? DefaultAppDataPath)
            : DefaultAppDataPath;

    private static string SettingsFileName => Path.GetFileName(SettingsFilePath);

    private static readonly string LogsPath = TestHooks.LogsPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameShift", "logs");

    private static readonly object _fileLock = new();
    private static readonly object _logFolderLock = new();
    private static bool _logFolderPrepared;
    private static bool _logFolderReady;

    // Kept for the process lifetime: its open handles stop the folders from being renamed or replaced.
    private static LogFolder? _logFolder;
    private static string? _lastRefusedPath;
    private static bool _lastLoggingEnabled;
    private static string? _lastLogLevel;

    /// <summary>
    /// Global logger instance configured with settings.
    /// Other classes can use this for structured logging.
    /// </summary>
    public static ILogger Logger { get; private set; }

    static SettingsManager()
    {
        // Initialize with default settings to configure logger
        var settings = new AppSettings();
        ConfigureLogger(settings);
        Logger = Serilog.Log.Logger;
    }

    /// <summary>
    /// Loads settings from %AppData%/GameShift/settings.json.
    /// Returns default settings if file doesn't exist or could not be read.
    /// </summary>
    /// <returns>Loaded or default AppSettings instance</returns>
    public static AppSettings Load()
    {
        TryLoad(out var settings);
        return settings;
    }

    /// <summary>
    /// Loads settings like <see cref="Load"/>. False when a settings file is in place but could not
    /// be read as settings; <paramref name="settings"/> are then defaults. A file that opened but did
    /// not deserialize is first kept aside (see <see cref="GetUnreadableSettingsFiles"/>) and then
    /// counts as missing. A missing file or a refused data folder (see
    /// <see cref="IsDataFolderRefused"/>) gives defaults and true.
    /// </summary>
    public static bool TryLoad(out AppSettings settings)
    {
        lock (_fileLock)
        {
            try
            {
                var folder = GetDataFolder();
                if (folder == null)
                {
                    settings = new AppSettings();
                    return true;
                }

                if (!TryRead(folder, out var loaded))
                {
                    settings = new AppSettings();
                    return false;
                }

                if (loaded == null)
                {
                    Logger.Information("No settings file in place, using defaults");
                    settings = new AppSettings();
                    return true;
                }

                // Only reconfigure logger if logging settings actually changed
                if (loaded.EnableLogging != _lastLoggingEnabled || loaded.LogLevel != _lastLogLevel)
                {
                    ConfigureLogger(loaded);
                }
                Logger.Information("Settings loaded from {Path}", SettingsFilePath);

                settings = loaded;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load settings, using defaults");
                settings = new AppSettings();
                return false;
            }
        }
    }

    /// <summary>
    /// Saves settings to %AppData%/GameShift/settings.json.
    /// Creates the folder if it doesn't exist; saves nothing when the folder was refused. Throws
    /// <see cref="IOException"/>, saving nothing, when a settings file is in place that could not
    /// be read, since it may hold changes still to undo.
    /// </summary>
    /// <param name="settings">Settings to persist</param>
    public static void Save(AppSettings settings)
    {
        lock (_fileLock)
        {
            try
            {
                var folder = GetDataFolder();
                if (folder != null && !TryRead(folder, out _))
                {
                    Logger.Warning("Settings were not saved: {Path} could not be read and was left in place", SettingsFilePath);
                    throw UnreadableInPlace();
                }

                Write(folder, settings);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save settings to {Path}", SettingsFilePath);
                throw;
            }
        }
    }

    /// <summary>
    /// Atomically applies a mutation to the settings: loads the current settings, runs
    /// <paramref name="mutate"/>, and saves the result, all while holding the file lock. Use this
    /// instead of a separate Load() -> mutate -> Save() when the caller may run concurrently with
    /// another writer (e.g. a background detection-thread tip dismissal racing a UI settings save),
    /// where the unguarded read-modify-write sequence can lose one writer's update.
    /// Throws <see cref="IOException"/>, without running <paramref name="mutate"/>, when a settings
    /// file is in place that could not be read.
    /// </summary>
    /// <param name="mutate">Mutation applied to the loaded settings before they are saved.</param>
    public static void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        // Monitor (lock) is reentrant, so the nested TryLoad() re-enters this same lock on this
        // thread; the whole load-mutate-save is therefore atomic against other Update/Save callers.
        lock (_fileLock)
        {
            if (!TryLoad(out var settings))
            {
                Logger.Warning("A settings change was dropped: {Path} could not be read and was left in place", SettingsFilePath);
                throw UnreadableInPlace();
            }

            mutate(settings);
            try
            {
                Write(GetDataFolder(), settings);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save settings to {Path}", SettingsFilePath);
                throw;
            }
        }
    }

    // Thrown like a failed write, so callers that record a change before making it stop first.
    private static IOException UnreadableInPlace() =>
        new($"The settings file {SettingsFilePath} could not be read and is still in place, so nothing was saved");

    /// <summary>
    /// The full paths of settings files that could not be read and were kept aside in the data
    /// folder. Empty when the folder was refused.
    /// </summary>
    public static IReadOnlyList<string> GetUnreadableSettingsFiles()
    {
        lock (_fileLock)
        {
            var folder = GetDataFolder();
            if (folder == null)
                return [];

            // Names only; nothing is opened by path.
            return Directory.EnumerateFiles(folder.Path, UnreadableSettingsPrefix + "*.json")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Reads the settings file. False when one is in place but could not be read as settings.
    /// <paramref name="loaded"/> is null when there is none, including after an unreadable one was
    /// kept aside.
    /// </summary>
    private static bool TryRead(UserDataFolder folder, out AppSettings? loaded)
    {
        loaded = null;
        string? json;
        try
        {
            json = folder.ReadAllText(SettingsFileName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not read {Path}", SettingsFilePath);
            return false;
        }

        if (json == null)
            return true;

        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch (JsonException ex)
        {
            Logger.Warning(ex, "{Path} does not hold valid settings", SettingsFilePath);
        }

        return loaded != null || KeepAside(folder);
    }

    /// <summary>
    /// Renames the unreadable settings file to <c>settings.unreadable-&lt;UTC time&gt;.json</c> in the
    /// same folder, so no later save can replace the changes it may record. False when it could not
    /// be moved; it is then still in place.
    /// </summary>
    private static bool KeepAside(UserDataFolder folder)
    {
        var stamp = UtcNow().ToString("yyyyMMdd-HHmmss", global::System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            for (int n = 0; n < MaxKeptNameAttempts; n++)
            {
                var name = n == 0
                    ? $"{UnreadableSettingsPrefix}{stamp}.json"
                    : $"{UnreadableSettingsPrefix}{stamp}-{n}.json";
                if (folder.RenameFile(SettingsFileName, name))
                {
                    Logger.Warning("{Path} could not be read; it was kept as {Name} and defaults are used", SettingsFilePath, name);
                    return true;
                }
            }

            Logger.Error("{Path} could not be read and was left in place: no free name to keep it under", SettingsFilePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "{Path} could not be read and could not be kept aside; it was left in place", SettingsFilePath);
        }

        return false;
    }

    /// <summary>Writes <paramref name="settings"/>; nothing is written when <paramref name="folder"/> was refused.</summary>
    private static void Write(UserDataFolder? folder, AppSettings settings)
    {
        if (folder != null)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            folder.WriteAllText(SettingsFileName, json);
        }

        // Only reconfigure logger if logging settings actually changed
        if (settings.EnableLogging != _lastLoggingEnabled || settings.LogLevel != _lastLogLevel)
        {
            ConfigureLogger(settings);
        }

        if (folder != null)
            Logger.Information("Settings saved to {Path}", SettingsFilePath);
        else
            Logger.Warning("Settings were not saved: {Path} was refused", AppDataPath);
    }

    /// <summary>
    /// Configures Serilog with rolling file sink and settings-based log level.
    /// Uses structured logging with 31-day retention.
    /// </summary>
    /// <param name="settings">Settings to apply to logger configuration</param>
    private static void ConfigureLogger(AppSettings settings)
    {
        // Parse log level from settings
        var logLevel = Enum.TryParse<LogEventLevel>(settings.LogLevel, out var level)
            ? level
            : LogEventLevel.Information;

        // Only configure logger if logging is enabled
        if (settings.EnableLogging && IsLogsFolderReady())
        {
            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .WriteTo.File(
                    path: Path.Combine(LogsPath, "gameshift-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        else if (settings.EnableLogging)
        {
            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .WriteTo.Sink(new TraceSink())
                .CreateLogger();
        }
        else
        {
            // Create a silent logger if logging is disabled
            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Fatal()
                .CreateLogger();
        }

        Logger = Serilog.Log.Logger;

        // Cache the current logging settings to avoid redundant reconfiguration
        _lastLoggingEnabled = settings.EnableLogging;
        _lastLogLevel = settings.LogLevel;
    }

    /// <summary>
    /// Gets the path to the AppData directory for this application.
    /// </summary>
    public static string GetAppDataPath() => AppDataPath;

    /// <summary>
    /// Gets the path to the logs directory.
    /// </summary>
    public static string GetLogsPath() => LogsPath;

    /// <summary>
    /// Whether the logs folder was accepted for this run; nothing may be written to
    /// <see cref="GetLogsPath"/> when it was not. Prepared on first use.
    /// </summary>
    public static bool IsLogsFolderReady()
    {
        lock (_logFolderLock)
        {
            if (_logFolderPrepared)
                return _logFolderReady;
            _logFolderPrepared = true;

            if (TestHooks.LogsPathOverride != null)
            {
                Directory.CreateDirectory(LogsPath);
                return _logFolderReady = true;
            }

            _logFolder = LogFolder.Prepare(LogsPath, JournalTrust.AdministratorsSid, out var refusal);
            if (_logFolder == null)
                ReportToDebugger($"Logging to files is off for this run: {LogsPath}: {refusal}");
            return _logFolderReady = _logFolder != null;
        }
    }

    /// <summary>
    /// Whether this looks like the first start: the data folder holds no settings file. False when
    /// the folder was refused, since then nothing a first-run setup chooses could be kept.
    /// </summary>
    public static bool IsFirstRun()
    {
        lock (_fileLock)
        {
            try
            {
                return GetDataFolder() is { } folder && !folder.FileExists(SettingsFileName);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not check for a settings file");
                return false;
            }
        }
    }

    /// <summary>
    /// The first-run setup was closed without finishing. Saves the defaults with Start with Windows
    /// off, so the elevated logon task never appears without a choice, and the setup counts as done.
    /// Keeps nothing when the data folder was refused. Throws like <see cref="Update"/>.
    /// </summary>
    public static void RecordFirstRunSkipped() => Update(s => s.StartWithWindows = false);

    /// <summary>
    /// Whether the data folder was refused for this run, so <see cref="Load"/> returns defaults and
    /// <see cref="Save"/> keeps nothing. True as well when the folder could not be checked.
    /// </summary>
    public static bool IsDataFolderRefused()
    {
        lock (_fileLock)
        {
            try
            {
                return GetDataFolder() == null;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not check the data folder");
                return true;
            }
        }
    }

    /// <summary>The verified data folder for this run; null, with a logged warning, when it was refused.</summary>
    internal static UserDataFolder? GetDataFolder()
    {
        var path = AppDataPath;
        var folder = UserDataFolder.Get(path, out var refusal);
        if (folder == null && Interlocked.Exchange(ref _lastRefusedPath, path) != path)
            Logger.Warning("{Path} was refused: {Reason}; nothing is saved there this run", path, refusal);
        return folder;
    }

    private static void ReportToDebugger(string message)
    {
        Console.Error.WriteLine(message);
        global::System.Diagnostics.Trace.WriteLine(message);
    }

    /// <summary>Sends log events to an attached debugger when the logs folder was refused.</summary>
    private sealed class TraceSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent) =>
            global::System.Diagnostics.Trace.WriteLine($"[{logEvent.Level}] {logEvent.RenderMessage()}");
    }
}
