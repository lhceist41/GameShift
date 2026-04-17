using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Optimization;

namespace GameShift.Core.SystemTweaks;

/// <summary>
/// Manages all registered system tweaks. Tracks which ones GameShift applied
/// (so it only reverts its own changes). Persists state in AppSettings.
/// </summary>
public class SystemTweaksManager
{
    private readonly List<ISystemTweak> _tweaks;

    /// <summary>All registered tweaks.</summary>
    public IReadOnlyList<ISystemTweak> Tweaks => _tweaks;

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

        // Hard-block DisableMemoryIntegrity when VBS-requiring anti-cheat is installed
        if (tweak is Tweaks.DisableMemoryIntegrity && AntiCheatDetector.IsVbsRequiredByAntiCheat())
        {
            var blockers = string.Join(", ",
                AntiCheatDetector.GetVbsRequiringAntiCheats().Select(ac => ac.DisplayName));
            SettingsManager.Logger.Warning(
                "[SystemTweaks] DisableMemoryIntegrity BLOCKED — {AntiCheats} require VBS/HVCI enabled",
                blockers);
            return false;
        }

        if (tweak.DetectIsApplied())
        {
            // Already applied — record as externally applied
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
            // Not applied by GameShift — just remove tracking
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
    /// Applies all recommended tweaks.
    /// Exclusions:
    ///   - DisableMemoryIntegrity: security/anti-cheat gated, never auto-applied
    ///   - OptimizePageFile: requires reboot + user understanding
    ///   - OptimizeInterruptHandling: higher risk, requires reboot (opt-in only)
    ///   - DisableMemoryCompression: only for 32GB+ systems, requires reboot (opt-in only)
    ///   - EnableLargePages: requires logoff/reboot, game support varies (opt-in only)
    ///   - DisableHags: context-dependent — follows recommendation engine
    ///     (Enable/Disable/NoChange), only applies when recommendation differs from current state
    /// Returns count of successfully applied tweaks.
    /// </summary>
    public int ApplyAllRecommended()
    {
        int count = 0;
        foreach (var tweak in _tweaks)
        {
            if (tweak is Tweaks.DisableMemoryIntegrity) continue;     // Security gated
            if (tweak is Tweaks.OptimizePageFile) continue;            // Opt-in only
            if (tweak is Tweaks.OptimizeInterruptHandling) continue;   // Higher risk, opt-in only
            if (tweak is Tweaks.DisableMemoryCompression) continue;    // 32GB+ only, opt-in only
            if (tweak is Tweaks.EnableLargePages) continue;            // Opt-in only

            // HAGS: context-dependent — skip if NoChange recommendation
            if (tweak is Tweaks.DisableHags hags)
            {
                hags.EvaluateRecommendation();
                if (hags.Recommendation == GameShift.Core.SystemTweaks.Tweaks.HagsRecommendation.NoChange) continue;
            }

            if (tweak.DetectIsApplied()) continue;                     // Skip already applied

            if (ApplyTweak(tweak))
                count++;
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
