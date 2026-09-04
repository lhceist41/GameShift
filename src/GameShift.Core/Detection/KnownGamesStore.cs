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
    private readonly HashSet<string> _ignoredGameIds;
    private readonly string _filePath;
    private readonly string _ignoredGamesFilePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new KnownGamesStore instance.
    /// </summary>
    public KnownGamesStore()
    {
        _games = new List<GameInfo>();
        _ignoredGameIds = new HashSet<string>(StringComparer.Ordinal);
        _filePath = Path.Combine(SettingsManager.GetAppDataPath(), "known_games.json");
        _ignoredGamesFilePath = Path.Combine(SettingsManager.GetAppDataPath(), "ignored_games.json");
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
                }
                else
                {
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
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load known games from {Path}, starting with empty list", _filePath);
            }

            LoadIgnoredGames();
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
                if (_ignoredGameIds.Contains(scannedGame.Id))
                {
                    _logger.Debug("Skipping ignored scanned game: {GameId}", scannedGame.Id);
                    continue;
                }

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

            TrySaveGames(_games);
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
            TrySaveGames(_games);

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
            if (game == null)
            {
                return false;
            }

            var updatedGames = _games.ToList();
            updatedGames.Remove(game);

            HashSet<string>? updatedIgnoredGameIds = null;
            if (!string.Equals(game.LauncherSource, "Manual", StringComparison.OrdinalIgnoreCase))
            {
                updatedIgnoredGameIds = new HashSet<string>(_ignoredGameIds, StringComparer.Ordinal)
                {
                    game.Id
                };
            }

            if (!TrySaveGames(updatedGames))
            {
                return false;
            }

            if (updatedIgnoredGameIds != null && !TrySaveIgnoredGames(updatedIgnoredGameIds))
            {
                if (!TrySaveGames(_games))
                {
                    _logger.Error(
                        "Failed to roll back known games after ignore persistence failed for {GameId}",
                        gameId);
                }

                return false;
            }

            _games.Clear();
            _games.AddRange(updatedGames);

            if (updatedIgnoredGameIds != null)
            {
                _ignoredGameIds.Clear();
                _ignoredGameIds.UnionWith(updatedIgnoredGameIds);
            }

            _logger.Information("Removed game: {GameId}", gameId);
            return true;
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
            return _games.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Persists the current known games list to disk.
    /// Creates directory if it doesn't exist.
    /// Wrapped in try/catch to prevent exceptions from escaping.
    /// </summary>
    private bool TrySaveGames(List<GameInfo> games)
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
            var json = JsonSerializer.Serialize(games, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save known games to {Path}", _filePath);
            return false;
        }
    }

    private void LoadIgnoredGames()
    {
        try
        {
            if (!File.Exists(_ignoredGamesFilePath))
            {
                _logger.Debug("Ignored games file not found, keeping current ignore state");
                return;
            }

            var json = File.ReadAllText(_ignoredGamesFilePath);
            var ignoredGameIds = JsonSerializer.Deserialize<List<string>>(json);

            if (ignoredGameIds == null)
            {
                _logger.Warning(
                    "Ignored games file was null after deserialization; keeping current ignore state");
                return;
            }

            var loadedGameIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gameId in ignoredGameIds)
            {
                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    loadedGameIds.Add(gameId);
                }
            }

            _ignoredGameIds.Clear();
            _ignoredGameIds.UnionWith(loadedGameIds);
            _logger.Information("Loaded {Count} ignored game IDs from store", _ignoredGameIds.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "Failed to load ignored games from {Path}; keeping current ignore state",
                _ignoredGamesFilePath);
        }
    }

    private bool TrySaveIgnoredGames(HashSet<string> ignoredGameIds)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                ignoredGameIds.OrderBy(gameId => gameId).ToList(),
                new JsonSerializerOptions { WriteIndented = true });

            return TryWriteFileAtomically(_ignoredGamesFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save ignored games to {Path}", _ignoredGamesFilePath);
            return false;
        }
    }

    private bool TryWriteFileAtomically(string destinationPath, string contents)
    {
        string? temporaryFilePath = null;

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temporaryFilePath = Path.Combine(
                directory ?? string.Empty,
                $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(temporaryFilePath, contents);

            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryFilePath, destinationPath, null);
            }
            else
            {
                File.Move(temporaryFilePath, destinationPath);
            }

            temporaryFilePath = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to atomically write file to {Path}", destinationPath);
            return false;
        }
        finally
        {
            if (temporaryFilePath != null && File.Exists(temporaryFilePath))
            {
                try
                {
                    File.Delete(temporaryFilePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to clean up temporary file {Path}", temporaryFilePath);
                }
            }
        }
    }
}
