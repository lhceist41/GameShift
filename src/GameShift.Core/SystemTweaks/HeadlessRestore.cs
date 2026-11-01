using System.Text;
using GameShift.Core.Config;
using GameShift.Core.Journal;
using GameShift.Core.Monitoring;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using GameShift.Core.Verification;
using Serilog;

namespace GameShift.Core.SystemTweaks;

/// <summary>Exit codes of a headless run (<see cref="LaunchOptions.IsHeadless"/>), read by the uninstaller.</summary>
public static class HeadlessExitCode
{
    /// <summary>Everything asked for was done and nothing GameShift recorded is left.</summary>
    public const int Done = 0;

    /// <summary>Something failed, was skipped, or could not be checked; the summary file says what.</summary>
    public const int SomethingLeft = 1;

    /// <summary>A step stopped with an unexpected error.</summary>
    public const int Error = 2;

    /// <summary>GameShift is running; nothing was changed.</summary>
    public const int AlreadyRunning = 3;
}

/// <summary>
/// The commands the uninstaller runs without a window: undo a crashed session and every lasting
/// change GameShift recorded for the account that runs it, and delete GameShift's scheduled tasks.
/// Nothing is reported done unless its reverter succeeded.
/// </summary>
public static class HeadlessRestore
{
    /// <summary>In the admin-only logs folder.</summary>
    public const string SummaryFileName = "restore-tweaks.txt";

    /// <summary>Runs the commands in <paramref name="options"/> on this PC and returns the exit code.</summary>
    public static int RunOnThisMachine(LaunchOptions options)
    {
        var logger = SettingsManager.Logger;
        return Run(
            options,
            TryAcquireSingleInstance,
            () => RestoreTweaks(
                () => StartupRecovery.Run(new JournalManager(), JournalTrust.Check, new WatchdogRevertEngine(logger), logger),
                () => LastingChangesInventory.CreateForThisMachine(new SystemTweaksManager(), new DpcFixEngine(), new VbsHvciToggle()),
                () => !SettingsManager.IsDataFolderRefused(),
                WriteSummaryToLogs,
                Environment.UserName,
                logger),
            () => StartupManager.RemoveAllTasks() ? HeadlessExitCode.Done : HeadlessExitCode.SomethingLeft,
            logger);
    }

    /// <param name="acquireInstanceLock">The app's single-instance lock, or null when GameShift holds it.</param>
    internal static int Run(
        LaunchOptions options,
        Func<IDisposable?> acquireInstanceLock,
        Func<int> restoreTweaks,
        Func<int> removeTasks,
        ILogger logger)
    {
        // Holding the app's lock for the whole run keeps GameShift from starting a session while
        // its changes are undone.
        using var instance = acquireInstanceLock();
        if (instance == null)
        {
            logger.Warning("[Headless] GameShift is running; nothing was changed");
            return HeadlessExitCode.AlreadyRunning;
        }

        var code = HeadlessExitCode.Done;
        if (options.RestoreTweaks)
            code = Math.Max(code, Guard(restoreTweaks, "Restoring lasting changes", logger));
        if (options.RemoveTasks)
            code = Math.Max(code, Guard(removeTasks, "Removing the scheduled tasks", logger));
        return code;
    }

    /// <summary>
    /// Undoes a crashed session the way the app start does, then every recorded lasting change.
    /// Changes that need the user's own confirmation are listed, never undone.
    /// </summary>
    internal static int RestoreTweaks(
        Func<StartupRecoveryResult> recover,
        Func<LastingChangesInventory> createInventory,
        Func<bool> settingsAvailable,
        Action<string> writeSummary,
        string accountName,
        ILogger logger)
    {
        var code = HeadlessExitCode.Done;
        var summary = new StringBuilder();
        summary.AppendLine($"GameShift restore for the Windows account \"{accountName}\", {DateTime.Now:yyyy-MM-dd HH:mm}.");
        summary.AppendLine("Changes recorded under other Windows accounts are not included.");
        summary.AppendLine();

        try
        {
            var recovery = recover();
            // NothingToDo still marks an active session when the journal was left alone (a version
            // this one does not replay), so only an inactive one counts as clean.
            var clean = recovery.Outcome == StartupRecoveryOutcome.Recovered ||
                        (recovery.Outcome == StartupRecoveryOutcome.NothingToDo && !recovery.WasSessionActive);
            if (!clean)
                code = Math.Max(code, HeadlessExitCode.SomethingLeft);
            summary.AppendLine(recovery.Notice ?? (clean
                ? "No unfinished optimization session was found."
                : "An unfinished optimization session was found, but this version could not undo it."));
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[Headless] Crash recovery failed");
            code = Math.Max(code, HeadlessExitCode.Error);
            summary.AppendLine($"Undoing an unfinished optimization session stopped with an error: {ex.Message}");
        }
        summary.AppendLine();

        try
        {
            if (!settingsAvailable())
            {
                code = Math.Max(code, HeadlessExitCode.SomethingLeft);
                summary.AppendLine("GameShift's settings folder could not be used, so the changes it recorded could not be read.");
            }

            var inventory = createInventory();
            var list = inventory.List();
            foreach (var warning in list.Warnings)
            {
                code = Math.Max(code, HeadlessExitCode.SomethingLeft);
                summary.AppendLine(warning);
            }

            IReadOnlyList<LastingChangeResult> results = list.Recorded.Count > 0 ? inventory.Undo(list.Recorded) : [];
            if (results.Any(r => r.Outcome != LastingChangeOutcome.Undone))
                code = Math.Max(code, HeadlessExitCode.SomethingLeft);
            if (results.Count > 0)
                summary.AppendLine(LastingChangesInventory.DescribeResults(results));

            var skipped = list.NeedingOwnConfirmation;
            if (skipped.Count > 0)
            {
                code = Math.Max(code, HeadlessExitCode.SomethingLeft);
                summary.AppendLine("Not undone, because GameShift cannot tell whether it made them. Undo them with Restore All in GameShift, or yourself:");
                summary.Append(LastingChangesInventory.DescribeItems(skipped));
            }

            if (list.Items.Count == 0)
                summary.AppendLine("GameShift found no lasting changes to undo.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[Headless] Undoing the lasting changes failed");
            code = Math.Max(code, HeadlessExitCode.Error);
            summary.AppendLine($"Undoing the lasting changes stopped with an error: {ex.Message}");
        }

        var text = summary.ToString();
        logger.Information("[Headless] Restore finished with exit code {Code}:{NewLine}{Summary}", code, Environment.NewLine, text);
        try
        {
            writeSummary(text);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[Headless] Could not write the restore summary");
        }

        return code;
    }

    private static int Guard(Func<int> command, string what, ILogger logger)
    {
        try
        {
            return command();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[Headless] {What} failed", what);
            return HeadlessExitCode.Error;
        }
    }

    private static IDisposable? TryAcquireSingleInstance()
    {
        var mutex = new Mutex(true, RevertVerificationRunner.SingleInstanceMutexName, out var createdNew);
        if (createdNew)
            return new OwnedMutex(mutex);

        mutex.Dispose();
        return null;
    }

    // Path writes in the logs folder are safe: it is admin-only and held open without delete
    // sharing once prepared (LogFolder).
    private static void WriteSummaryToLogs(string text)
    {
        if (!SettingsManager.IsLogsFolderReady())
        {
            SettingsManager.Logger.Warning("[Headless] The logs folder is not available; the restore summary was not written");
            return;
        }

        File.WriteAllText(Path.Combine(SettingsManager.GetLogsPath(), SummaryFileName), text);
    }

    private sealed class OwnedMutex(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
