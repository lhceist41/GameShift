using GameShift.Core.Optimization;

namespace GameShift.Tests.Presentation;

/// <summary>Session outcomes as the engine builds them, for the presentation mappers.</summary>
internal static class OutcomeFixtures
{
    /// <summary>20:14 UTC, shown as 21:14 in <see cref="UtcPlusOne"/>.</summary>
    public static readonly DateTime Start = new(2026, 11, 1, 20, 14, 0, DateTimeKind.Utc);

    public static readonly TimeZoneInfo UtcPlusOne =
        TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    public static ModuleOutcome Module(string name, ModuleOutcomeKind kind, string? reason = null) =>
        new(name, kind, reason, Start);

    public static ModuleRevert Revert(string name, ModuleRevertKind kind, string? reason = null) =>
        new(name, kind, reason, Start.AddHours(1));

    public static SessionOutcome Activation(params ModuleOutcome[] modules) =>
        new(Start, "Test Game", modules, Array.Empty<ModuleRevert>(), EndedUtc: null, RevertedByRecovery: false);

    public static SessionOutcome Deactivation(ModuleRevert[] reverts, bool recovered = false, params ModuleOutcome[] modules) =>
        new(Start, "Test Game", modules, reverts, Start.AddHours(1), recovered);
}
