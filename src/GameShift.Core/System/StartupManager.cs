using GameShift.Core.Config;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.System;

/// <summary>
/// Manages Windows startup registration via a Scheduled Task with HighestAvailable run level.
///
/// Why a scheduled task and not HKCU\Run:
/// GameShift requires admin elevation (requireAdministrator in the manifest). Windows blocks
/// UAC prompts at login, so apps that need elevation cannot be launched via the Run registry
/// keys - Windows silently refuses. A scheduled task with RunLevel=HighestAvailable is the
/// standard workaround: the Task Scheduler service is already elevated and can launch the
/// app without a UAC prompt at logon time.
///
/// Task name  : GameShift\Startup
/// Trigger    : At log on (current user)
/// Action     : Launch GameShift.App.exe
/// Principal  : Current user with HighestAvailable run level
///
/// For backwards compatibility, SetStartWithWindows(false) also removes the legacy
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry if it exists.
/// </summary>
public static class StartupManager
{
    public const string TaskFolder = "GameShift";
    public const string TaskName = "Startup";
    public const string FullTaskName = @"GameShift\Startup";

    // Legacy HKCU Run key - still cleaned up on unregister so existing installs are fixed up.
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyStartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string LegacyAppName = "GameShift";

    /// <summary>
    /// Returns the current executable path suitable for the scheduled task.
    /// Uses Environment.ProcessPath (works with single-file publish).
    /// </summary>
    private static string GetExePath()
    {
        return Environment.ProcessPath ??
               global::System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ??
               "GameShift.App.exe";
    }

    /// <summary>
    /// Sets whether GameShift should start with Windows.
    /// Creates or removes the scheduled task, and cleans up any legacy HKCU\Run entry.
    /// </summary>
    /// <param name="enable">true to register for startup, false to unregister</param>
    public static void SetStartWithWindows(bool enable)
    {
        // Always clean up the legacy HKCU\Run entry - it never worked for elevated apps
        // and would just leave dead entries in Task Manager's Startup tab.
        RemoveLegacyRunEntry();

        if (enable)
        {
            RegisterScheduledTask();
        }
        else
        {
            UnregisterScheduledTask();
        }
    }

    /// <summary>
    /// Launch-time <see cref="ReconcileStartupRegistration"/> that follows only a saved setting.
    /// Without a settings file the first-run wizard registers the user's choice; with a refused data
    /// folder the settings in hand are defaults, so the task is left as it is.
    /// </summary>
    public static void ReconcileAtLaunch(AppSettings settings) =>
        ReconcileAtLaunch(settings, ReconcileStartupRegistration);

    internal static void ReconcileAtLaunch(AppSettings settings, Action<bool> reconcile)
    {
        if (SettingsManager.IsDataFolderRefused())
        {
            Log.Information("[StartupManager] Startup task left unchanged: the settings folder was refused, so there is no saved choice to follow");
            return;
        }

        if (SettingsManager.IsFirstRun())
        {
            Log.Information("[StartupManager] Startup task left unchanged until the first-run setup saves a choice");
            return;
        }

        reconcile(settings.StartWithWindows);
    }

    /// <summary>
    /// Startup-time reconciliation of the Windows startup registration.
    ///
    /// Unlike <see cref="SetStartWithWindows"/> (which always goes to the Task Scheduler and is
    /// meant for explicit user toggles), this runs on every launch and avoids that work
    /// unless something actually needs to change. It reads the Task Scheduler's on-disk task
    /// definition directly and only (re)registers when the task is missing or points at a
    /// different executable path (e.g. after the app was moved or updated).
    ///
    /// On the common path (already in the desired state) this performs only a cheap registry
    /// cleanup and a single file-existence check - no schtasks.exe spawn. Safe on any thread.
    /// Never throws: on any failure it falls back to the authoritative <see cref="SetStartWithWindows"/>.
    /// </summary>
    /// <param name="enable">Desired state: true = registered for startup, false = not registered.</param>
    public static void ReconcileStartupRegistration(bool enable)
    {
        try
        {
            // Cheap, idempotent registry cleanup (no process spawn).
            RemoveLegacyRunEntry();

            bool taskExists = TryReadRegisteredCommand(out var registeredCommand);

            if (enable)
            {
                var exePath = GetExePath();
                bool upToDate = taskExists
                    && !string.IsNullOrEmpty(registeredCommand)
                    && string.Equals(
                        registeredCommand!.Trim().Trim('"'),
                        exePath.Trim().Trim('"'),
                        StringComparison.OrdinalIgnoreCase);

                if (upToDate)
                    return; // Already registered for the current exe - no schtasks spawn needed.

                RegisterScheduledTask();
            }
            else
            {
                if (taskExists)
                    UnregisterScheduledTask();
                // Not registered and not wanted - nothing to do, no schtasks spawn.
            }
        }
        catch (Exception ex)
        {
            // Reconciliation must never block or crash startup. Fall back to the
            // authoritative path so registration still self-heals.
            Log.Warning(ex, "[StartupManager] Fast startup reconciliation failed; falling back to full registration");
            try { SetStartWithWindows(enable); }
            catch (Exception inner) { Log.Warning(inner, "[StartupManager] Fallback startup registration also failed"); }
        }
    }

    /// <summary>
    /// Reads the &lt;Command&gt; element of the GameShift\Startup task directly from the Task
    /// Scheduler's on-disk definition (%windir%\System32\Tasks\GameShift\Startup), avoiding a
    /// schtasks.exe spawn. The file is the XML the Task Scheduler writes when the task is registered.
    /// </summary>
    /// <param name="command">The decoded executable path the task launches, or null if it could not be parsed.</param>
    /// <returns>True if the task definition file exists (even if the command did not parse), false otherwise.</returns>
    private static bool TryReadRegisteredCommand(out string? command)
    {
        command = null;

        var taskFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "Tasks", TaskFolder, TaskName);

        if (!File.Exists(taskFile))
            return false;

        try
        {
            var xml = File.ReadAllText(taskFile);

            const string openTag = "<Command>";
            const string closeTag = "</Command>";
            var start = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += openTag.Length;
                var end = xml.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    // Reverse the XML entity escaping applied by SecurityElement.Escape in BuildTaskXml.
                    // Decode &amp; last so an already-decoded "&" is not processed twice.
                    command = xml.Substring(start, end - start)
                        .Replace("&lt;", "<")
                        .Replace("&gt;", ">")
                        .Replace("&quot;", "\"")
                        .Replace("&apos;", "'")
                        .Replace("&amp;", "&")
                        .Trim();
                }
            }

            return true; // Task file exists; treat as registered even if the command did not parse.
        }
        catch
        {
            // File exists but is unreadable - report "exists" so we don't needlessly re-register
            // on every launch; if the command was needed and is null, the caller re-registers safely.
            return true;
        }
    }

    /// <summary>
    /// Checks if GameShift is currently registered to start with Windows via the scheduled task.
    /// </summary>
    public static bool IsRegisteredForStartup()
    {
        try
        {
            var (success, output) = RunSchtasks($"/Query /TN \"{FullTaskName}\"");
            return success && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[StartupManager] Failed to check Windows startup registration");
            return false;
        }
    }

    // ── Scheduled task registration ──────────────────────────────────

    // Task Scheduler 2.0 constants (taskschd.h).
    internal const int TaskCreateOrUpdate = 6;
    internal const int TaskLogonInteractiveToken = 3;

    private static void RegisterScheduledTask()
    {
        RegisterScheduledTask(
            new TaskSchedulerRegistrar(),
            GetExePath(),
            Environment.UserDomainName + "\\" + Environment.UserName);
    }

    /// <summary>
    /// Registers the startup task from the XML string directly, so no task file is written to a
    /// user-writable folder for the elevated process to import and delete.
    /// </summary>
    internal static bool RegisterScheduledTask(ITaskRegistrar registrar, string exePath, string userId)
    {
        if (!File.Exists(exePath))
        {
            Log.Warning("[StartupManager] GameShift executable not found at '{Path}', skipping startup registration", exePath);
            return false;
        }

        try
        {
            registrar.RegisterTask(FullTaskName, BuildTaskXml(exePath, userId), TaskCreateOrUpdate, TaskLogonInteractiveToken);
            Log.Information("[StartupManager] Registered GameShift for Windows startup via scheduled task");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[StartupManager] Failed to register startup task (0x{HResult:X8})", ex.HResult);
            return false;
        }
    }

    private static void UnregisterScheduledTask()
    {
        try
        {
            var (success, output) = RunSchtasks($"/Delete /TN \"{FullTaskName}\" /F");
            if (success)
                Log.Information("[StartupManager] Unregistered GameShift from Windows startup");
            else if (!output.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
                Log.Warning("[StartupManager] Failed to unregister startup task: {Output}", output);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[StartupManager] Failed to unregister startup task");
        }
    }

    internal static string BuildTaskXml(string exePath, string userId)
    {
        var escapedPath = global::System.Security.SecurityElement.Escape(exePath);
        var escapedUser = global::System.Security.SecurityElement.Escape(userId);
        var workingDir = global::System.Security.SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Launches GameShift at user logon with elevated privileges.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <UserId>{escapedUser}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{escapedUser}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedPath}</Command>
                  <WorkingDirectory>{workingDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (bool success, string output) RunSchtasks(string arguments)
    {
        try
        {
            var result = ProcessRunner.Run(NativeInterop.SystemExePath("schtasks.exe"), arguments, 30_000);
            if (result.TimedOut)
                return (false, "schtasks timed out");
            if (!result.Exited)
                return (false, "Failed to start schtasks.exe");

            var combined = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : $"{result.StdOut}\n{result.StdErr}";
            return (result.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Registers a task definition given as XML.</summary>
    internal interface ITaskRegistrar
    {
        /// <summary>Throws on failure, with the Task Scheduler HRESULT in <see cref="Exception.HResult"/>.</summary>
        void RegisterTask(string path, string xml, int flags, int logonType);
    }

    private sealed class TaskSchedulerRegistrar : ITaskRegistrar
    {
        public void RegisterTask(string path, string xml, int flags, int logonType)
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            // Null user, password and SDDL go out as VT_EMPTY, so the principal comes from the XML.
            service.GetFolder(@"\").RegisterTask(path, xml, flags, null, null, logonType, null);
        }
    }

    // ── Legacy HKCU\Run cleanup ──────────────────────────────────────

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            if (key?.GetValue(LegacyAppName) != null)
            {
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
                Log.Information("[StartupManager] Removed legacy HKCU\\Run entry");
            }

            using var approvedKey = Registry.CurrentUser.OpenSubKey(LegacyStartupApprovedKeyPath, writable: true);
            approvedKey?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[StartupManager] Failed to clean up legacy HKCU\\Run entry");
        }
    }
}
