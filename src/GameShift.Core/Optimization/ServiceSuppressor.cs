using System.ServiceProcess;
using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;

namespace GameShift.Core.Optimization;

/// <summary>
/// Pauses the Windows services that can start downloads, installs or indexing during a game:
/// Windows Update, Update Orchestrator, Delivery Optimization, BITS and Windows Search.
/// A safety list prevents critical system services from ever being stopped.
/// </summary>
public class ServiceSuppressor : IOptimization, IJournaledOptimization
{
    /// <summary>
    /// A service this module pauses during a game.
    /// </summary>
    /// <param name="ServiceName">Windows service name (e.g., "WSearch").</param>
    /// <param name="DisplayName">Human-readable name for logging/UI.</param>
    public readonly record struct ServiceInfo(string ServiceName, string DisplayName);

    internal static readonly ServiceInfo[] SessionServices =
    {
        new("wuauserv", "Windows Update"),
        new("UsoSvc",   "Update Orchestrator Service"),
        new("DoSvc",    "Delivery Optimization"),
        new("BITS",     "Background Intelligent Transfer Service"),
        new("WSearch",  "Windows Search"),
    };

    // ── Never-stop safety list ───────────────────────────────────────

    /// <summary>
    /// Services that must NEVER be stopped, regardless of tier assignments.
    /// Stopping any of these causes system instability, networking failure, or audio loss.
    /// </summary>
    private static readonly HashSet<string> NeverStopServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs",                 // Remote Procedure Call - stopping = instant freeze
        "RpcEptMapper",          // RPC Endpoint Mapper
        "DcomLaunch",            // DCOM Server Process Launcher
        "LSM",                   // Local Session Manager
        "CoreMessagingRegistrar",
        "Winmgmt",               // WMI - GameShift itself depends on this
        "NlaSvc",                // Network Location Awareness - breaks networking
        "Wcmsvc",                // Windows Connection Manager
        "nsi",                   // Network Store Interface - breaks networking
        "Dhcp",                  // DHCP Client - breaks networking
        "Dnscache",              // DNS Client - breaks name resolution
        "BrokerInfrastructure",  // Background Tasks Infrastructure
        "Power",                 // Power service
        "AudioSrv",              // Windows Audio - breaks game audio
        "AudioEndpointBuilder",  // Audio Endpoint Builder
        "EventLog",              // Windows Event Log

        // Anti-cheat services
        "vgc",                   // Riot Vanguard
        "BEService",             // BattlEye
        "EasyAntiCheat",         // EAC
        "EasyAntiCheat_EOS",     // EAC (Epic Online Services variant)
    };

    // ── IOptimization implementation ─────────────────────────────────

    public const string OptimizationId = "Windows Service Suppressor";

    public string Name => OptimizationId;

    public string Description => "Pauses Windows Update, Delivery Optimization, background transfers and search indexing during a game";

    public bool IsApplied { get; private set; }

    public bool IsAvailable => true; // Services that don't exist are skipped individually

    // Journaling: context for Apply(), and the list of services this run actually stopped
    // (serialized into the journal so the watchdog can restart them after a crash).
    private SystemContext? _context;
    private readonly List<string> _stoppedServices = new();

    internal IServiceControl Services { get; init; } = WindowsServiceControl.Instance;

    /// <summary>Replaces <see cref="SessionServices"/> when set, so tests use their own services.</summary>
    internal Func<GameProfile, IReadOnlyList<ServiceInfo>>? Targets { get; init; }

    /// <summary>
    /// Stops each of the session services that is running. Records original state before stopping each service.
    /// </summary>
    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile)
    {
        CanApply(new SystemContext { Profile = profile, Snapshot = snapshot });
        var result = Apply();
        return Task.FromResult(result.State == OptimizationState.Applied);
    }

    // ── IJournaledOptimization ────────────────────────────────────────────────

    public bool CanApply(SystemContext context)
    {
        _context = context;
        return true;
    }

    public OptimizationResult Apply()
    {
        if (_context == null)
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "No context");

        ApplyInternal(_context.Snapshot, _context.Profile);

        return new OptimizationResult(
            OptimizationId,
            JsonSerializer.Serialize(new { services = _stoppedServices }),
            string.Empty,
            OptimizationState.Applied);
    }

    private void ApplyInternal(SystemStateSnapshot snapshot, GameProfile profile)
    {
        _stoppedServices.Clear();
        int stoppedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        var servicesToSuppress = Targets?.Invoke(profile) ?? SessionServices;

        foreach (var svcInfo in servicesToSuppress)
        {
            // Safety check - never stop critical services
            if (NeverStopServices.Contains(svcInfo.ServiceName))
            {
                SettingsManager.Logger.Warning(
                    "[ServiceSuppressor] Service {ServiceName} is in the never-stop safety list - skipping",
                    svcInfo.ServiceName);
                skippedCount++;
                continue;
            }

            try
            {
                var status = Services.GetStatus(svcInfo.ServiceName);

                // Only stop if currently running
                if (status == ServiceControllerStatus.Running)
                {
                    // ServiceController.Stop() also stops every RUNNING dependent service.
                    // Enumerate them first so (a) we refuse to cascade into a never-stop
                    // service, and (b) every cascade-stopped dependent is recorded and
                    // restarted on revert. Found by the revert-symmetry harness: stopping a
                    // tier service left its dependent WdiSystemHost stopped after the session.
                    var runningDependents = Services.GetRunningDependents(svcInfo.ServiceName);

                    var protectedDependent = runningDependents
                        .FirstOrDefault(d => NeverStopServices.Contains(d));
                    if (protectedDependent != null)
                    {
                        SettingsManager.Logger.Warning(
                            "[ServiceSuppressor] Not stopping {ServiceName}: running dependent {Dependent} is in the never-stop safety list",
                            svcInfo.ServiceName, protectedDependent);
                        skippedCount++;
                        continue;
                    }

                    // Record original state before making changes (parent and dependents)
                    snapshot.RecordServiceState(svcInfo.ServiceName, status);
                    foreach (var dependent in runningDependents)
                        snapshot.RecordServiceState(dependent, ServiceControllerStatus.Running);

                    // Recorded before Stop(): a stop that times out or fails part-way can still
                    // leave the service or a dependent stopped, and revert must restart them.
                    // Restarting a service that is still running is a no-op.
                    RecordForRestart(svcInfo.ServiceName, runningDependents);

                    SettingsManager.Logger.Debug(
                        "[ServiceSuppressor] Stopping service {ServiceName} ({DisplayName})",
                        svcInfo.ServiceName,
                        svcInfo.DisplayName);

                    Services.StopAndWait(svcInfo.ServiceName, TimeSpan.FromSeconds(10));

                    SettingsManager.Logger.Information(
                        "[ServiceSuppressor] Successfully stopped service {ServiceName}",
                        svcInfo.ServiceName);
                    stoppedCount++;
                }
                else
                {
                    SettingsManager.Logger.Debug(
                        "[ServiceSuppressor] Skipping service {ServiceName} (status: {Status})",
                        svcInfo.ServiceName,
                        status);
                    skippedCount++;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Service doesn't exist on this system - skip silently
                SettingsManager.Logger.Debug(
                    "[ServiceSuppressor] Service {ServiceName} not found on system: {Message}",
                    svcInfo.ServiceName,
                    ex.Message);
                skippedCount++;
            }
            catch (global::System.ServiceProcess.TimeoutException ex)
            {
                // Service didn't stop within timeout - log warning and continue
                SettingsManager.Logger.Warning(
                    "[ServiceSuppressor] Service {ServiceName} failed to stop within timeout: {Message}",
                    svcInfo.ServiceName,
                    ex.Message);
                errorCount++;
            }
            catch (Exception ex)
            {
                // Other errors - log and continue to next service
                SettingsManager.Logger.Warning(
                    ex,
                    "[ServiceSuppressor] Failed to stop service {ServiceName}",
                    svcInfo.ServiceName);
                errorCount++;
            }
        }

        SettingsManager.Logger.Information(
            "[ServiceSuppressor] Completed - {StoppedCount} stopped, {SkippedCount} skipped, {ErrorCount} errors",
            stoppedCount,
            skippedCount,
            errorCount);

        IsApplied = true;
    }

    /// <summary>
    /// Parent first so revert starts it before its dependents; a dependent's Start() also pulls
    /// required services up, so order is belt and suspenders.
    /// </summary>
    private void RecordForRestart(string serviceName, IReadOnlyList<string> runningDependents)
    {
        if (!_stoppedServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
            _stoppedServices.Add(serviceName);
        foreach (var dependent in runningDependents)
        {
            if (!_stoppedServices.Contains(dependent, StringComparer.OrdinalIgnoreCase))
            {
                _stoppedServices.Add(dependent);
                SettingsManager.Logger.Information(
                    "[ServiceSuppressor] Dependent service {Dependent} stops with {ServiceName} and will be restarted on revert",
                    dependent, serviceName);
            }
        }
    }

    /// <summary>
    /// Restarts all services that were stopped during ApplyAsync.
    /// Uses snapshot to determine which services to restart.
    /// </summary>
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot)
    {
        var result = Revert();
        return Task.FromResult(result.State == OptimizationState.Reverted);
    }

    public bool Verify() => IsApplied;

    public OptimizationResult Revert()
    {
        var notRestarted = RestartServices(_stoppedServices);

        // Services that could not be restarted stay listed, as the evidence of what is still stopped.
        _stoppedServices.Clear();
        _stoppedServices.AddRange(notRestarted);
        IsApplied = false;

        return notRestarted.Count == 0
            ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
            : RestartFailed(notRestarted);
    }

    /// <summary>
    /// Watchdog recovery: restarts the services recorded in the journal OriginalValue JSON,
    /// without any live instance state.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ServiceRevertPayload>(originalValueJson);
            if (payload == null)
            {
                // Apply always journals a payload object; a literal JSON null means the record is
                // corrupt. Report failure instead of claiming a successful revert.
                return new OptimizationResult(
                    OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
                    "Corrupt journal record (null payload)");
            }

            var notRestarted = RestartServices(payload.Services ?? new List<string>());
            return notRestarted.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : RestartFailed(notRestarted);
        }
        catch (Exception ex)
        {
            SettingsManager.Logger.Error(ex, "[ServiceSuppressor] RevertFromRecord failed");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Collects every RUNNING service that would be cascade-stopped by stopping
    /// <paramref name="parentName"/> (breadth-first over DependentServices, so dependents of
    /// dependents are included). Discovery order places each service after the one it depends
    /// on, which is the correct restart order. Per-service query errors are tolerated: a
    /// dependent we cannot inspect is simply not recorded, matching today's behavior.
    /// </summary>
    private static List<string> CollectRunningDependents(ServiceController parent, string parentName)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parentName };
        var queue = new Queue<ServiceController>();
        queue.Enqueue(parent);

        try
        {
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                ServiceController[] dependents;
                try
                {
                    dependents = current.DependentServices;
                }
                catch (Exception ex)
                {
                    SettingsManager.Logger.Debug(
                        "[ServiceSuppressor] Could not enumerate dependents of {ServiceName}: {Message}",
                        current.ServiceName, ex.Message);
                    continue;
                }

                foreach (var dependent in dependents)
                {
                    try
                    {
                        if (!seen.Add(dependent.ServiceName))
                        {
                            dependent.Dispose();
                            continue;
                        }

                        if (dependent.Status is ServiceControllerStatus.Running
                            or ServiceControllerStatus.StartPending)
                        {
                            result.Add(dependent.ServiceName);
                            queue.Enqueue(dependent); // keep walking; dispose after dequeue pass
                        }
                        else
                        {
                            dependent.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        SettingsManager.Logger.Debug(
                            "[ServiceSuppressor] Could not inspect dependent service: {Message}", ex.Message);
                        dependent.Dispose();
                    }
                }

                if (!ReferenceEquals(current, parent))
                    current.Dispose();
            }
        }
        finally
        {
            while (queue.Count > 0)
            {
                var leftover = queue.Dequeue();
                if (!ReferenceEquals(leftover, parent))
                    leftover.Dispose();
            }
        }

        return result;
    }

    // Slow services - SysMain (SuperFetch) in particular - can take well over 10 seconds to
    // reach Running after a stop, and the Service Control Manager may abort a START_PENDING that
    // exceeds its wait hint under revert-time load, dropping the service back to Stopped. A single
    // Start() + 10s wait therefore left SysMain stopped after a session (caught by the
    // revert-symmetry harness). Retry the start with a generous per-attempt wait so a slow or
    // SCM-aborted start is recovered before revert returns.
    private const int RestartMaxAttempts = 3;
    private static readonly TimeSpan RestartWaitPerAttempt = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestartRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Restarts the services in order and returns those that could not be restarted.</summary>
    private List<string> RestartServices(IReadOnlyList<string> serviceNames)
    {
        int restartedCount = 0;
        var notRestarted = new List<string>();

        foreach (var name in serviceNames)
        {
            if (Services.TryRestart(name))
                restartedCount++;
            else
                notRestarted.Add(name);
        }

        SettingsManager.Logger.Information(
            "[ServiceSuppressor] Revert completed - {RestartedCount} restarted, {ErrorCount} errors",
            restartedCount, notRestarted.Count);
        return notRestarted;
    }

    private static OptimizationResult RestartFailed(IReadOnlyList<string> notRestarted) =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
            $"Could not restart: {string.Join(", ", notRestarted)}");

    /// <summary>
    /// Restarts a single stopped service, retrying so a slow or SCM-aborted start (SysMain in
    /// particular) still reaches Running before revert returns. Returns true once the service is
    /// confirmed Running (or was already Running); false only if every attempt failed. A service
    /// that no longer exists is a non-restartable error and is not retried, matching prior behavior.
    /// </summary>
    private static bool TryRestartService(string name)
    {
        for (int attempt = 1; attempt <= RestartMaxAttempts; attempt++)
        {
            try
            {
                using var sc = new ServiceController(name);

                if (sc.Status == ServiceControllerStatus.Running)
                {
                    if (attempt > 1)
                        SettingsManager.Logger.Information(
                            "[ServiceSuppressor] Service {ServiceName} reached Running on attempt {Attempt}", name, attempt);
                    else
                        SettingsManager.Logger.Debug(
                            "[ServiceSuppressor] Service {ServiceName} already running", name);
                    return true;
                }

                // Only issue Start() when fully stopped; a StartPending service is already coming
                // up, so just wait for it (a second Start() on a pending service throws).
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    SettingsManager.Logger.Debug(
                        "[ServiceSuppressor] Restarting service {ServiceName} (attempt {Attempt})", name, attempt);
                    sc.Start();
                }

                sc.WaitForStatus(ServiceControllerStatus.Running, RestartWaitPerAttempt);

                SettingsManager.Logger.Information(
                    "[ServiceSuppressor] Successfully restarted service {ServiceName}", name);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // Service doesn't exist on this system - not restartable, do not retry.
                SettingsManager.Logger.Warning(
                    "[ServiceSuppressor] Service {ServiceName} not found during revert: {Message}", name, ex.Message);
                return false;
            }
            catch (global::System.ServiceProcess.TimeoutException ex)
            {
                SettingsManager.Logger.Warning(
                    "[ServiceSuppressor] Service {ServiceName} did not reach Running within timeout (attempt {Attempt}/{Max}): {Message}",
                    name, attempt, RestartMaxAttempts, ex.Message);
            }
            catch (Exception ex)
            {
                SettingsManager.Logger.Warning(
                    ex, "[ServiceSuppressor] Failed to restart service {ServiceName} (attempt {Attempt}/{Max})",
                    name, attempt, RestartMaxAttempts);
            }

            if (attempt < RestartMaxAttempts)
                global::System.Threading.Thread.Sleep(RestartRetryDelay);
        }

        SettingsManager.Logger.Warning(
            "[ServiceSuppressor] Service {ServiceName} could not be restarted after {Max} attempts", name, RestartMaxAttempts);
        return false;
    }

    private sealed class ServiceRevertPayload
    {
        public List<string> Services { get; set; } = new();
    }

    /// <summary>
    /// Service Control Manager operations. A seam so apply and revert can be tested without
    /// touching real services.
    /// </summary>
    internal interface IServiceControl
    {
        /// <summary>Throws <see cref="InvalidOperationException"/> when the service does not exist.</summary>
        ServiceControllerStatus GetStatus(string serviceName);

        List<string> GetRunningDependents(string serviceName);

        /// <summary>Stops the service (and its running dependents) and waits until it is stopped.</summary>
        void StopAndWait(string serviceName, TimeSpan timeout);

        bool TryRestart(string serviceName);
    }

    private sealed class WindowsServiceControl : IServiceControl
    {
        public static readonly WindowsServiceControl Instance = new();

        public ServiceControllerStatus GetStatus(string serviceName)
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status;
        }

        public List<string> GetRunningDependents(string serviceName)
        {
            using var sc = new ServiceController(serviceName);
            return CollectRunningDependents(sc, serviceName);
        }

        public void StopAndWait(string serviceName, TimeSpan timeout)
        {
            using var sc = new ServiceController(serviceName);
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }

        public bool TryRestart(string serviceName) => TryRestartService(serviceName);
    }
}
