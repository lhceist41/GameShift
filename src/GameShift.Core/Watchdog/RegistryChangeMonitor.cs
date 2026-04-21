using System.Text.Json;
using GameShift.Core.Journal;
using GameShift.Core.Optimization;
using GameShift.Core.System;
using Microsoft.Win32;
using Serilog;

namespace GameShift.Core.Watchdog;

/// <summary>
/// Monitors a fixed set of registry values that GameShift modifies during active gaming sessions.
/// When an external process changes one of these values, logs a warning with the before/after
/// values and optionally restores GameShift's value if the owning optimization is still
/// <c>Applied</c> in the session journal.
///
/// Uses one named background thread per watched key. Each thread calls
/// <see cref="NativeInterop.RegNotifyChangeKeyValue"/> in asynchronous mode and waits on a
/// <see cref="WaitHandle.WaitAny"/> with both the registry-change event and a stop event,
/// so the threads exit immediately on <see cref="StopSession"/> without a 15-second poll gap.
///
/// <para>Lifecycle:</para>
/// <list type="bullet">
///   <item><see cref="StartSession"/> — called by <see cref="OptimizationEngine"/> after all
///         optimizations are applied.</item>
///   <item><see cref="StopSession"/> — called by <see cref="OptimizationEngine"/> before
///         reverting optimizations.</item>
/// </list>
/// </summary>
public sealed class RegistryChangeMonitor : IDisposable
{
    // ── Watched key descriptors ───────────────────────────────────────────────

    private sealed record WatchedKey(
        /// <summary>Registry path relative to HKLM (no leading backslash).</summary>
        string KeyPath,
        /// <summary>The specific value name within that key to monitor.</summary>
        string ValueName,
        /// <summary>
        /// Name matching a <see cref="IJournaledOptimization.Name"/> entry in the session journal.
        /// Used to look up the <c>AppliedValue</c> JSON for re-apply.
        /// Null when the owning optimization is not yet journal-migrated (warning only).
        /// </summary>
        string? OptimizationName,
        /// <summary>
        /// Key inside the <c>AppliedValue</c> JSON dict that holds the expected DWORD.
        /// Must be non-null when <see cref="OptimizationName"/> is non-null.
        /// </summary>
        string? JsonKey
    );

    private static readonly WatchedKey[] KnownKeys =
    [
        new(
            KeyPath:          @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
            ValueName:        "GlobalTimerResolutionRequests",
            OptimizationName: TimerResolutionManager.OptimizationId,
            JsonKey:          null   // TimerResolutionManager not yet journal-migrated → warn only
        ),
        new(
            KeyPath:          @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            ValueName:        "DisableOverlays",
            OptimizationName: MpoToggle.OptimizationId,
            JsonKey:          "DisableOverlays"
        ),
        new(
            KeyPath:          @"SOFTWARE\Microsoft\Windows\Dwm",
            ValueName:        "OverlayTestMode",
            OptimizationName: MpoToggle.OptimizationId,
            JsonKey:          "OverlayTestMode"
        ),
        new(
            KeyPath:          @"SYSTEM\CurrentControlSet\Control\PriorityControl",
            ValueName:        "Win32PrioritySeparation",
            OptimizationName: ProcessPriorityBooster.OptimizationId,
            JsonKey:          null   // Win32PrioritySeparation is snapshot-only, not in journaled state → warn only
        ),
    ];

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly JournalManager _journal;
    private readonly ILogger _logger;

    // Signals all watcher threads to stop; replaced each StartSession call.
    private ManualResetEvent? _stopEvent;
    private readonly List<Thread> _threads = new();
    private volatile bool _disposed;

    public RegistryChangeMonitor(JournalManager journal, ILogger logger)
    {
        _journal = journal;
        _logger  = logger.ForContext<RegistryChangeMonitor>();
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Starts one background watcher thread per <see cref="KnownKeys"/> entry.
    /// Safe to call repeatedly — stops any previous session first.
    /// </summary>
    public void StartSession()
    {
        StopSession(); // clean up any previous run

        _stopEvent = new ManualResetEvent(false);
        _threads.Clear();

        foreach (var key in KnownKeys)
        {
            var captured = key;
            var thread = new Thread(() => WatchKeyLoop(captured, _stopEvent))
            {
                Name        = $"GameShift_RegWatch_{captured.ValueName}",
                IsBackground = true,
                Priority     = ThreadPriority.BelowNormal,
            };
            _threads.Add(thread);
            thread.Start();
        }

        _logger.Information(
            "[RegistryChangeMonitor] Started {Count} key watcher(s)",
            _threads.Count);
    }

    /// <summary>
    /// Signals all watcher threads to exit and waits up to 2 seconds for each.
    /// </summary>
    public void StopSession()
    {
        if (_stopEvent == null)
            return;

        _logger.Information("[RegistryChangeMonitor] Stopping registry watchers...");
        _stopEvent.Set();

        foreach (var t in _threads)
        {
            if (!t.Join(2_000))
                _logger.Warning("[RegistryChangeMonitor] Watcher thread '{Name}' did not exit in time", t.Name);
        }

        _stopEvent.Dispose();
        _stopEvent = null;
        _threads.Clear();
        _logger.Debug("[RegistryChangeMonitor] All watchers stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSession();
    }

    // ── Per-key watch loop ────────────────────────────────────────────────────

    /// <summary>
    /// Runs on a dedicated background thread. Opens the registry key, arms
    /// <see cref="NativeInterop.RegNotifyChangeKeyValue"/> in async mode, then waits on
    /// either the registry-change event or the stop event.
    /// Re-arms after each change until the stop event fires.
    /// </summary>
    private void WatchKeyLoop(WatchedKey key, ManualResetEvent stopEvent)
    {
        RegistryKey? regKey    = null;
        EventWaitHandle? evt   = null;

        try
        {
            regKey = Registry.LocalMachine.OpenSubKey(key.KeyPath, writable: false);
            if (regKey == null)
            {
                _logger.Debug(
                    "[RegistryChangeMonitor] Key not found — skipping watcher: HKLM\\{Path}",
                    key.KeyPath);
                return;
            }

            evt = new EventWaitHandle(false, EventResetMode.AutoReset);
            var hKey   = regKey.Handle.DangerousGetHandle();
            var hEvent = evt.SafeWaitHandle.DangerousGetHandle();

            _logger.Debug(
                "[RegistryChangeMonitor] Watching HKLM\\{Path}\\{Value}",
                key.KeyPath, key.ValueName);

            var handles = new WaitHandle[] { evt, stopEvent };

            while (true)
            {
                // Arm one-shot async notification for value changes in this key
                int rc = NativeInterop.RegNotifyChangeKeyValue(
                    hKey,
                    bWatchSubtree: false,
                    NativeInterop.RegNotifyFilter.LastSet,
                    hEvent,
                    fAsynchronous: true);

                if (rc != 0)
                {
                    _logger.Warning(
                        "[RegistryChangeMonitor] RegNotifyChangeKeyValue failed for HKLM\\{Path} " +
                        "(error {Code}) — watcher disabled for this key",
                        key.KeyPath, rc);
                    return;
                }

                // Block until registry changed OR stop requested
                int idx = WaitHandle.WaitAny(handles);

                if (idx == 1) // stopEvent
                    return;

                // idx == 0 → registry change notification fired
                if (!_disposed)
                    HandleChange(key, regKey);

                // Loop to re-arm for the next change
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected when StopSession disposes handles during the WaitAny
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[RegistryChangeMonitor] Watcher for HKLM\\{Path}\\{Value} crashed",
                key.KeyPath, key.ValueName);
        }
        finally
        {
            evt?.Dispose();
            regKey?.Dispose();
        }
    }

    // ── Change handler ────────────────────────────────────────────────────────

    /// <summary>
    /// Called on the watcher thread immediately after a registry change notification fires.
    /// Reads the new value, logs the warning, and optionally restores GameShift's value.
    /// </summary>
    private void HandleChange(WatchedKey key, RegistryKey regKey)
    {
        object? currentRaw;
        try
        {
            currentRaw = regKey.GetValue(key.ValueName);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[RegistryChangeMonitor] Could not read HKLM\\{Path}\\{Value} after change notification",
                key.KeyPath, key.ValueName);
            return;
        }

        _logger.Warning(
            "[RegistryChangeMonitor] External change: HKLM\\{Path}\\{Value} = {Current} " +
            "(GameShift modification may have been reverted by another process)",
            key.KeyPath,
            key.ValueName,
            currentRaw ?? "<deleted>");

        // Re-apply only when we have enough information: the key maps to a journal-migrated
        // optimization AND the optimization is currently Applied in the session journal.
        if (key.OptimizationName == null || key.JsonKey == null)
        {
            _logger.Debug(
                "[RegistryChangeMonitor] No re-apply configured for {Value} — warning only",
                key.ValueName);
            return;
        }

        TryReApply(key, currentRaw);
    }

    private void TryReApply(WatchedKey key, object? currentRaw)
    {
        try
        {
            // Load journal from disk to get the current session's applied values
            var journalData = _journal.LoadJournal();
            if (journalData == null)
            {
                _logger.Debug("[RegistryChangeMonitor] No journal — skipping re-apply");
                return;
            }

            // Find the most recently Applied entry for this optimization
            JournalEntry? entry = null;
            for (int i = journalData.Optimizations.Count - 1; i >= 0; i--)
            {
                var e = journalData.Optimizations[i];
                if (e.Name == key.OptimizationName &&
                    e.State == nameof(OptimizationState.Applied))
                {
                    entry = e;
                    break;
                }
            }

            if (entry == null)
            {
                _logger.Information(
                    "[RegistryChangeMonitor] Optimization '{Opt}' is not Applied — not re-applying {Value}",
                    key.OptimizationName, key.ValueName);
                return;
            }

            // Parse the expected value from the journal's AppliedValue JSON
            if (string.IsNullOrEmpty(entry.AppliedValue))
            {
                _logger.Debug("[RegistryChangeMonitor] Applied entry for '{Opt}' has no AppliedValue JSON",
                    key.OptimizationName);
                return;
            }

            var appliedDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entry.AppliedValue);
            if (appliedDict == null || !appliedDict.TryGetValue(key.JsonKey!, out var expectedElem))
            {
                _logger.Debug(
                    "[RegistryChangeMonitor] Key '{JsonKey}' not found in AppliedValue for '{Opt}'",
                    key.JsonKey, key.OptimizationName);
                return;
            }

            if (expectedElem.ValueKind != JsonValueKind.Number)
                return;

            int expectedInt = expectedElem.GetInt32();

            // Skip if the current value already matches (another watcher iteration or our own write)
            if (currentRaw is int currentInt && currentInt == expectedInt)
            {
                _logger.Debug(
                    "[RegistryChangeMonitor] {Value} already at expected {Expected} — no action needed",
                    key.ValueName, expectedInt);
                return;
            }

            _logger.Warning(
                "[RegistryChangeMonitor] Re-applying HKLM\\{Path}\\{Value} = {Expected} " +
                "(reverted from {Current})",
                key.KeyPath, key.ValueName, expectedInt, currentRaw ?? "<deleted>");

            using var writeKey = Registry.LocalMachine.OpenSubKey(key.KeyPath, writable: true);
            if (writeKey == null)
            {
                _logger.Warning(
                    "[RegistryChangeMonitor] Cannot open HKLM\\{Path} for writing — re-apply skipped",
                    key.KeyPath);
                return;
            }

            writeKey.SetValue(key.ValueName, expectedInt, RegistryValueKind.DWord);
            _logger.Information(
                "[RegistryChangeMonitor] Re-applied HKLM\\{Path}\\{Value} = {Expected}",
                key.KeyPath, key.ValueName, expectedInt);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "[RegistryChangeMonitor] Re-apply failed for HKLM\\{Path}\\{Value}",
                key.KeyPath, key.ValueName);
        }
    }
}
