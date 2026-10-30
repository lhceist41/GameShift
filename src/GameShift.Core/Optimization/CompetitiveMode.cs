using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
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
            bool modified = root.TryGetProperty("discordModified", out var m) && m.ValueKind == JsonValueKind.True;

            if (modified)
            {
                using var key = Registry.CurrentUser.OpenSubKey(DiscordOverlayRegistryPath, writable: true);
                if (key != null)
                {
                    if (root.TryGetProperty("discordPrev", out var pv) && pv.ValueKind == JsonValueKind.Number)
                    {
                        key.SetValue(DiscordOverlayEnabledValue, pv.GetInt32(), RegistryValueKind.DWord);
                        _logger.Information("[CompetitiveMode] Restored Discord overlay registry to {Value}", pv.GetInt32());
                    }
                    else
                    {
                        key.DeleteValue(DiscordOverlayEnabledValue, throwOnMissingValue: false);
                        _logger.Information("[CompetitiveMode] Deleted Discord overlay value (was absent before session)");
                    }
                }
            }

            // Resume any processes suspended during the crashed session so Discord/Steam/NVIDIA are
            // not left frozen. PID reuse is guarded inside ResumeSuspendedByRecord.
            if (root.TryGetProperty("suspended", out var susp) && susp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in susp.EnumerateArray())
                {
                    if (item.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out int pid) &&
                        item.TryGetProperty("name", out var nameEl))
                    {
                        var name = nameEl.GetString();
                        if (!string.IsNullOrEmpty(name))
                            ResumeSuspendedByRecord(pid, name);
                    }
                }
            }

            return new OptimizationResult(OptimizationId, string.Empty, string.Empty, OptimizationState.Reverted);
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
    private void ResumeProcess(int processId, string processName)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeInterop.OpenProcess(
                NativeInterop.PROCESS_SUSPEND_RESUME, false, processId);

            if (handle == IntPtr.Zero)
            {
                _logger.Warning(
                    "[CompetitiveMode] Failed to open process handle for resume - {ProcessName} (PID: {ProcessId}) may have already exited",
                    processName,
                    processId);
                return;
            }

            int status = NativeInterop.NtResumeProcess(handle);
            if (status != 0)
            {
                _logger.Warning(
                    "[CompetitiveMode] NtResumeProcess returned non-zero status {Status} for {ProcessName} (PID: {ProcessId})",
                    status,
                    processName,
                    processId);
            }
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
    /// left untouched.
    /// </summary>
    private void ResumeSuspendedByRecord(int processId, string processName)
    {
        try
        {
            using var check = Process.GetProcessById(processId);
            if (!string.Equals(check.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "[CompetitiveMode] PID reuse detected on recovery - PID {ProcessId} is now {NewName}, expected {OldName}, skipping resume",
                    processId, check.ProcessName, processName);
                return;
            }
        }
        catch (ArgumentException)
        {
            // Process already exited - nothing to resume.
            return;
        }
        catch (InvalidOperationException)
        {
            // Process exited between lookup and access (race) - nothing to resume.
            return;
        }

        ResumeProcess(processId, processName);
        _logger.Information(
            "[CompetitiveMode] Resumed {ProcessName} (PID: {ProcessId}) during crash recovery",
            processName, processId);
    }
}
