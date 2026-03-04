using System.Diagnostics;
using GameShift.Core.Config;
using GameShift.Core.Detection;

namespace GameShift.Core.GameProfiles;

/// <summary>
/// Manages per-game optimization profiles. Applies session-specific process priority,
/// affinity, and launcher demotion when a game starts; reverts when it exits.
/// Runs parallel to the IOptimization pipeline and BackgroundMode.
/// </summary>
public class GameProfileManager : IDisposable
{
    private readonly List<GameProfile> _allProfiles;
    private GameProfile? _activeProfile;

    // Session state for revert
    private readonly Dictionary<int, ProcessPriorityClass> _originalPriorities = new();
    private readonly Dictionary<int, IntPtr> _originalAffinities = new();
    private readonly List<(string ProcessName, int Pid, ProcessPriorityClass OriginalPriority)> _launcherOriginals = new();

    /// <summary>Currently active game profile, null if no game detected.</summary>
    public GameProfile? ActiveProfile => _activeProfile;

    /// <summary>Whether a profile is currently active.</summary>
    public bool HasActiveProfile => _activeProfile != null;

    /// <summary>Set of process names currently managed by an active game profile session.</summary>
    public HashSet<string> ActiveSessionProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GameProfileManager()
    {
        _allProfiles = new List<GameProfile>(BuiltInProfiles.GetAll());

        // Load custom profiles from settings
        var settings = SettingsManager.Load();
        var profileSettings = settings.GameProfiles;
        if (profileSettings?.CustomProfiles != null)
        {
            _allProfiles.AddRange(profileSettings.CustomProfiles);
        }
    }

    /// <summary>Gets all profiles (built-in + custom).</summary>
    public IReadOnlyList<GameProfile> GetAllProfiles() => _allProfiles;

    /// <summary>Gets the active profile or null.</summary>
    public GameProfile? GetActiveProfile() => _activeProfile;

    /// <summary>
    /// Finds a matching profile for a given process name.
    /// Case-insensitive comparison.
    /// </summary>
    public GameProfile? GetProfileForProcess(string executableName)
    {
        var settings = SettingsManager.Load();
        var profileSettings = settings.GameProfiles;

        foreach (var profile in _allProfiles)
        {
            if (!profile.Enabled) continue;

            // Check user override
            if (profileSettings?.ProfileOverrides.TryGetValue(profile.Id, out var over) == true && !over.Enabled)
                continue;

            foreach (var processName in profile.ProcessNames)
            {
                if (string.Equals(processName, executableName, StringComparison.OrdinalIgnoreCase))
                    return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Called when a game starts. Finds matching profile and applies session optimizations.
    /// </summary>
    public void OnGameStarted(object? sender, GameDetectedEventArgs e)
    {
        var settings = SettingsManager.Load();
        if (settings.GameProfiles?.Enabled == false) return;

        if (_activeProfile != null)
        {
            SettingsManager.Logger.Information(
                "[GameProfiles] Additional game {Game} detected, profile {Active} already active — skipping",
                e.GameName, _activeProfile.DisplayName);
            return;
        }

        var exeName = Path.GetFileName(e.ExecutablePath);
        var profile = GetProfileForProcess(exeName);
        if (profile == null)
        {
            SettingsManager.Logger.Debug("[GameProfiles] No profile matched for {Exe}", exeName);
            return;
        }

        _activeProfile = profile;
        ActiveSessionProcessNames.Clear();
        foreach (var pn in profile.ProcessNames)
            ActiveSessionProcessNames.Add(pn);

        SettingsManager.Logger.Information(
            "[GameProfiles] Activating profile '{Profile}' for {Game} (PID {Pid})",
            profile.DisplayName, e.GameName, e.ProcessId);

        // Apply user overrides
        var overrides = settings.GameProfiles?.ProfileOverrides?.GetValueOrDefault(profile.Id);

        // 1. Set game process priority
        try
        {
            var targetPriority = Enum.TryParse<ProcessPriorityClass>(overrides?.CustomPriority, true, out var customPri)
                ? customPri : profile.GamePriority;

            using var proc = Process.GetProcessById(e.ProcessId);
            _originalPriorities[e.ProcessId] = proc.PriorityClass;
            proc.PriorityClass = targetPriority;
            SettingsManager.Logger.Information(
                "[GameProfiles] Set {Exe} (PID {Pid}) priority to {Priority}",
                exeName, e.ProcessId, targetPriority);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[GameProfiles] Failed to set game priority for PID {Pid}", e.ProcessId);
        }

        // 2. Set game process affinity
        try
        {
            var affinityMask = overrides?.CustomAffinityMask ?? IntelHybridDetector.GetAffinityMask(profile);
            if (affinityMask != 0)
            {
                using var proc = Process.GetProcessById(e.ProcessId);
                _originalAffinities[e.ProcessId] = proc.ProcessorAffinity;
                proc.ProcessorAffinity = (IntPtr)affinityMask;
                SettingsManager.Logger.Information(
                    "[GameProfiles] Set {Exe} (PID {Pid}) affinity to 0x{Mask:X}",
                    exeName, e.ProcessId, affinityMask);
            }
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Warning(ex, "[GameProfiles] Failed to set game affinity for PID {Pid}", e.ProcessId);
        }

        // 3. Set launcher priorities
        if (profile.LauncherPriority != null)
        {
            foreach (var launcherName in profile.LauncherProcessNames)
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(launcherName);
                    var procs = Process.GetProcessesByName(name);
                    foreach (var proc in procs)
                    {
                        try
                        {
                            _launcherOriginals.Add((launcherName, proc.Id, proc.PriorityClass));
                            proc.PriorityClass = profile.LauncherPriority.Value;
                            SettingsManager.Logger.Debug(
                                "[GameProfiles] Set launcher {Launcher} (PID {Pid}) to {Priority}",
                                launcherName, proc.Id, profile.LauncherPriority.Value);
                        }
                        catch { }
                        finally { proc.Dispose(); }
                    }
                }
                catch { }
            }
        }

        SettingsManager.Logger.Information("[GameProfiles] Profile '{Profile}' applied", profile.DisplayName);
    }

    /// <summary>
    /// Called when all games stop. Reverts session optimizations.
    /// </summary>
    public void OnAllGamesStopped(object? sender, EventArgs e)
    {
        if (_activeProfile == null) return;

        SettingsManager.Logger.Information(
            "[GameProfiles] Deactivating profile '{Profile}'", _activeProfile.DisplayName);

        // 1. Restore game process priorities
        foreach (var (pid, originalPriority) in _originalPriorities)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.PriorityClass = originalPriority;
            }
            catch { } // Process may have exited
        }
        _originalPriorities.Clear();

        // 2. Restore game process affinities
        foreach (var (pid, originalAffinity) in _originalAffinities)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.ProcessorAffinity = originalAffinity;
            }
            catch { }
        }
        _originalAffinities.Clear();

        // 3. Restore launcher priorities
        foreach (var (name, pid, originalPriority) in _launcherOriginals)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.PriorityClass = originalPriority;
            }
            catch { }
        }
        _launcherOriginals.Clear();

        ActiveSessionProcessNames.Clear();

        SettingsManager.Logger.Information("[GameProfiles] Profile '{Profile}' reverted", _activeProfile.DisplayName);
        _activeProfile = null;
    }

    public void Dispose()
    {
        // If a profile is active during shutdown, try to revert
        if (_activeProfile != null)
        {
            OnAllGamesStopped(null, EventArgs.Empty);
        }
    }
}
