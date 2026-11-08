using GameShift.Core.Config;
using GameShift.Core.SystemTweaks.Tweaks;
using Serilog;

namespace GameShift.Core.SystemTweaks;

/// <summary>
/// Manages all registered system tweaks. Tracks which ones GameShift applied
/// (so it only reverts its own changes). Persists state in AppSettings.
/// </summary>
public class SystemTweaksManager
{
    private readonly List<ISystemTweak> _tweaks;

    /// <summary>
    /// Tweaks that can no longer be applied. They stay registered so the changes earlier versions
    /// recorded can still be reverted: <see cref="RevertAllTweaks"/> and Restore All resolve ledger
    /// entries by class name and cannot revert a class that is missing.
    /// </summary>
    private static readonly HashSet<string> RetiredTweaks = new(StringComparer.Ordinal)
    {
        nameof(DisableGameDvr),
        nameof(DisableMpo),
        nameof(OptimizeMmcss),
        nameof(OptimizeWin32PrioritySeparation),
        nameof(DisableMemoryIntegrity),
        nameof(DisablePowerThrottling),
        nameof(OptimizePageFile),
        nameof(DisableLastAccessTimestamp),
        nameof(DisableNtfs8dot3),
        nameof(DisableUsbSelectiveSuspend),
        nameof(OptimizeInterruptHandling),
        nameof(DisableMemoryCompression),
        nameof(EnableLargePages),
        nameof(OptimizeNtfsMemoryUsage),
        nameof(OptimizeKernelMemory),
    };

    /// <summary>All registered tweaks, including the retired ones kept for revert.</summary>
    public IReadOnlyList<ISystemTweak> Tweaks => _tweaks;

    /// <summary>The tweaks that can still be applied.</summary>
    public IReadOnlyList<ISystemTweak> OfferedTweaks => _tweaks.Where(t => !IsRetired(t)).ToList();

    public static bool IsRetired(ISystemTweak tweak) => IsRetired(tweak.GetType().Name);

    /// <summary>Whether the tweak with this class name is retired, without creating the tweak.</summary>
    public static bool IsRetired(string tweakClassName) => RetiredTweaks.Contains(tweakClassName);

    public SystemTweaksManager()
    {
        _tweaks = new List<ISystemTweak>
        {
            new Tweaks.DisableGameDvr(),
            new Tweaks.DisableHags(),
            new Tweaks.DisableMpo(),
            new Tweaks.OptimizeMmcss(),
            new Tweaks.OptimizeWin32PrioritySeparation(),
            new Tweaks.DisableMemoryIntegrity(),
            new Tweaks.DisablePowerThrottling(),
            new Tweaks.OptimizePageFile(),
            new Tweaks.DisableLastAccessTimestamp(),
            new Tweaks.DisableNtfs8dot3(),
            new Tweaks.DisableUsbSelectiveSuspend(),
            new Tweaks.OptimizeInterruptHandling(),
            new Tweaks.DisableMemoryCompression(),
            new Tweaks.EnableLargePages(),
            new Tweaks.OptimizeNtfsMemoryUsage(),
            new Tweaks.OptimizeKernelMemory()
        };
    }

    /// <summary>
    /// Gets the current status of a tweak: "Not Applied", "Applied (by GameShift)", or "Already Applied".
    /// </summary>
    public string GetTweakStatus(ISystemTweak tweak)
    {
        var settings = SettingsManager.Load();
        var tweakSettings = settings.SystemTweaks ?? new SystemTweaksSettings();
        var className = tweak.GetType().Name;

        bool isCurrentlyApplied = tweak.DetectIsApplied();

        if (tweakSettings.AppliedTweaks.TryGetValue(className, out var state) && state.IsAppliedByGameShift)
        {
            return isCurrentlyApplied ? "Applied (by GameShift)" : "Not Applied";
        }

        return isCurrentlyApplied ? "Already Applied" : "Not Applied";
    }

    /// <summary>
    /// Applies a tweak and persists the state.
    /// </summary>
    public bool ApplyTweak(ISystemTweak tweak)
    {
        var className = tweak.GetType().Name;

        if (IsRetired(tweak))
        {
            Log.Warning("[SystemTweaks] {Tweak} can no longer be applied; it can only be reverted", className);
            return false;
        }

        if (tweak.DetectIsApplied())
        {
            // Already applied - record as externally applied
            var settings = SettingsManager.Load();
            settings.SystemTweaks ??= new SystemTweaksSettings();
            settings.SystemTweaks.AppliedTweaks[className] = new TweakState
            {
                IsAppliedByGameShift = false,
                OriginalValues = null,
                AppliedAt = DateTime.UtcNow
            };
            SettingsManager.Save(settings);
            SettingsManager.Logger.Information("[SystemTweaks] {Tweak} already applied externally", className);
            return true;
        }

        try
        {
            var originalValues = tweak.Apply();

            var settings = SettingsManager.Load();
            settings.SystemTweaks ??= new SystemTweaksSettings();
            settings.SystemTweaks.AppliedTweaks[className] = new TweakState
            {
                IsAppliedByGameShift = true,
                OriginalValues = originalValues,
                AppliedAt = DateTime.UtcNow
            };
            SettingsManager.Save(settings);

            SettingsManager.Logger.Information("[SystemTweaks] Applied {Tweak}", className);
            return true;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[SystemTweaks] Failed to apply {Tweak}", className);
            return false;
        }
    }

    /// <summary>
    /// Reverts a tweak that GameShift applied.
    /// </summary>
    public bool RevertTweak(ISystemTweak tweak)
    {
        var className = tweak.GetType().Name;
        var settings = SettingsManager.Load();
        var tweakSettings = settings.SystemTweaks ?? new SystemTweaksSettings();

        if (!tweakSettings.AppliedTweaks.TryGetValue(className, out var state))
        {
            SettingsManager.Logger.Warning("[SystemTweaks] No state found for {Tweak}, cannot revert", className);
            return false;
        }

        if (!state.IsAppliedByGameShift)
        {
            // Not applied by GameShift - just remove tracking
            tweakSettings.AppliedTweaks.Remove(className);
            SettingsManager.Save(settings);
            SettingsManager.Logger.Information("[SystemTweaks] {Tweak} was not applied by GameShift, removing tracking only", className);
            return true;
        }

        try
        {
            bool success = tweak.Revert(state.OriginalValues);
            if (success)
            {
                tweakSettings.AppliedTweaks.Remove(className);
                SettingsManager.Save(settings);
                SettingsManager.Logger.Information("[SystemTweaks] Reverted {Tweak}", className);
            }
            return success;
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[SystemTweaks] Failed to revert {Tweak}", className);
            return false;
        }
    }

    /// <summary>
    /// Reverts every tweak GameShift applied, restoring each from its captured original values.
    /// Gives users a single "restore everything" action so persistent tweaks are not left behind
    /// when they were never individually toggled back. Returns the count successfully reverted.
    /// </summary>
    public int RevertAllTweaks()
    {
        var settings = SettingsManager.Load();
        var applied = settings.SystemTweaks?.AppliedTweaks;
        if (applied == null || applied.Count == 0) return 0;

        // Snapshot the class names first: RevertTweak mutates the AppliedTweaks dictionary.
        var classNames = applied
            .Where(kv => kv.Value.IsAppliedByGameShift)
            .Select(kv => kv.Key)
            .ToList();

        int count = 0;
        foreach (var className in classNames)
        {
            var tweak = _tweaks.FirstOrDefault(t => t.GetType().Name == className);
            if (tweak != null && RevertTweak(tweak)) count++;
        }
        return count;
    }

    /// <summary>
    /// Gets a tweak by its class name.
    /// </summary>
    public ISystemTweak? GetTweakByClassName(string className)
    {
        return _tweaks.FirstOrDefault(t => t.GetType().Name == className);
    }
}
