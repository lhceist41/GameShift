using GameShift.Core.Optimization;
using GameShift.Core.Profiles;

namespace GameShift.Core.Presentation;

/// <summary>A session module's plain title and one-line effect, looked up by its id.</summary>
public sealed record SessionModuleText(string Title, string Effect)
{
    private static readonly Dictionary<string, SessionModuleText> Known = new()
    {
        [ServiceSuppressor.OptimizationId] = new(
            "Pause background services",
            "Pauses Windows Update, Delivery Optimization, background transfers and search indexing while you play."),
        [PowerPlanSwitcher.OptimizationId] = new(
            "Use a high performance power plan",
            "Switches from Balanced to the High Performance power plan while you play."),
        [TimerResolutionManager.OptimizationId] = new(
            "Use a finer system timer",
            "Asks Windows for a 1 ms timer resolution to lower input latency."),
        [ProcessPriorityBooster.OptimizationId] = new(
            "Raise the game's priority",
            "Sets the game's process priority to High."),
        [HybridCpuDetector.OptimizationId] = new(
            "Pin the game to its best cores",
            "Keeps the game on the performance cores (P-cores) or the V-Cache CCD."),
        [EfficiencyModeController.OptimizationId] = new(
            "Put background apps in efficiency mode",
            "Applies Windows Efficiency Mode to background apps, which keeps them on E-cores on hybrid CPUs."),
        [CpuSchedulingOptimizer.OptimizationId] = new(
            "Keep the game at full speed",
            "Keeps the game at full speed while background apps are in efficiency mode, and keeps background apps off a pinned game's cores."),
    };

    /// <param name="description">The module's own description, used for a module without a plain title.</param>
    public static SessionModuleText For(string moduleName, string description) =>
        Known.TryGetValue(moduleName, out var text) ? text : new(moduleName, description);
}

/// <summary>The default profile's switch for each session module, matched by module id.</summary>
public static class ModuleSwitch
{
    /// <summary>Shown instead of a switch on the module that follows two other switches.</summary>
    public const string FollowsOtherSwitchesText = "Follows efficiency mode and core pinning";

    public static bool HasSwitch(string moduleName) => moduleName switch
    {
        ServiceSuppressor.OptimizationId or PowerPlanSwitcher.OptimizationId or
        TimerResolutionManager.OptimizationId or ProcessPriorityBooster.OptimizationId or
        HybridCpuDetector.OptimizationId or EfficiencyModeController.OptimizationId => true,
        _ => false,
    };

    /// <summary>Sets the module's switch in <paramref name="profile"/>; false when it has none.</summary>
    public static bool TrySet(GameProfile profile, string moduleName, bool enabled)
    {
        switch (moduleName)
        {
            case ServiceSuppressor.OptimizationId: profile.SuppressServices = enabled; return true;
            case PowerPlanSwitcher.OptimizationId: profile.SwitchPowerPlan = enabled; return true;
            case TimerResolutionManager.OptimizationId: profile.SetTimerResolution = enabled; return true;
            case ProcessPriorityBooster.OptimizationId: profile.BoostProcessPriority = enabled; return true;
            case HybridCpuDetector.OptimizationId: profile.PinGameCores = enabled; return true;
            case EfficiencyModeController.OptimizationId: profile.EnableEfficiencyMode = enabled; return true;
            default: return false;
        }
    }
}

public readonly record struct SessionModuleInput(string ModuleName, bool IsAvailable, bool EnabledInProfile);

/// <param name="ByModule">Each module's badge, keyed by module id.</param>
/// <param name="SessionRunning">A game session was requested when read: read again soon, and once more after it ends.</param>
public sealed record SessionModuleSnapshot(IReadOnlyDictionary<string, ModuleStatus> ByModule, bool SessionRunning);

public static class SessionModuleStates
{
    /// <summary>
    /// Every module's badge for the session running now. The engine raises its last events before
    /// the orchestrator clears the session marker, so a page polls while
    /// <see cref="SessionModuleSnapshot.SessionRunning"/> is true and refreshes once more after.
    /// </summary>
    /// <param name="sessionRequestedUtc">
    /// Read before <paramref name="lastOutcome"/>: a session that ends between the two reads then
    /// still counts as running, so the next poll corrects the badges instead of none coming.
    /// </param>
    public static SessionModuleSnapshot Read(
        IReadOnlyList<SessionModuleInput> modules,
        Func<DateTime?> sessionRequestedUtc,
        Func<SessionOutcome?> lastOutcome,
        bool isPaused)
    {
        var requested = sessionRequestedUtc();
        var current = CurrentSession.Select(lastOutcome(), requested);
        var byModule = new Dictionary<string, ModuleStatus>();
        foreach (var m in modules)
            byModule[m.ModuleName] = ModuleStatus.For(m.ModuleName, m.IsAvailable, m.EnabledInProfile, isPaused, current);
        return new(byModule, requested != null);
    }
}
