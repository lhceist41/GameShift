using GameShift.Core.Optimization;
using GameShift.Core.Profiles;
using Serilog;

namespace GameShift.Core.Verification;

/// <summary>Outcome of one full probe -> apply -> probe -> revert -> probe cycle.</summary>
public sealed class RevertVerificationResult
{
    public required int BeforeItemCount { get; init; }
    public required int AppliedChangeCount { get; init; }
    public required List<StateDifference> AppliedChanges { get; init; }
    public required List<StateDifference> Failures { get; init; }
    public required List<StateDifference> ExternalDrift { get; init; }
    public required List<string> Warnings { get; init; }
    public string? Error { get; init; }

    public bool Passed => Error == null && Failures.Count == 0;

    public string FormatReport()
    {
        var lines = new List<string>
        {
            "Revert symmetry verification",
            $"  probed items:          {BeforeItemCount}",
            $"  changes while applied: {AppliedChangeCount}",
            $"  residue after revert:  {Failures.Count}",
            $"  external drift (info): {ExternalDrift.Count}",
            $"  capture warnings:      {Warnings.Count}",
            $"  verdict:               {(Passed ? "PASS - system state matches the pre-session state" : "FAIL")}",
        };
        if (Error != null) lines.Add($"  error: {Error}");

        if (AppliedChanges.Count > 0)
        {
            lines.Add("  changes observed during the session (proof the run was meaningful):");
            foreach (var d in AppliedChanges.Take(60))
                lines.Add($"    ~ {d.Key}: '{d.Before}' -> '{d.After}'");
            if (AppliedChanges.Count > 60) lines.Add($"    ... and {AppliedChanges.Count - 60} more");
        }

        if (Failures.Count > 0)
        {
            lines.Add("  RESIDUE (state not restored to its pre-session value):");
            foreach (var d in Failures)
                lines.Add($"    ! {d.Key}: '{d.Before}' -> '{d.After}'");
        }

        if (ExternalDrift.Count > 0)
        {
            lines.Add("  external drift (not touched by GameShift during the session, informational):");
            foreach (var d in ExternalDrift.Take(20))
                lines.Add($"    ? {d.Key}: '{d.Before}' -> '{d.After}'");
            if (ExternalDrift.Count > 20) lines.Add($"    ... and {ExternalDrift.Count - 20} more");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Runs the trust-contract verification cycle end to end on the REAL machine:
/// capture a full <see cref="StateProbe"/>, activate a maximum-coverage session through the real
/// <see cref="OptimizationEngine"/> with the same module set the app ships, capture again (the
/// mid-probe proves the session actually changed things AND discovers exactly which service/task
/// items GameShift touched), deactivate, capture again, and require the final probe to match the
/// first one.
///
/// Diff policy: registry and power items must ALWAYS round-trip (they are immune to background
/// churn on the probed surface). Service status and scheduled task items are only failures when
/// the mid-probe shows GameShift itself touched them; otherwise a difference is reported as
/// external drift, because Windows starts and stops unrelated services on its own schedule.
/// Demand-start (Manual) services are a special case even when touched: their running state is
/// OS-managed process lifetime, not configuration. Example: stopping the Diagnostic Policy
/// Service makes its WDI worker host (WdiSystemHost) idle-exit with no SCM dependency edge, and
/// Windows relaunches it only when a diagnostic scenario next needs it. Restoring such state
/// would be restoring noise that decays minutes later, so it is reported as drift. Automatic
/// services GameShift touched, every start type, and the whole registry/power surface remain
/// hard contractual failures.
///
/// The run uses the real journal, so if the harness dies mid-cycle the existing watchdog and
/// boot recovery revert the session exactly like a crashed game session. Deliberately session-
/// scoped: persistent SystemTweaks have their own apply/revert lifecycle and are out of scope.
/// </summary>
public static class RevertVerificationRunner
{
    /// <summary>
    /// The engine module set, mirroring ServiceFactory.CreateAll in composition and order.
    /// VbsHvciToggle and CoreIsolationManager are intentionally absent: they are reboot-scoped,
    /// user-initiated changes that are not part of a gaming session (same as the shipped app).
    /// The coverage contract test asserts this list stays in sync with GameShift.Core.
    /// </summary>
    public static IOptimization[] CreateEngineModules() => new IOptimization[]
    {
        new ServiceSuppressor(),
        new PowerPlanSwitcher(),
        new TimerResolutionManager(),
        new ProcessPriorityBooster(),
        new MemoryOptimizer(),
        new VisualEffectReducer(),
        new NetworkOptimizer(),
        new HybridCpuDetector(),
        new MpoToggle(),
        new CompetitiveMode(),
        new GpuDriverOptimizer(),
        new ScheduledTaskSuppressor(),
        new CpuParkingManager(),
        new IoPriorityManager(),
        new EfficiencyModeController(),
        new CpuSchedulingOptimizer(),
        new SessionSystemTweaksOptimizer(),
    };

    /// <summary>
    /// The synthetic maximum-coverage profile. Competitive intensity so intensity-gated modules
    /// (MPO, C-state limiting) participate. Pinning toggles stay off because they target a live
    /// game process that does not exist here, and widget/Defender toggles stay off out of
    /// politeness on developer machines; neither leaves persistent state, which is what this
    /// harness verifies.
    /// </summary>
    public static GameProfile CreateVerificationProfile() => new()
    {
        Id = "state-verification",
        GameName = "State Verification Probe",
        ExecutableName = SystemStateProber.ProbeExecutableName,
        ProcessId = global::System.Environment.ProcessId,
        Intensity = OptimizationIntensity.Competitive,
        DisableProcessorIdle = true,
        DisableMpo = true,
        EnableCompetitiveMode = true,
        UsePerformanceCoresOnly = false,
        PinToVCacheCcd = false,
        KillWidgets = false,
        SuppressDefenderScheduledScan = false,
    };

    /// <summary>
    /// Splits the residual differences into hard failures and informational drift. Pure, so the
    /// policy itself is unit-tested:
    /// registry/power/service-start-type differences are always failures; scheduled task and
    /// service STATUS differences are failures only when the session touched them; and a touched
    /// service status is still only drift when the service is demand-start (Manual or Disabled),
    /// because Windows owns the process lifetime of demand-start services.
    /// </summary>
    internal static (List<StateDifference> Failures, List<StateDifference> Drift) ClassifyResidual(
        StateProbe before, IReadOnlySet<string> touchedKeys, IReadOnlyList<StateDifference> residual)
    {
        var failures = new List<StateDifference>();
        var drift = new List<StateDifference>();

        foreach (var diff in residual)
        {
            bool isServiceStatus = diff.Key.StartsWith("svc:status:", StringComparison.OrdinalIgnoreCase);
            bool isTask = diff.Key.StartsWith("task:", StringComparison.OrdinalIgnoreCase);

            if (!isServiceStatus && !isTask)
            {
                failures.Add(diff);   // registry, power, service start types: always contractual
                continue;
            }

            if (!touchedKeys.Contains(diff.Key))
            {
                drift.Add(diff);      // background churn by Windows itself, not GameShift residue
                continue;
            }

            if (isServiceStatus)
            {
                var serviceName = diff.Key["svc:status:".Length..];
                bool autoStart = before.Items.TryGetValue($"svc:start:{serviceName}", out var startType)
                    && startType is "Automatic" or "Boot" or "System";
                if (!autoStart)
                {
                    drift.Add(diff);  // demand-start: OS-managed process lifetime, self-heals
                    continue;
                }
            }

            failures.Add(diff);
        }

        return (failures, drift);
    }

    public static async Task<RevertVerificationResult> RunAsync(ILogger logger)
    {
        var prober = new SystemStateProber();
        var warnings = new List<string>();
        string? error = null;

        logger.Information("[RevertVerification] capturing pre-session probe");
        var before = prober.Capture();
        warnings.AddRange(before.Warnings);

        var engine = new OptimizationEngine(CreateEngineModules());
        var profile = CreateVerificationProfile();

        StateProbe? mid = null;
        try
        {
            logger.Information("[RevertVerification] activating maximum-coverage session");
            await engine.ActivateProfileAsync(profile);

            mid = prober.Capture();
            warnings.AddRange(mid.Warnings);
        }
        catch (Exception ex)
        {
            error = $"activation failed: {ex.Message}";
            logger.Error(ex, "[RevertVerification] activation failed");
        }
        finally
        {
            try
            {
                logger.Information("[RevertVerification] deactivating session");
                await engine.DeactivateProfileAsync();
            }
            catch (Exception ex)
            {
                error ??= $"deactivation failed: {ex.Message}";
                logger.Error(ex, "[RevertVerification] deactivation failed");
            }
        }

        // Let asynchronous service starts and powercfg activations settle before the final probe.
        await Task.Delay(TimeSpan.FromSeconds(4));

        logger.Information("[RevertVerification] capturing post-revert probe");
        var after = prober.Capture();
        warnings.AddRange(after.Warnings);

        var appliedChanges = mid != null ? ProbeComparison.Compare(before, mid) : new List<StateDifference>();
        var touchedKeys = new HashSet<string>(appliedChanges.Select(d => d.Key), StringComparer.OrdinalIgnoreCase);

        var residual = ProbeComparison.Compare(before, after);
        var (failures, drift) = ClassifyResidual(before, touchedKeys, residual);

        return new RevertVerificationResult
        {
            BeforeItemCount = before.Count,
            AppliedChangeCount = appliedChanges.Count,
            AppliedChanges = appliedChanges,
            Failures = failures,
            ExternalDrift = drift,
            Warnings = warnings.Distinct().ToList(),
            Error = error,
        };
    }
}
