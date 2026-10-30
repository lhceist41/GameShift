using GameShift.Core.Journal;
using GameShift.Core.Optimization;

namespace GameShift.Tests.Optimization;

/// <summary>
/// A session journal written by an earlier version replays the recorded RSC state through netsh,
/// and only a state netsh accepts. Runs against an in-memory netsh.
/// </summary>
public class NetworkOptimizerRscTests
{
    private const string SetRsc = "int tcp set global rsc=";

    private readonly List<string> _calls = new();

    private NetworkOptimizer NewOptimizer() => new()
    {
        Netsh = arguments =>
        {
            _calls.Add(arguments);
            return (0, string.Empty);
        },
    };

    [Theory]
    [InlineData("enabled")]
    [InlineData("disabled")]
    public void RevertFromRecord_ReplaysTheRecordedState(string state)
    {
        var result = NewOptimizer().RevertFromRecord("{\"Netsh:rsc\":\"" + state + "\"}");

        Assert.Equal(OptimizationState.Reverted, result.State);
        Assert.Equal(new[] { SetRsc + state }, _calls);
    }

    [Theory]
    [InlineData("\"aktiviert\"")]
    [InlineData("null")]
    [InlineData("1")]
    public void RevertFromRecord_NeverPassesAnUnknownStateToNetsh(string recorded)
    {
        NewOptimizer().RevertFromRecord("{\"Netsh:rsc\":" + recorded + "}");

        Assert.Empty(_calls);
    }
}
