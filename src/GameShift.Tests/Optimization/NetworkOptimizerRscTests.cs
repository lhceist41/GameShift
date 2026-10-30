using GameShift.Core.Optimization;

namespace GameShift.Tests.Optimization;

/// <summary>
/// RSC is restored from the state captured before disabling it, so it may only be disabled when
/// that state was read. Runs against an in-memory netsh.
/// </summary>
public class NetworkOptimizerRscTests
{
    private const string SetRsc = "int tcp set global rsc=";

    private readonly List<string> _calls = new();

    private NetworkOptimizer NewOptimizer(int queryExitCode, string queryOutput) => new()
    {
        Netsh = arguments =>
        {
            _calls.Add(arguments);
            return arguments == "int tcp show global" ? (queryExitCode, queryOutput) : (0, string.Empty);
        },
    };

    [Fact]
    public void StateReadable_DisablesAndRestoresIt()
    {
        var optimizer = NewOptimizer(0, "TCP Global Parameters\n----\nReceive Segment Coalescing State    : enabled\n");

        optimizer.DisableRscGlobal();
        optimizer.RestoreRscGlobal();

        Assert.Equal(new[] { SetRsc + "disabled", SetRsc + "enabled" }, _calls.Where(c => c.StartsWith(SetRsc)));
    }

    [Theory]
    [InlineData(0, "Globale TCP-Parameter\n----\nStatus der Empfangssegmentzusammenfuegung : aktiviert\n")]
    [InlineData(1, "")]
    public void StateNotReadable_LeavesRscUnchanged(int queryExitCode, string queryOutput)
    {
        var optimizer = NewOptimizer(queryExitCode, queryOutput);

        optimizer.DisableRscGlobal();
        optimizer.RestoreRscGlobal();

        Assert.DoesNotContain(_calls, c => c.StartsWith(SetRsc));
    }
}
