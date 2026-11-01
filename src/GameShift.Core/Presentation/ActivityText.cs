namespace GameShift.Core.Presentation;

/// <summary>What an activity entry is about, for its icon and colour.</summary>
public enum ActivityKind
{
    Other,
    Game,
    Session,
    Applied,
    Failed,
    Reverted,
}

/// <summary>A plain label and kind for each activity entry type, so the page never shows the type's code name.</summary>
public static class ActivityText
{
    public static ActivityKind Kind(string type) => type switch
    {
        "GameStarted" or "GameStopped" => ActivityKind.Game,
        "SessionEnded" => ActivityKind.Session,
        "OptimizationApplied" => ActivityKind.Applied,
        "OptimizationFailed" => ActivityKind.Failed,
        "OptimizationReverted" => ActivityKind.Reverted,
        _ => ActivityKind.Other,
    };

    public static string Label(string type) => type switch
    {
        "GameStarted" => "Game started",
        "GameStopped" => "Game closed",
        "SessionEnded" => "Session ended",
        "OptimizationApplied" => "Change applied",
        "OptimizationFailed" => "Change failed",
        "OptimizationReverted" => "Change undone",
        _ => "Event",
    };
}
