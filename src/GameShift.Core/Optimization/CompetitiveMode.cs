using System.Diagnostics;
using System.Text.Json;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.System;
using Serilog;

namespace GameShift.Core.Optimization;

/// <summary>
/// GameShift no longer suspends overlays or closes background apps during a game. The class stays so
/// a session journal written by an earlier version still resumes the suspended processes and
/// restores the Discord overlay value.
/// </summary>
public class CompetitiveMode : IOptimization, IJournaledOptimization
{
    private readonly ILogger _logger = SettingsManager.Logger;

    private const string DiscordOverlayRegistryPath = @"SOFTWARE\Discord\Modules\discord_overlay2";
    private const string DiscordOverlayEnabledValue = "enabled";

    public const string OptimizationId = "Competitive Mode";

    public string Name => OptimizationId;
    public string Description => "Revert-only: resumes processes suspended by earlier versions";
    public bool IsApplied => false;
    public bool IsAvailable => false;

    public Task<bool> ApplyAsync(SystemStateSnapshot snapshot, GameProfile profile) => Task.FromResult(false);
    public Task<bool> RevertAsync(SystemStateSnapshot snapshot) => Task.FromResult(true);

    public bool CanApply(SystemContext context) => false;

    public OptimizationResult Apply() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, "Revert-only: no longer applied");

    public OptimizationResult Revert() =>
        new(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);

    public bool Verify() => false;

    internal RecoveryRegistry RecoveryRegistry { get; init; } = RecoveryRegistry.Windows;

    /// <summary>
    /// Resumes a journaled PID; false only when that process is still there and stays suspended.
    /// A seam so the revert can be tested without touching processes.
    /// </summary>
    internal Func<int, string, bool>? ResumeSuspended { get; init; }

    /// <summary>
    /// Watchdog/boot recovery after a crash: restores the persistent Discord-overlay registry value
    /// AND resumes the processes that were suspended during the session (Discord/Steam/NVIDIA),
    /// using the PIDs/names journaled in Apply. A PID alone is enough to resume (see ResumeProcess),
    /// and the watchdog runs as SYSTEM, so these apps are not left frozen after a crash. Each resume
    /// is guarded against PID reuse.
    /// </summary>
    public OptimizationResult RevertFromRecord(string originalValueJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(originalValueJson);
            var root = doc.RootElement;
            var problems = new List<string>();
            int refused = 0;

            bool modified = root.TryGetProperty("discordModified", out var m) && m.ValueKind == JsonValueKind.True;
            if (modified)
            {
                // No recorded previous value means the value was absent before, so it is deleted.
                var previous = root.TryGetProperty("discordPrev", out var pv) ? pv : default;
                if (previous.ValueKind == JsonValueKind.Undefined)
                {
                    RecoveryRegistry.DeleteValue(RecoveryHive.CurrentUser, DiscordOverlayRegistryPath, DiscordOverlayEnabledValue);
                    _logger.Information("[CompetitiveMode] Deleted Discord overlay value (was absent before session)");
                }
                else if (RecoveryRegistry.RestoreDword(
                             RecoveryHive.CurrentUser, DiscordOverlayRegistryPath, DiscordOverlayEnabledValue, previous))
                {
                    _logger.Information("[CompetitiveMode] Restored Discord overlay registry from the journal record");
                }
                else
                {
                    _logger.Warning("[CompetitiveMode] Could not restore the Discord overlay registry value");
                    problems.Add("Discord overlay setting");
                }
            }

            // Resume any processes suspended during the crashed session so Discord/Steam/NVIDIA are
            // not left frozen. PID reuse is guarded inside ResumeSuspendedByRecord.
            if (root.TryGetProperty("suspended", out var susp) && susp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in susp.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.Object &&
                               item.TryGetProperty("name", out var nameEl) &&
                               nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()
                        : null;
                    if (!RecoveryInputRules.IsSuspendedOverlay(name) ||
                        !RecoveryInputRules.TryInt32Property(item, "pid", out int pid))
                    {
                        _logger.Warning("[CompetitiveMode] Refused a suspended-process entry GameShift did not write: {Entry}", item.GetRawText());
                        refused++;
                        continue;
                    }

                    if (!(ResumeSuspended ?? ResumeSuspendedByRecord)(pid, name!))
                        problems.Add($"{name} (PID {pid})");
                }
            }

            if (refused > 0)
                problems.Add($"{refused} refused process entr{(refused == 1 ? "y" : "ies")}");

            return problems.Count == 0
                ? new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted)
                : new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed,
                    $"Not restored: {string.Join(", ", problems)}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CompetitiveMode] RevertFromRecord failed");
            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Resumes a single process by PID using NtResumeProcess via NativeInterop.
    /// Process handle is always closed in a finally block.
    /// </summary>
    private bool ResumeProcess(int processId, string processName)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeInterop.OpenProcess(
                NativeInterop.PROCESS_SUSPEND_RESUME, false, processId);

            if (handle == IntPtr.Zero)
            {
                _logger.Warning(
                    "[CompetitiveMode] Failed to open process handle for resume - {ProcessName} (PID: {ProcessId})",
                    processName,
                    processId);
                return false;
            }

            int status = NativeInterop.NtResumeProcess(handle);
            if (status != 0)
            {
                _logger.Warning(
                    "[CompetitiveMode] NtResumeProcess returned non-zero status {Status} for {ProcessName} (PID: {ProcessId})",
                    status,
                    processName,
                    processId);
                return false;
            }

            return true;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeInterop.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// Resumes a single journaled process by PID after a crash (used by RevertFromRecord), guarding
    /// against PID reuse: if the PID now belongs to a different process, or has already exited, it is
    /// left untouched and nothing of the session is left suspended.
    /// </summary>
    private bool ResumeSuspendedByRecord(int processId, string processName)
    {
        try
        {
            using var check = Process.GetProcessById(processId);
            if (!string.Equals(check.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "[CompetitiveMode] PID reuse detected on recovery - PID {ProcessId} is now {NewName}, expected {OldName}, skipping resume",
                    processId, check.ProcessName, processName);
                return true;
            }
        }
        catch (ArgumentException)
        {
            // Process already exited - nothing to resume.
            return true;
        }
        catch (InvalidOperationException)
        {
            // Process exited between lookup and access (race) - nothing to resume.
            return true;
        }

        if (!ResumeProcess(processId, processName))
            return false;

        _logger.Information(
            "[CompetitiveMode] Resumed {ProcessName} (PID: {ProcessId}) during crash recovery",
            processName, processId);
        return true;
    }
}
