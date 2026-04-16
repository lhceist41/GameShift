using System.Text.Json;
using Serilog;
using Serilog.Events;

namespace GameShift.Core.Config;

/// <summary>
/// Manages application settings persistence to %AppData%/GameShift/settings.json.
/// Also configures Serilog for structured logging.
/// </summary>
public static class SettingsManager
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameShift");

    private static readonly string SettingsFilePath = Path.Combine(AppDataPath, "settings.json");
    private static readonly string LogsPath = Path.Combine(AppDataPath, "logs");

    private static readonly object _fileLock = new();
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
    /// Returns default settings if file doesn't exist.
    /// </summary>
    /// <returns>Loaded or default AppSettings instance</returns>
    public static AppSettings Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Logger.Information("Settings file not found, using defaults");
                    return new AppSettings();
                }

                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);

                if (settings == null)
                {
                    Logger.Warning("Settings file was null after deserialization, using defaults");
                    return new AppSettings();
                }

                // Only reconfigure logger if logging settings actually changed
                if (settings.EnableLogging != _lastLoggingEnabled || settings.LogLevel != _lastLogLevel)
                {
                    ConfigureLogger(settings);
                }
                Logger.Information("Settings loaded from {Path}", SettingsFilePath);

                return settings;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load settings, using defaults");
                return new AppSettings();
            }
        }
    }

    /// <summary>
    /// Saves settings to %AppData%/GameShift/settings.json.
    /// Creates directory if it doesn't exist.
    /// </summary>
    /// <param name="settings">Settings to persist</param>
    public static void Save(AppSettings settings)
    {
        lock (_fileLock)
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(AppDataPath))
                {
                    Directory.CreateDirectory(AppDataPath);
                    Logger.Information("Created AppData directory at {Path}", AppDataPath);
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(SettingsFilePath, json);

                // Only reconfigure logger if logging settings actually changed
                if (settings.EnableLogging != _lastLoggingEnabled || settings.LogLevel != _lastLogLevel)
                {
                    ConfigureLogger(settings);
                }
                Logger.Information("Settings saved to {Path}", SettingsFilePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save settings to {Path}", SettingsFilePath);
                throw;
            }
        }
    }

    /// <summary>
    /// Configures Serilog with rolling file sink and settings-based log level.
    /// Uses structured logging with 31-day retention.
    /// </summary>
    /// <param name="settings">Settings to apply to logger configuration</param>
    private static void ConfigureLogger(AppSettings settings)
    {
        // Ensure logs directory exists
        if (!Directory.Exists(LogsPath))
        {
            Directory.CreateDirectory(LogsPath);
        }

        // Parse log level from settings
        var logLevel = Enum.TryParse<LogEventLevel>(settings.LogLevel, out var level)
            ? level
            : LogEventLevel.Information;

        // Only configure logger if logging is enabled
        if (settings.EnableLogging)
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
}
