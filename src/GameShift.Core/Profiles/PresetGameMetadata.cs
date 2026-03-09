using GameShift.Core.Optimization;

namespace GameShift.Core.Profiles;

/// <summary>
/// Metadata about a preset game for UI display (anticheat badge, VBS safety info).
/// </summary>
public class PresetGameMetadata
{
    /// <summary>Display name for UI.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Anti-cheat system name (e.g., "Riot Vanguard", "VAC"). Empty if none.</summary>
    public string AntiCheatName { get; init; } = "";

    /// <summary>Structured anti-cheat type for optimization strategy decisions.</summary>
    public AntiCheatType AntiCheat { get; init; } = AntiCheatType.None;

    /// <summary>Whether this game uses an anti-cheat system.</summary>
    public bool HasAntiCheat => !string.IsNullOrEmpty(AntiCheatName);

    /// <summary>
    /// Whether VBS/HVCI is safe to disable for this game.
    /// False = NEVER disable VBS (e.g., Riot Vanguard requires HVCI).
    /// </summary>
    public bool VbsSafeToDisable { get; init; } = true;

    /// <summary>Why VBS cannot be disabled for this game (shown in UI warning).</summary>
    public string VbsSafetyReason { get; init; } = "";
}
