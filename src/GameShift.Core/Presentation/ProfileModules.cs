using GameShift.Core.Optimization;

namespace GameShift.Core.Presentation;

/// <summary>A profile switch for one session module, with the same title and effect as During games.</summary>
/// <param name="Note">What limits the switch, or empty.</param>
public sealed record ProfileModuleRow(string ModuleName, string Title, string Effect, string Note, bool IsAdvanced);

/// <summary>The profile editor's switches, matched to the profile by module id.</summary>
public static class ProfileModules
{
    public static IReadOnlyList<ProfileModuleRow> Rows { get; } =
    [
        Row(ServiceSuppressor.OptimizationId, "", isAdvanced: false),
        Row(PowerPlanSwitcher.OptimizationId, "Not on a Ryzen X3D CPU with two CCDs.", isAdvanced: false),
        Row(ProcessPriorityBooster.OptimizationId, "", isAdvanced: false),
        Row(EfficiencyModeController.OptimizationId, "", isAdvanced: false),
        Row(TimerResolutionManager.OptimizationId, "Off by default.", isAdvanced: true),
        Row(HybridCpuDetector.OptimizationId, "For hybrid CPUs and Ryzen X3D CPUs with two CCDs. Off by default.", isAdvanced: true),
    ];

    private static ProfileModuleRow Row(string moduleName, string note, bool isAdvanced)
    {
        var text = SessionModuleText.For(moduleName, "");
        return new(moduleName, text.Title, text.Effect, note, isAdvanced);
    }
}
