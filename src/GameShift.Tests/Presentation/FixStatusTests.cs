using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Presentation;
using Xunit;

namespace GameShift.Tests.Presentation;

/// <summary>
/// A Latency Doctor fix shows Active only once it is in effect, and never offers an undo GameShift cannot do.
/// </summary>
public class FixStatusTests
{
    private static readonly DateTime Boot = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    // The engine stores AppliedAt as local time.
    private static AppliedDpcFix Recorded(DateTime appliedUtc, bool requiresReboot = true) => new()
    {
        FixId = "msi_mode_nvidia",
        Description = "Force MSI Mode",
        ActionType = "RegistrySet",
        AppliedAt = appliedUtc.ToLocalTime(),
        RequiresReboot = requiresReboot,
    };

    private static FixAttempt Attempt(FixAttemptKind kind, string? reason = null, bool requiresRestart = false) =>
        new(kind, reason, requiresRestart, Boot.AddHours(1));

    [Fact]
    public void ARebootFix_AppliedBeforeTheLastBoot_IsActive()
    {
        var status = FixStatus.For(Recorded(Boot.AddSeconds(-1)), null, null, Boot);

        Assert.Equal(FixStatusKind.Active, status.Kind);
        Assert.Null(status.Reason);
        Assert.Equal(FixAction.Undo, status.Action);
        Assert.Equal("Active", status.Word);
    }

    [Fact]
    public void ARebootFix_AppliedAfterTheLastBoot_IsRestartPending()
    {
        var status = FixStatus.For(Recorded(Boot.AddSeconds(1)), true, null, Boot);

        Assert.Equal(FixStatusKind.RestartPending, status.Kind);
        Assert.Equal("Restart pending", status.Word);
        Assert.Equal(FixAction.Undo, status.Action);
        Assert.Contains("Restart", status.Reason);
    }

    [Fact]
    public void ARebootFix_AppliedAtTheBootInstant_IsStillRestartPending()
    {
        var recorded = Recorded(Boot);
        recorded.AppliedAt = Boot;

        Assert.Equal(FixStatusKind.RestartPending, FixStatus.For(recorded, null, null, Boot).Kind);
    }

    [Fact]
    public void AFixWithoutReboot_IsActiveAtOnce()
    {
        var status = FixStatus.For(Recorded(Boot.AddMinutes(5), requiresReboot: false), null, null, Boot);

        Assert.Equal(FixStatusKind.Active, status.Kind);
    }

    [Fact]
    public void TheBootComparison_SurvivesTheSettingsRoundTrip()
    {
        // settings.json keeps the local offset; the comparison must not shift by the UTC offset.
        var json = JsonSerializer.Serialize(Recorded(Boot.AddMinutes(1)));
        var readBack = JsonSerializer.Deserialize<AppliedDpcFix>(json)!;

        Assert.Equal(FixStatusKind.RestartPending, FixStatus.For(readBack, null, null, Boot).Kind);
        Assert.Equal(FixStatusKind.Active, FixStatus.For(readBack, null, null, Boot.AddMinutes(2)).Kind);
    }

    [Fact]
    public void AnEntryWithoutATime_CountsAsAppliedBeforeTheBoot()
    {
        var recorded = Recorded(Boot);
        recorded.AppliedAt = default;

        Assert.Equal(FixStatusKind.Active, FixStatus.For(recorded, null, null, Boot).Kind);
    }

    [Fact]
    public void AFailedApply_KeptForUndo_IsFailedWithItsReason_AndOffersTheUndo()
    {
        var attempt = Attempt(FixAttemptKind.ApplyFailed, "powercfg could not apply the setting or put the earlier value back");

        var status = FixStatus.For(Recorded(Boot.AddMinutes(5), requiresReboot: false), null, attempt, Boot);

        Assert.Equal(FixStatusKind.Failed, status.Kind);
        Assert.Equal("Failed", status.Word);
        Assert.Equal("Powercfg could not apply the setting or put the earlier value back.", status.Reason);
        Assert.Equal(FixAction.Undo, status.Action);
    }

    [Fact]
    public void AFailedApply_ThatChangedNothing_IsFailed_AndOffersAnotherTry()
    {
        var attempt = Attempt(FixAttemptKind.ApplyFailed, "The current power setting could not be read, so nothing was changed.");

        var status = FixStatus.For(null, null, attempt, Boot);

        Assert.Equal(FixStatusKind.Failed, status.Kind);
        Assert.Equal("The current power setting could not be read, so nothing was changed.", status.Reason);
        Assert.Equal(FixAction.Apply, status.Action);
    }

    // A diagnosed issue card can offer a fix that is already recorded; the engine then refuses it.
    private static FixAttempt RefusedAsAlreadyRecorded() =>
        new(FixAttemptKind.ApplyFailed, "This fix has already been applied.", false, Boot.AddHours(1), RecordedBefore: true);

    [Fact]
    public void AnApplyRefused_BecauseTheFixWasAlreadyRecorded_KeepsRestartPending()
    {
        var status = FixStatus.For(Recorded(Boot.AddSeconds(1)), null, RefusedAsAlreadyRecorded(), Boot);

        Assert.Equal(FixStatusKind.RestartPending, status.Kind);
        Assert.Equal(FixAction.Undo, status.Action);
    }

    [Fact]
    public void AnApplyRefused_BecauseTheFixWasAlreadyRecorded_KeepsActive()
    {
        var status = FixStatus.For(Recorded(Boot.AddSeconds(-1)), null, RefusedAsAlreadyRecorded(), Boot);

        Assert.Equal(FixStatusKind.Active, status.Kind);
        Assert.Null(status.Reason);
        Assert.Equal(FixAction.Undo, status.Action);
    }

    [Fact]
    public void AnApplyRefused_BecauseTheFixWasAlreadyRecorded_IsNotAFailure_OnceTheRecordIsGone()
    {
        var status = FixStatus.For(null, null, RefusedAsAlreadyRecorded(), Boot);

        Assert.Equal(FixStatusKind.NotActive, status.Kind);
        Assert.Equal(FixAction.Apply, status.Action);
    }

    [Fact]
    public void AFailedUndo_IsFailed_AndSaysTheUndoFailed()
    {
        var status = FixStatus.For(Recorded(Boot.AddSeconds(-1)), true, Attempt(FixAttemptKind.UndoFailed, "Revert failed: access denied"), Boot);

        Assert.Equal(FixStatusKind.Failed, status.Kind);
        Assert.Equal("Undo failed: Revert failed: access denied.", status.Reason);
        Assert.Equal(FixAction.Undo, status.Action);
    }

    [Fact]
    public void AFailureWithoutAMessage_StillGivesAReason()
    {
        var status = FixStatus.For(null, null, Attempt(FixAttemptKind.ApplyFailed, "  "), Boot);

        Assert.Equal("No reason was given.", status.Reason);
    }

    [Fact]
    public void NotApplied_IsNotActive_AndOffersTheApply()
    {
        var status = FixStatus.For(null, false, null, Boot);

        Assert.Equal(FixStatusKind.NotActive, status.Kind);
        Assert.Equal("Not active", status.Word);
        Assert.Null(status.Reason);
        Assert.Equal(FixAction.Apply, status.Action);
    }

    [Fact]
    public void OnInWindows_WithoutALedgerEntry_IsActive_ButOffersNoUndo()
    {
        var status = FixStatus.For(null, true, null, Boot);

        Assert.Equal(FixStatusKind.Active, status.Kind);
        Assert.Equal(FixAction.None, status.Action);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public void Recorded_ButOffInWindows_IsNotActive_AndStillOffersTheUndo()
    {
        var status = FixStatus.For(Recorded(Boot.AddSeconds(-1)), false, null, Boot);

        Assert.Equal(FixStatusKind.NotActive, status.Kind);
        Assert.Equal(FixAction.Undo, status.Action);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public void ARebootFix_UndoneInThisBoot_IsRestartPending()
    {
        var undone = new FixAttempt(FixAttemptKind.Undone, null, true, Boot.AddSeconds(1));

        var status = FixStatus.For(null, false, undone, Boot);

        Assert.Equal(FixStatusKind.RestartPending, status.Kind);
        Assert.Equal(FixAction.Apply, status.Action);
    }

    [Fact]
    public void AnUndoneFix_WithoutReboot_IsNotActive()
    {
        var undone = new FixAttempt(FixAttemptKind.Undone, null, false, Boot.AddSeconds(1));

        Assert.Equal(FixStatusKind.NotActive, FixStatus.For(null, null, undone, Boot).Kind);
    }

    [Fact]
    public void ARebootFix_UndoneBeforeTheLastBoot_IsNotActive()
    {
        var undone = new FixAttempt(FixAttemptKind.Undone, null, true, Boot.AddSeconds(-1));

        Assert.Equal(FixStatusKind.NotActive, FixStatus.For(null, null, undone, Boot).Kind);
    }
}
