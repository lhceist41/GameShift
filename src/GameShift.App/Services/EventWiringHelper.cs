using System;
using GameShift.Core.Detection;
using GameShift.Core.Profiles.GameActions;
using GameShift.Core.System;

namespace GameShift.App.Services;

/// <summary>
/// Holds references to event handler delegates wired during startup,
/// allowing clean unsubscription during shutdown.
/// </summary>
public sealed class EventSubscriptions
{
    public EventHandler<GameDetectedEventArgs>? BgModeGameStarted { get; init; }
    public EventHandler? BgModeAllGamesStopped { get; init; }
    public EventHandler<GameDetectedEventArgs>? MonitorPauseGameStarted { get; init; }
    public EventHandler? MonitorResumeAllGamesStopped { get; init; }
    public EventHandler<ProcessSpawnedEventArgs>? MarkDirtyHandler { get; init; }
}

/// <summary>
/// Wires and unwires event handlers between core services.
/// Extracts the event subscription logic from App.xaml.cs so OnStartup and OnExit stay clean.
/// </summary>
public static class EventWiringHelper
{
    /// <summary>
    /// Subscribes all inter-service event handlers and returns an
    /// <see cref="EventSubscriptions"/> holding the delegate references for later unsubscription.
    /// </summary>
    public static EventSubscriptions WireAll(ServiceRegistry services)
    {
        EventHandler<GameDetectedEventArgs>? bgModeGameStarted = null;
        EventHandler? bgModeAllGamesStopped = null;
        EventHandler<GameDetectedEventArgs>? monitorPauseGameStarted = null;
        EventHandler? monitorResumeAllGamesStopped = null;
        EventHandler<ProcessSpawnedEventArgs>? markDirtyHandler = null;

        // Wire process spawn events to shared cache invalidation
        if (services.Detector != null)
        {
            markDirtyHandler = (_, _) => ProcessSnapshotService.MarkDirty();
            services.Detector.ProcessSpawned += markDirtyHandler;
        }

        // Wire Background Mode gaming session hooks
        if (services.Detector != null && services.BackgroundMode != null)
        {
            bgModeGameStarted = (_, _) => services.BackgroundMode.OnGamingStart();
            bgModeAllGamesStopped = (object? _, EventArgs _2) => services.BackgroundMode.OnGamingStop();
            services.Detector.GameStarted += bgModeGameStarted;
            services.Detector.AllGamesStopped += bgModeAllGamesStopped;
        }

        // Pause dashboard monitors during gaming (they poll every 1-2s and the dashboard isn't visible)
        if (services.Detector != null)
        {
            monitorPauseGameStarted = (_, _) => { services.PerfMon?.Pause(); services.PingMon?.Pause(); services.TempMon?.Pause(); };
            monitorResumeAllGamesStopped = (object? _, EventArgs _2) => { services.PerfMon?.Resume(); services.PingMon?.Resume(); services.TempMon?.Resume(); };
            services.Detector.GameStarted += monitorPauseGameStarted;
            services.Detector.AllGamesStopped += monitorResumeAllGamesStopped;
        }

        // Wire Game Profile manager events
        if (services.Detector != null && services.GameProfileMgr != null)
        {
            services.Detector.GameStarted += services.GameProfileMgr.OnGameStarted;
            services.Detector.AllGamesStopped += services.GameProfileMgr.OnAllGamesStopped;
        }

        return new EventSubscriptions
        {
            BgModeGameStarted = bgModeGameStarted,
            BgModeAllGamesStopped = bgModeAllGamesStopped,
            MonitorPauseGameStarted = monitorPauseGameStarted,
            MonitorResumeAllGamesStopped = monitorResumeAllGamesStopped,
            MarkDirtyHandler = markDirtyHandler
        };
    }

    /// <summary>
    /// Unsubscribes all event handlers that were wired by <see cref="WireAll"/>.
    /// Called during application shutdown to prevent leaks and stale callbacks.
    /// </summary>
    public static void UnwireAll(ServiceRegistry services, EventSubscriptions? subs)
    {
        if (services.Detector == null) return;

        if (subs != null)
        {
            if (subs.BgModeGameStarted != null) services.Detector.GameStarted -= subs.BgModeGameStarted;
            if (subs.BgModeAllGamesStopped != null) services.Detector.AllGamesStopped -= subs.BgModeAllGamesStopped;
            if (subs.MonitorPauseGameStarted != null) services.Detector.GameStarted -= subs.MonitorPauseGameStarted;
            if (subs.MonitorResumeAllGamesStopped != null) services.Detector.AllGamesStopped -= subs.MonitorResumeAllGamesStopped;
            if (subs.MarkDirtyHandler != null) services.Detector.ProcessSpawned -= subs.MarkDirtyHandler;
        }

        if (services.GameProfileMgr != null)
        {
            services.Detector.GameStarted -= services.GameProfileMgr.OnGameStarted;
            services.Detector.AllGamesStopped -= services.GameProfileMgr.OnAllGamesStopped;
        }
    }
}
