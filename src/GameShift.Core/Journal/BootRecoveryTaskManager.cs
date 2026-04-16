using System.Text;
using GameShift.Core.Config;
using Serilog;

namespace GameShift.Core.Journal;

/// <summary>
/// Registers and unregisters the GameShift boot-recovery Windows Scheduled Task.
///
/// Task name  : GameShift\BootRecovery
/// Trigger    : System boot, 30-second delay
/// Action     : GameShift.Watchdog.exe --boot-recovery
/// Principal  : SYSTEM, HighestAvailable
/// Settings   : RunOnlyIfLoggedOn=false, ExecutionTimeLimit=PT5M
///
/// Registration uses <c>schtasks /Create /XML</c> with a UTF-16 temp file.
/// The task is idempotent (/F overwrites any existing registration).
///
/// Call <see cref="EnsureRegistered"/> once during GameShift.App startup.
/// Call <see cref="Unregister"/> from an installer / uninstall flow.
/// </summary>
public static class BootRecoveryTaskManager
{
    public const string TaskFolder = "GameShift";
    public const string TaskName   = "BootRecovery";
    public const string FullTaskName = @"GameShift\BootRecovery";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers (or re-registers) the boot-recovery scheduled task.
    /// Safe to call on every startup — /F makes the operation idempotent.
    /// Logs a warning and returns silently if <paramref name="watchdogExePath"/> does not exist.
    /// </summary>
    /// <param name="watchdogExePath">Absolute path to GameShift.Watchdog.exe.</param>
    /// <param name="logger">Optional caller-supplied logger; falls back to SettingsManager.Logger.</param>
    public static void EnsureRegistered(string watchdogExePath, ILogger? logger = null)
    {
        var log = (logger ?? SettingsManager.Logger).ForContext(typeof(BootRecoveryTaskManager));

        if (!File.Exists(watchdogExePath))
        {
            log.Debug(
                "[BootRecoveryTask] Watchdog executable not found at '{Path}' — skipping task registration",
                watchdogExePath);
            return;
        }

        log.Information(
            "[BootRecoveryTask] Registering boot-recovery task (watchdog: {Path})",
            watchdogExePath);

        var xml = BuildTaskXml(watchdogExePath);
        var tempFile = Path.Combine(Path.GetTempPath(), $"GameShift_BootTask_{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks expects UTF-16 LE with BOM
            File.WriteAllText(tempFile, xml, Encoding.Unicode);

            RunSchtasks($"/Create /TN \"{FullTaskName}\" /XML \"{tempFile}\" /F", log, out var output);
            log.Information("[BootRecoveryTask] Task registered: {Output}", output.Trim());
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[BootRecoveryTask] Failed to register boot-recovery task");
        }
        finally
        {
            TryDelete(tempFile, log);
        }
    }

    /// <summary>
    /// Removes the boot-recovery scheduled task.
    /// Safe to call even if the task does not exist.
    /// </summary>
    public static void Unregister(ILogger? logger = null)
    {
        var log = (logger ?? SettingsManager.Logger).ForContext(typeof(BootRecoveryTaskManager));
        log.Information("[BootRecoveryTask] Unregistering boot-recovery task");

        try
        {
            RunSchtasks($"/Delete /TN \"{FullTaskName}\" /F", log, out var output);
            log.Information("[BootRecoveryTask] Task removed: {Output}", output.Trim());
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[BootRecoveryTask] Failed to unregister boot-recovery task");
        }
    }

    // ── Task XML ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the task XML for schtasks /Create /XML.
    /// Uses the SYSTEM account (S-1-5-18) so the task runs regardless of logged-on state.
    /// </summary>
    private static string BuildTaskXml(string watchdogExePath)
    {
        // Escape the exe path for XML attribute/element content
        var escapedPath = global::System.Security.SecurityElement.Escape(watchdogExePath);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Reverts GameShift system optimizations if a previous session crashed or was interrupted by a power loss or BSOD.</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger>
                  <Delay>PT30S</Delay>
                </BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedPath}</Command>
                  <Arguments>--boot-recovery</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void RunSchtasks(string arguments, ILogger log, out string output)
    {
        output = string.Empty;
        log.Debug("[BootRecoveryTask] schtasks {Args}", arguments);

        using var p = global::System.Diagnostics.Process.Start(
            new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

        if (p == null)
        {
            log.Warning("[BootRecoveryTask] Failed to start schtasks.exe");
            return;
        }

        var stderr = "";
        var stderrTask = Task.Run(() => { stderr = p.StandardError.ReadToEnd(); });
        var stdout = p.StandardOutput.ReadToEnd();
        stderrTask.Wait(30_000);
        p.WaitForExit(30_000);
        output = stdout;

        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            log.Warning("[BootRecoveryTask] schtasks stderr: {Err}", stderr.Trim());
    }

    private static void TryDelete(string path, ILogger log)
    {
        try { File.Delete(path); }
        catch (Exception ex) { log.Debug(ex, "[BootRecoveryTask] Could not delete temp file {Path}", path); }
    }
}
