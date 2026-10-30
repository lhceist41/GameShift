using System.ServiceProcess;
using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Tests.Optimization;

/// <summary>
/// A stop that times out can still leave the service and its dependents stopped. The suppressor
/// must have recorded them before the stop, so the revert restarts them and the journal lists them.
/// </summary>
public class ServiceSuppressorTests
{
    private const string Slow = "SlowSvc";
    private const string Dependent = "SlowSvcDependent";

    private readonly FakeServices _services = new();

    private ServiceSuppressor NewSuppressor()
    {
        var suppressor = new ServiceSuppressor
        {
            Services = _services,
            Targets = _ => new[] { new ServiceSuppressor.ServiceInfo(Slow, "Slow service") },
        };
        suppressor.CanApply(new SystemContext
        {
            Profile = new GameProfile { Id = "game", GameName = "Game" },
            Snapshot = new SystemStateSnapshot(),
        });
        return suppressor;
    }

    [Fact]
    public void StopTimesOut_RevertStillRestartsTheServiceAndItsDependent()
    {
        _services.Add(Slow, dependents: Dependent);
        _services.Add(Dependent);
        _services.TimesOut.Add(Slow);
        var suppressor = NewSuppressor();

        var applied = suppressor.Apply();
        Assert.Equal(OptimizationState.Applied, applied.State);
        Assert.Equal(ServiceControllerStatus.Stopped, _services.Status[Dependent]);

        suppressor.Revert();

        Assert.Equal(new[] { Slow, Dependent }, _services.Restarted);
        Assert.Equal(ServiceControllerStatus.Running, _services.Status[Slow]);
        Assert.Equal(ServiceControllerStatus.Running, _services.Status[Dependent]);
    }

    [Fact]
    public void StopTimesOut_JournalRecordStillListsTheServiceAndItsDependent()
    {
        _services.Add(Slow, dependents: Dependent);
        _services.Add(Dependent);
        _services.TimesOut.Add(Slow);

        var applied = NewSuppressor().Apply();

        var recorded = JsonDocument.Parse(applied.OriginalValue).RootElement.GetProperty("services")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { Slow, Dependent }, recorded);
    }

    [Fact]
    public void RestartFails_RevertReportsFailedAndKeepsTheServiceListed()
    {
        _services.Add(Slow, dependents: Dependent);
        _services.Add(Dependent);
        _services.WillNotStart.Add(Dependent);
        var suppressor = NewSuppressor();
        suppressor.Apply();

        var reverted = suppressor.Revert();

        Assert.Equal(OptimizationState.Failed, reverted.State);
        Assert.Contains(Dependent, reverted.ErrorMessage);
        Assert.Equal(ServiceControllerStatus.Running, _services.Status[Slow]);

        // Only the service still stopped is left for a retry.
        _services.WillNotStart.Clear();
        _services.Restarted.Clear();
        Assert.Equal(OptimizationState.Reverted, suppressor.Revert().State);
        Assert.Equal(new[] { Dependent }, _services.Restarted);
    }

    private sealed class FakeServices : ServiceSuppressor.IServiceControl
    {
        private readonly Dictionary<string, List<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ServiceControllerStatus> Status { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> TimesOut { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Restarted { get; } = new();

        public HashSet<string> WillNotStart { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, params string[] dependents)
        {
            Status[name] = ServiceControllerStatus.Running;
            _dependents[name] = dependents.ToList();
        }

        public ServiceControllerStatus GetStatus(string serviceName) =>
            Status.TryGetValue(serviceName, out var status)
                ? status
                : throw new InvalidOperationException($"Service {serviceName} was not found.");

        public List<string> GetRunningDependents(string serviceName) =>
            _dependents.TryGetValue(serviceName, out var dependents)
                ? dependents.Where(d => Status[d] == ServiceControllerStatus.Running).ToList()
                : new List<string>();

        public void StopAndWait(string serviceName, TimeSpan timeout)
        {
            foreach (var dependent in GetRunningDependents(serviceName))
                Status[dependent] = ServiceControllerStatus.Stopped;

            if (TimesOut.Contains(serviceName))
            {
                Status[serviceName] = ServiceControllerStatus.StopPending;
                throw new global::System.ServiceProcess.TimeoutException("Time out has expired.");
            }

            Status[serviceName] = ServiceControllerStatus.Stopped;
        }

        public bool TryRestart(string serviceName)
        {
            if (WillNotStart.Contains(serviceName))
                return false;

            Restarted.Add(serviceName);
            Status[serviceName] = ServiceControllerStatus.Running;
            return true;
        }
    }
}
