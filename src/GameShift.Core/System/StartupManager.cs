using GameShift.Core.Config;
using GameShift.Core.Updates;
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
/// Action     : Launch GameShift.App.exe --logon
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

    /// <summary>Passed by the startup task, so the app can tell a logon start from one the user made.</summary>
    public const string LogonArgument = "--logon";

    // A SYSTEM boot task earlier versions could register for a watchdog that releases never shipped.
    internal const string LegacyBootRecoveryTaskName = "BootRecovery";

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
    /// A copy running outside the registered install folder leaves the task to the installed copy.
    /// </summary>
    /// <param name="enable">true to register for startup, false to unregister</param>
    public static void SetStartWithWindows(bool enable)
    {
        // Always clean up the legacy HKCU\Run entry - it never worked for elevated apps
        // and would just leave dead entries in Task Manager's Startup tab.
        RemoveLegacyRunEntry();

        SetStartWithWindows(enable, GetExePath(), InstallationDetector.Current.RegisteredInstallDirectory,
            ReadRegisteredTask, new TaskSchedulerRegistrar(), CurrentUserId());
    }

    internal static void SetStartWithWindows(
        bool enable, string exePath, string? registeredInstallDirectory,
        Func<RegisteredStartupTask?> readRegistered, ITaskRegistrar registrar, string userId)
    {
        var installFolder = InstallFolderKeepingStartup(exePath, registeredInstallDirectory);
        if (installFolder == null)
        {
            if (enable)
                RegisterScheduledTask(registrar, exePath, userId);
            else
                UnregisterScheduledTask(registrar);
            return;
        }

        // The installed copy's task is never deleted from here; one that runs this exe (registered by an
        // older version) is this copy's own and may go when startup is turned off.
        if (!enable && RunsExe(readRegistered()?.Command, exePath))
        {
            UnregisterScheduledTask(registrar);
            return;
        }

        Log.Information("[StartupManager] Startup task left unchanged: GameShift is installed in {InstallFolder} and this copy runs from elsewhere",
            installFolder);
    }

    /// <summary>
    /// The registered install folder when this copy runs from elsewhere, so the startup task is the
    /// installed copy's to change; null when this copy may change it.
    /// </summary>
    public static string? InstallFolderKeepingStartup() =>
        InstallFolderKeepingStartup(GetExePath(), InstallationDetector.Current.RegisteredInstallDirectory);

    internal static string? InstallFolderKeepingStartup(string? exePath, string? registeredInstallDirectory) =>
        RunsFromRegisteredInstall(exePath, registeredInstallDirectory) ? null : registeredInstallDirectory;

    /// <summary>
    /// Launch-time <see cref="ReconcileStartupRegistration"/> that follows only a saved setting.
    /// Without a settings file the first-run wizard registers the user's choice; with a refused data
    /// folder the settings in hand are defaults, so the task is left as it is. A copy running outside
    /// the registered install folder leaves the task to the installed copy.
    /// </summary>
    public static void ReconcileAtLaunch(AppSettings settings, string? registeredInstallDirectory) =>
        ReconcileAtLaunch(settings, Environment.ProcessPath, registeredInstallDirectory, ReconcileStartupRegistration);

    internal static void ReconcileAtLaunch(
        AppSettings settings, string? exePath, string? registeredInstallDirectory, Action<bool> reconcile)
    {
        if (!RunsFromRegisteredInstall(exePath, registeredInstallDirectory))
        {
            Log.Information("[StartupManager] Startup task left unchanged: GameShift is installed in {InstallFolder} and this copy runs from elsewhere",
                registeredInstallDirectory);
            return;
        }

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
    /// True when no install is registered, or the exe runs from the registered install folder. Compares
    /// folders, not the install mode, so an installed copy keeps its task whatever decides that mode.
    /// </summary>
    internal static bool RunsFromRegisteredInstall(string? exePath, string? registeredInstallDirectory)
    {
        var installDir = InstallationDetector.NormalizeDirectory(registeredInstallDirectory);
        if (installDir == null)
            return true;

        string? exeDir;
        try { exeDir = InstallationDetector.NormalizeDirectory(Path.GetDirectoryName(exePath)); }
        catch { exeDir = null; }

        return string.Equals(exeDir, installDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Startup-time reconciliation of the Windows startup registration.
    ///
    /// Unlike <see cref="SetStartWithWindows"/> (which always goes to the Task Scheduler and is
    /// meant for explicit user toggles), this runs on every launch and avoids that work
    /// unless something actually needs to change. It reads the Task Scheduler's on-disk task
    /// definition directly and only (re)registers when the task is missing, points at a
    /// different executable path (e.g. after the app was moved or updated), or lacks
    /// <see cref="LogonArgument"/>.
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

            ReconcileStartupTask(enable, ReadRegisteredTask(), new TaskSchedulerRegistrar(), GetExePath(), CurrentUserId());
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
    /// <see cref="ReconcileStartupRegistration"/>'s decision for the task as registered (null when there
    /// is none): registers when it is wanted and not up to date, deletes it when it is not wanted.
    /// </summary>
    internal static void ReconcileStartupTask(
        bool enable, RegisteredStartupTask? registered, ITaskRegistrar registrar, string exePath, string userId)
    {
        if (enable)
        {
            if (!IsUpToDate(registered, exePath))
                RegisterScheduledTask(registrar, exePath, userId);
        }
        else if (registered != null)
        {
            UnregisterScheduledTask(registrar);
        }
    }

    private static bool IsUpToDate(RegisteredStartupTask? registered, string exePath) =>
        RunsExe(registered?.Command, exePath)
        // A task registered before the logon argument existed is updated once to carry it.
        && string.Equals(registered!.Arguments?.Trim(), LogonArgument, StringComparison.OrdinalIgnoreCase);

    private static bool RunsExe(string? command, string exePath) =>
        command != null
        && string.Equals(command.Trim().Trim('"'), exePath.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);

    /// <summary>What the registered startup task runs; a part that could not be read is null.</summary>
    internal sealed record RegisteredStartupTask(string? Command, string? Arguments);

    /// <summary>
    /// Reads the GameShift\Startup task directly from the Task Scheduler's on-disk definition
    /// (%windir%\System32\Tasks\GameShift\Startup), avoiding a schtasks.exe spawn. Null when the task
    /// does not exist; an unreadable file counts as a task whose parts are unknown, so it is replaced.
    /// </summary>
    private static RegisteredStartupTask? ReadRegisteredTask()
    {
        var taskFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "Tasks", TaskFolder, TaskName);

        if (!File.Exists(taskFile))
            return null;

        try
        {
            return ParseRegisteredTask(File.ReadAllText(taskFile));
        }
        catch
        {
            return new RegisteredStartupTask(null, null);
        }
    }

    /// <summary>Reads &lt;Command&gt; and &lt;Arguments&gt; from a task definition.</summary>
    internal static RegisteredStartupTask ParseRegisteredTask(string xml) =>
        new(ReadElement(xml, "Command"), ReadElement(xml, "Arguments"));

    private static string? ReadElement(string xml, string name)
    {
        var openTag = $"<{name}>";
        var closeTag = $"</{name}>";
        var start = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += openTag.Length;
        var end = xml.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < start)
            return null;

        // Reverse the XML entity escaping applied by SecurityElement.Escape in BuildTaskXml.
        // Decode &amp; last so an already-decoded "&" is not processed twice.
        return xml.Substring(start, end - start)
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&amp;", "&")
            .Trim();
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

    private static string CurrentUserId() => Environment.UserDomainName + "\\" + Environment.UserName;

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

    // HRESULT_FROM_WIN32 of ERROR_FILE_NOT_FOUND (no such task) and ERROR_PATH_NOT_FOUND (no such folder).
    internal const int TaskNotFound = unchecked((int)0x80070002);
    internal const int TaskFolderNotFound = unchecked((int)0x80070003);

    /// <summary>
    /// Deletes the startup task through the Task Scheduler API. A task or folder that does not exist
    /// is recognised by its HRESULT, which unlike schtasks output does not depend on the display language.
    /// </summary>
    internal static void UnregisterScheduledTask(ITaskRegistrar registrar)
    {
        try
        {
            registrar.DeleteTask(TaskFolder, TaskName);
            Log.Information("[StartupManager] Unregistered GameShift from Windows startup");
        }
        catch (Exception ex) when (ex.HResult is TaskNotFound or TaskFolderNotFound)
        {
            // Not registered, so there is nothing to remove.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[StartupManager] Failed to unregister startup task (0x{HResult:X8})", ex.HResult);
        }
    }

    /// <summary>
    /// Deletes the legacy GameShift\BootRecovery task, then the GameShift task folder if that left it
    /// empty. Never throws.
    /// </summary>
    public static void DeleteLegacyBootRecoveryTask() => DeleteLegacyBootRecoveryTask(new TaskSchedulerRegistrar());

    internal static void DeleteLegacyBootRecoveryTask(ITaskRegistrar registrar)
    {
        try
        {
            registrar.DeleteTask(TaskFolder, LegacyBootRecoveryTaskName);
            Log.Information("[StartupManager] Removed the legacy boot-recovery task");
        }
        catch (Exception ex) when (ex.HResult is TaskNotFound or TaskFolderNotFound)
        {
            // Never registered on this PC, which is the usual case.
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[StartupManager] Failed to remove the legacy boot-recovery task (0x{HResult:X8})", ex.HResult);
            return;
        }

        DeleteTaskFolderIfEmpty(registrar);
    }

    /// <summary>
    /// Deletes the startup task and the legacy boot-recovery task, then the GameShift task folder if
    /// that left it empty. True when neither task is left; a task that does not exist counts as
    /// removed. Never throws.
    /// </summary>
    public static bool RemoveAllTasks() => RemoveAllTasks(new TaskSchedulerRegistrar());

    internal static bool RemoveAllTasks(ITaskRegistrar registrar)
    {
        var startupGone = TryDeleteTask(registrar, TaskName);
        var legacyGone = TryDeleteTask(registrar, LegacyBootRecoveryTaskName);
        DeleteTaskFolderIfEmpty(registrar);
        return startupGone && legacyGone;
    }

    private static bool TryDeleteTask(ITaskRegistrar registrar, string name)
    {
        try
        {
            registrar.DeleteTask(TaskFolder, name);
            Log.Information("[StartupManager] Removed the {Task} task", TaskFolder + "\\" + name);
            return true;
        }
        catch (Exception ex) when (ex.HResult is TaskNotFound or TaskFolderNotFound)
        {
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[StartupManager] Failed to remove the {Task} task (0x{HResult:X8})", TaskFolder + "\\" + name, ex.HResult);
            return false;
        }
    }

    /// <summary>Deletes the GameShift task folder when it holds no task or folder. Never throws.</summary>
    internal static void DeleteTaskFolderIfEmpty(ITaskRegistrar registrar)
    {
        try
        {
            if (registrar.DeleteFolderIfEmpty(TaskFolder))
                Log.Information("[StartupManager] Removed the empty GameShift task folder");
        }
        catch (Exception ex) when (ex.HResult is TaskNotFound or TaskFolderNotFound)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[StartupManager] Failed to remove the GameShift task folder (0x{HResult:X8})", ex.HResult);
        }
    }

    internal static string BuildTaskXml(string exePath, string userId)
    {
        var escapedPath = global::System.Security.SecurityElement.Escape(exePath);
        var escapedArguments = global::System.Security.SecurityElement.Escape(LogonArgument);
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
                  <Arguments>{escapedArguments}</Arguments>
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

    /// <summary>Registers a task definition given as XML, and deletes a task or an empty task folder.</summary>
    internal interface ITaskRegistrar
    {
        /// <summary>Throws on failure, with the Task Scheduler HRESULT in <see cref="Exception.HResult"/>.</summary>
        void RegisterTask(string path, string xml, int flags, int logonType);

        /// <summary>Throws on failure, with the Task Scheduler HRESULT in <see cref="Exception.HResult"/>.</summary>
        void DeleteTask(string folder, string name);

        /// <summary>
        /// Deletes a top-level task folder and returns true, or returns false when it still holds a task or
        /// folder. Throws on failure, with the Task Scheduler HRESULT in <see cref="Exception.HResult"/>.
        /// </summary>
        bool DeleteFolderIfEmpty(string folder);
    }

    private sealed class TaskSchedulerRegistrar : ITaskRegistrar
    {
        public void RegisterTask(string path, string xml, int flags, int logonType)
        {
            // Null user, password and SDDL go out as VT_EMPTY, so the principal comes from the XML.
            Connect().GetFolder(@"\").RegisterTask(path, xml, flags, null, null, logonType, null);
        }

        public void DeleteTask(string folder, string name)
        {
            Connect().GetFolder(@"\" + folder).DeleteTask(name, 0);
        }

        public bool DeleteFolderIfEmpty(string folder)
        {
            var service = Connect();
            var target = service.GetFolder(@"\" + folder);
            // TASK_ENUM_HIDDEN, so a hidden task counts as content too.
            if (target.GetTasks(1).Count > 0 || target.GetFolders(0).Count > 0)
                return false;

            service.GetFolder(@"\").DeleteFolder(folder, 0);
            return true;
        }

        private static dynamic Connect()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return service;
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
