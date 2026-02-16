using System.Text.Json;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Detection;

/// <summary>
/// Manages the persistent known games list.
/// Merges scanner results with manually-added games and persists to JSON.
/// Supports manual game addition with JSON persistence.
/// </summary>
public class KnownGamesStore
{
    private readonly List<GameInfo> _games;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new KnownGamesStore instance.
    /// </summary>
    public KnownGamesStore()
    {
        _games = new List<GameInfo>();
        _filePath = Path.Combine(SettingsManager.GetAppDataPath(), "known_games.json");
        _logger = SettingsManager.Logger;
    }

    /// <summary>
    /// Loads known games from disk storage.
    /// If file doesn't exist or is malformed, starts with empty list.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _logger.Information("Known games file not found, starting with empty list");
                    return;
                }

                var json = File.ReadAllText(_filePath);
                var games = JsonSerializer.Deserialize<List<GameInfo>>(json);

                if (games != null)
                {
                    _games.Clear();
                    _games.AddRange(games);
                    _logger.Information("Loaded {Count} known games from store", _games.Count);
                }
                else
                {
                    _logger.Warning("Known games file was null after deserialization");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load known games from {Path}, starting with empty list", _filePath);
            }
        }
    }

    /// <summary>
    /// Merges scanned games from launchers with existing known games list.
    /// Scanner data updates existing entries, but manual entries take precedence.
    /// Prevents duplicates by checking game IDs.
    /// </summary>
    /// <param name="scannedGames">Games detected by library scanners</param>
    public void MergeScannedGames(IEnumerable<GameInfo> scannedGames)
    {
        lock (_lock)
        {
            int newCount = 0;

            foreach (var scannedGame in scannedGames)
            {
                var existing = _games.FirstOrDefault(g => g.Id == scannedGame.Id);

                if (existing == null)
                {
                    // New game - add it
                    _games.Add(scannedGame);
                    newCount++;
                }
                else if (existing.LauncherSource != "Manual")
                {
                    // Update existing scanner entry (may have updated install path, etc.)
                    // But preserve manual entries - they take precedence
                    var index = _games.IndexOf(existing);
                    _games[index] = scannedGame;
                }
                // If existing.LauncherSource == "Manual", preserve it (don't overwrite with scanner data)
            }

            Save();
            _logger.Debug("Merged {NewCount} new games from scanners, total: {TotalCount}", newCount, _games.Count);
        }
    }

    /// <summary>
    /// Manually adds a game by executable path.
    /// Creates a GameInfo with LauncherSource="Manual".
    /// </summary>
    /// <param name="exePath">Full path to game executable</param>
    /// <returns>Created GameInfo or null if path is invalid</returns>
    public GameInfo? AddManualGame(string exePath)
    {
        lock (_lock)
        {
            // Validate path exists
            if (!File.Exists(exePath))
            {
                _logger.Warning("Cannot add manual game - file does not exist: {ExePath}", exePath);
                return null;
            }

            // Get full normalized path
            var fullPath = Path.GetFullPath(exePath);
            var gameName = Path.GetFileNameWithoutExtension(fullPath);
            var installDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

            // Generate ID for manual entry
            var gameId = GameInfo.GenerateId("Manual", gameName);

            // Check if already exists
            if (_games.Any(g => g.Id == gameId))
            {
                _logger.Warning("Manual game already exists: {GameName}", gameName);
                return _games.First(g => g.Id == gameId);
            }

            // Create GameInfo
            var gameInfo = new GameInfo
            {
                Id = gameId,
                GameName = gameName,
                ExecutablePath = fullPath,
                InstallDirectory = installDir,
                LauncherSource = "Manual",
                LauncherId = string.Empty
            };

            _games.Add(gameInfo);
            Save();

            _logger.Information("Manually added game: {GameName} from {ExePath}", gameName, exePath);
            return gameInfo;
        }
    }

    /// <summary>
    /// Removes a game from the known games list by ID.
    /// </summary>
    /// <param name="gameId">Unique game ID</param>
    /// <returns>True if game was found and removed</returns>
    public bool RemoveGame(string gameId)
    {
        lock (_lock)
        {
            var game = _games.FirstOrDefault(g => g.Id == gameId);
            if (game != null)
            {
                _games.Remove(game);
                Save();
                _logger.Information("Removed game: {GameId}", gameId);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Gets a read-only snapshot of all known games.
    /// </summary>
    /// <returns>Read-only list of known games</returns>
    public IReadOnlyList<GameInfo> GetAllGames()
    {
        lock (_lock)
        {
            return _games.AsReadOnly();
        }
    }

    /// <summary>
    /// Persists the current known games list to disk.
    /// Creates directory if it doesn't exist.
    /// Wrapped in try/catch to prevent exceptions from escaping.
    /// </summary>
    private void Save()
    {
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Serialize with indentation for readability
            var json = JsonSerializer.Serialize(_games, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save known games to {Path}", _filePath);
            // Don't throw - persistence failure shouldn't crash the app
        }
    }
}
