using GameShift.Core.Optimization;
using GameShift.Core.Presentation;
using Xunit;
using static GameShift.Tests.Presentation.OutcomeFixtures;

namespace GameShift.Tests.Presentation;

/// <summary>
/// The end-of-session toast shows the deactivation of the game session's own activation, never one
/// from a Quick Optimize run that happened during the game.
/// </summary>
public class SessionToastPairingTests
{
    private static readonly DateTime K1 = Start;
    private static readonly DateTime K2 = Start.AddMinutes(20);

    private static SessionOutcome Activated(DateTime key) =>
        new(key, "Test Game", [Module("A", ModuleOutcomeKind.Applied)], [], EndedUtc: null, RevertedByRecovery: false);

    private static SessionOutcome Deactivated(DateTime key, ModuleRevertKind kind = ModuleRevertKind.Reverted) =>
        new(key, "Test Game", [Module("A", ModuleOutcomeKind.Applied)], [Revert("A", kind)],
            key.AddMinutes(5), RevertedByRecovery: false);

    [Fact]
    public void QuickOptimizeDuringTheGame_DoesNotReplaceTheGameSessionsOwnResult()
    {
        var pairing = new SessionToastPairing<string>();
        var gameRevert = Deactivated(K1, ModuleRevertKind.RevertFailed);

        pairing.OnGameStarted();
        pairing.OnActivation(Activated(K1), isGameSession: true);
        Assert.Null(pairing.OnDeactivation(gameRevert));
        pairing.OnActivation(Activated(K2), isGameSession: false);
        Assert.Null(pairing.OnDeactivation(Deactivated(K2)));

        var ready = pairing.OnAllGamesStopped("game", isGameSession: false);

        Assert.NotNull(ready);
        Assert.Same(gameRevert, ready.Deactivation);
        Assert.True(SessionSummary.For(ready.Deactivation)!.KeepOpen);
    }

    [Fact]
    public void OnlyAQuickOptimizeDeactivation_GivesNoToast()
    {
        var pairing = new SessionToastPairing<string>();

        pairing.OnGameStarted();
        pairing.OnActivation(Activated(K2), isGameSession: false);
        Assert.Null(pairing.OnDeactivation(Deactivated(K2)));

        Assert.Null(pairing.OnAllGamesStopped("game", isGameSession: false));
    }

    [Fact]
    public void DeactivationAfterTheGameExits_ShowsTheToastOnce()
    {
        var pairing = new SessionToastPairing<string>();
        var revert = Deactivated(K1);

        pairing.OnGameStarted();
        pairing.OnActivation(Activated(K1), isGameSession: true);
        Assert.Null(pairing.OnAllGamesStopped("game", isGameSession: true));

        var ready = pairing.OnDeactivation(revert);

        Assert.NotNull(ready);
        Assert.Equal("game", ready.Session);
        Assert.Same(revert, ready.Deactivation);
        Assert.Null(pairing.OnDeactivation(revert));
    }

    [Fact]
    public void DeactivationBeforeTheGameExits_ShowsTheToastOnce()
    {
        var pairing = new SessionToastPairing<string>();
        var revert = Deactivated(K1);

        pairing.OnGameStarted();
        pairing.OnActivation(Activated(K1), isGameSession: true);
        Assert.Null(pairing.OnDeactivation(revert));

        var ready = pairing.OnAllGamesStopped("game", isGameSession: false);

        Assert.NotNull(ready);
        Assert.Same(revert, ready.Deactivation);
        Assert.Null(pairing.OnDeactivation(revert));
        Assert.Null(pairing.OnAllGamesStopped("game", isGameSession: false));
    }

    [Fact]
    public void ResumeAfterPause_ReplacesTheKey()
    {
        var pairing = new SessionToastPairing<string>();
        var resumed = Deactivated(K2);

        pairing.OnGameStarted();
        pairing.OnActivation(Activated(K1), isGameSession: true);
        Assert.Null(pairing.OnDeactivation(Deactivated(K1, ModuleRevertKind.RevertFailed)));
        pairing.OnActivation(Activated(K2), isGameSession: true);

        Assert.Null(pairing.OnAllGamesStopped("game", isGameSession: true));
        Assert.Null(pairing.OnDeactivation(Deactivated(K1)));
        var ready = pairing.OnDeactivation(resumed);

        Assert.NotNull(ready);
        Assert.Same(resumed, ready.Deactivation);
    }

    [Fact]
    public void AToastWaitingWithoutAKey_IsDroppedAtTheNextGameStart()
    {
        var pairing = new SessionToastPairing<string>();

        pairing.OnGameStarted();
        Assert.Null(pairing.OnAllGamesStopped("first game", isGameSession: true));
        pairing.OnGameStarted();
        pairing.OnActivation(Activated(K2), isGameSession: true);

        Assert.Null(pairing.OnDeactivation(Deactivated(K2)));
        Assert.Equal("second game", pairing.OnAllGamesStopped("second game", isGameSession: false)!.Session);
    }
}
