using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using GameShift.Tests.TestHelpers;

namespace GameShift.Tests.Optimization;

/// <summary>
/// Win32PrioritySeparation is system-wide and persistent. Once the booster changed it, a failure
/// later in Apply must still leave the optimization tracked, so the session's revert restores it.
/// </summary>
public class ProcessPriorityBoosterTests
{
    private const int GamePid = 7300;
    private const int OriginalSeparation = 0x26;
    private const int GamingSeparation = 0x2A;

    private readonly FakePrioritySeparation _separation = new() { Value = OriginalSeparation };
    private readonly FakeProcessPriorities _processes = new();

    private ProcessPriorityBooster NewBooster()
    {
        var booster = new ProcessPriorityBooster { Processes = _processes, PrioritySeparation = _separation };
        var profile = new GameProfile
        {
            Id = "game",
            GameName = "Game",
            ExecutableName = "Game.exe",
            ProcessId = GamePid,
        };
        booster.CanApply(new SystemContext { Profile = profile, Snapshot = new SystemStateSnapshot() });
        return booster;
    }

    [Fact]
    public void BoostDeniedAfterPrioritySeparationChanged_StaysAppliedAndRevertRestoresIt()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        _processes.GetPriorityFailure = new Win32Exception(5); // access denied by an anti-cheat
        var booster = NewBooster();

        var applied = booster.Apply();
        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(GamingSeparation, _separation.Value);

        var reverted = booster.Revert();
        Assert.Equal(OptimizationState.Reverted, reverted.State);
        Assert.Equal(OriginalSeparation, _separation.Value);
    }

    [Fact]
    public void UnexpectedFailureAfterPrioritySeparationChanged_StaysAppliedAndRevertRestoresIt()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        _processes.GetNameFailure = new NotSupportedException("unexpected");
        var booster = NewBooster();

        var applied = booster.Apply();
        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.True(booster.IsApplied);

        booster.Revert();
        Assert.Equal(OriginalSeparation, _separation.Value);
    }

    [Fact]
    public void PrioritySeparationRestoreFails_RevertReportsFailedAndKeepsTheOriginal()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        var booster = NewBooster();
        Assert.Equal(OptimizationState.Applied, booster.Apply().State);

        _separation.WriteFailure = new UnauthorizedAccessException("denied");
        Assert.Equal(OptimizationState.Failed, booster.Revert().State);
        Assert.Equal(GamingSeparation, _separation.Value);

        // The captured original survives the failed restore, so a retry can still put it back.
        _separation.WriteFailure = null;
        booster.Revert();
        Assert.Equal(OriginalSeparation, _separation.Value);
    }

    [Fact]
    public void GamePidReusedByAnotherProcess_IsNotGivenTheGamesOldPriority()
    {
        _processes.Add(GamePid, "Game", ProcessPriorityClass.Normal);
        var booster = NewBooster();
        Assert.Equal(OptimizationState.Applied, booster.Apply().State);
        Assert.Equal(ProcessPriorityClass.High, _processes.PriorityOf(GamePid));

        _processes.Add(GamePid, "OtherApp", ProcessPriorityClass.AboveNormal); // game exited, PID reused
        Assert.Equal(OptimizationState.Reverted, booster.Revert().State);

        Assert.Equal(ProcessPriorityClass.AboveNormal, _processes.PriorityOf(GamePid));
        Assert.Equal(OriginalSeparation, _separation.Value);
    }

    [Fact]
    public void FailureWithNothingChanged_ReportsFailed()
    {
        _separation.Value = GamingSeparation; // already at the gaming value, nothing to change
        _processes.GetNameFailure = new NotSupportedException("unexpected");
        var booster = NewBooster();

        var applied = booster.Apply();

        Assert.Equal(OptimizationState.Failed, applied.State);
        Assert.False(booster.IsApplied);
        Assert.Equal(GamingSeparation, _separation.Value);
    }

    private sealed class FakePrioritySeparation : ProcessPriorityBooster.IPrioritySeparationStore
    {
        public int? Value { get; set; }

        public Exception? WriteFailure { get; set; }

        public bool TryRead(out int? value)
        {
            value = Value;
            return true;
        }

        public void Write(int value) => Value = WriteFailure is null ? value : throw WriteFailure;

        public void Delete() => Value = null;
    }
}
