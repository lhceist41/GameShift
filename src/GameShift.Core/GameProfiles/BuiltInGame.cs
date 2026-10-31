using GameShift.Core.Optimization;
using GameShift.Core.Profiles.GameActions;

namespace GameShift.Core.GameProfiles;

/// <summary>
/// What GameShift knows about one built-in game: how to recognise it, its anti-cheat, and what to
/// show about it. Entries live in <see cref="BuiltInProfiles"/>.
/// </summary>
public sealed class BuiltInGame
{
    /// <summary>
    /// Stable built-in ID (e.g., "overwatch2"). Known-game records and saved settings are keyed by
    /// the ID generated from it, so it never changes.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>Display name (e.g., "Overwatch 2"). Not an identity.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Process names to match (e.g., ["Overwatch.exe"]).</summary>
    public string[] ProcessNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Opt-in for the fail-closed name-only detection fallback: when a process is proven alive but
    /// its image path cannot be read, this game may be matched on process name alone.
    /// Default false, and deliberately narrow - a shared or generic process name here would let an
    /// unrelated program be optimized as a game.
    /// </summary>
    public bool AllowNameOnlyFallback { get; init; }

    /// <summary>Anti-cheat system used by this game (for IFEO fallback and VBS gating).</summary>
    public AntiCheatType AntiCheat { get; init; } = AntiCheatType.None;

    /// <summary>System tweak class names recommended for this game, shown in the UI.</summary>
    public string[] RecommendedTweaks { get; init; } = Array.Empty<string>();

    /// <summary>Game-specific notes shown in the UI.</summary>
    public string[] Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set for the games that get a ready-made profile on first detection and one-time tips.
    /// Null for every other game.
    /// </summary>
    public BuiltInPreset? Preset { get; init; }
}

/// <summary>
/// The preset part of a <see cref="BuiltInGame"/>: the executable that selects it, the profile it
/// creates, its anti-cheat badge and VBS safety note, its tips, and the firewall rules earlier
/// versions created for it.
/// </summary>
public sealed class BuiltInPreset
{
    /// <summary>
    /// The one executable name that selects the preset. Other process names of the same game
    /// (for example Deadlock's "deadlock.exe") detect the game but do not select the preset.
    /// </summary>
    public string ExecutableName { get; init; } = "";

    /// <summary>Anti-cheat system name for the badge (e.g., "Riot Vanguard", "VAC"). Empty if none.</summary>
    public string AntiCheatName { get; init; } = "";

    /// <summary>Whether this game uses an anti-cheat system.</summary>
    public bool HasAntiCheat => !string.IsNullOrEmpty(AntiCheatName);

    /// <summary>
    /// Whether VBS/HVCI is safe to disable for this game.
    /// False = NEVER disable VBS (e.g., Riot Vanguard requires HVCI).
    /// </summary>
    public bool VbsSafeToDisable { get; init; } = true;

    /// <summary>Why VBS cannot be disabled for this game (shown in UI warning).</summary>
    public string VbsSafetyReason { get; init; } = "";

    /// <summary>Value of <c>GameProfile.BoostProcessPriority</c> in the profile the preset creates.</summary>
    public bool BoostProcessPriority { get; init; } = true;

    /// <summary>Tips shown once each while the game runs.</summary>
    public IReadOnlyList<OneTimeTipAction> Tips { get; init; } = Array.Empty<OneTimeTipAction>();

    /// <summary>
    /// Firewall rules earlier versions created for this game. GameShift no longer creates them;
    /// startup removes any that were left behind, matched on all three values, so none may change.
    /// </summary>
    public IReadOnlyList<FirewallLeftoverRule> FirewallLeftovers { get; init; } = Array.Empty<FirewallLeftoverRule>();
}

/// <summary>A firewall allow rule an earlier GameShift version created: display name, direction, program.</summary>
public sealed record FirewallLeftoverRule(string RuleName, string Direction, string Program);
