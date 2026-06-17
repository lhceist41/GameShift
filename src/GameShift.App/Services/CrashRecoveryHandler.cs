using System;
using System.IO;
using Serilog;

namespace GameShift.App.Services;

/// <summary>
/// Startup cleanup for state a previous run may have left behind: an orphaned ETW DPC-trace
/// session and leftover auto-update artifacts. Also ensures the per-user GameShift data
/// directory exists.
///
/// Reverting optimizations after a crash, BSOD, or power loss is handled by the journal-based
/// recovery path (the boot-recovery task / watchdog reading
/// <c>%ProgramData%\GameShift\state.json</c> via <see cref="GameShift.Core.Journal.WatchdogRevertEngine"/>),
/// not here. The earlier snapshot/lockfile recovery (<c>active_session.json</c>) was removed when
/// the journal became the single source of truth - nothing writes that lockfile anymore.
/// </summary>
public static class CrashRecoveryHandler
{
    /// <summary>
    /// Ensures the per-user data directory exists, stops any orphaned GameShift ETW DPC-trace
    /// session left by a previous crash, and removes leftover update artifacts.
    /// </summary>
    public static void RecoverIfNeeded(string gameshiftPath)
    {
        // Ensure the GameShift AppData directory exists (used for the startup diagnostic log).
        try
        {
            if (!Directory.Exists(gameshiftPath))
                Directory.CreateDirectory(gameshiftPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create GameShift data directory at {Path}", gameshiftPath);
        }

        // Clean up an orphaned ETW DPC trace session from a previous crash.
        try
        {
            var zombieSession = Microsoft.Diagnostics.Tracing.Session.TraceEventSession
                .GetActiveSession("GameShift-DPC-Trace");
            if (zombieSession != null)
            {
                zombieSession.Stop();
                zombieSession.Dispose();
                Log.Information("Cleaned up orphaned DPC monitoring ETW session");
            }
        }
        catch { /* Best-effort cleanup -- session may not exist */ }

        // Clean up leftover update artifacts from a previous auto-update.
        GameShift.Core.Updates.UpdateApplier.CleanupPreviousUpdate();
    }
}
