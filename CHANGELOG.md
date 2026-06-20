# Changelog

All notable changes to GameShift are documented here.

## [Unreleased]

### Fixed

- **P-core affinity is correct on CPUs with more than 64 logical processors** - the Intel hybrid P-core detector built its P-core affinity mask without checking the processor group or bounding the logical-processor index. On machines with multiple processor groups (more than 64 logical processors) the mask bits wrapped around and collided (`1 << 64` is `1 << 0`), producing a wrong P-core mask. It now sets a bit only for an in-range logical processor in group 0, mirroring the guard already used by the main CPU-topology detector; the P/E logical-processor counts are unchanged.
- **Concurrent writers can no longer clobber the crash-recovery journal** - the session journal (`%ProgramData%\GameShift\state.json`) is written by several components, each from its own in-memory copy: the optimization engine (active game session), the kernel-tuning and core-isolation tweaks, and the startup reader (pending reboot fixes / Windows-update warning). A whole-file write by one could overwrite the half owned by another - e.g. recording a pending reboot fix while a game was running could wipe the active session, and continuing a session could drop a pending reboot warning. Each save now re-reads the journal under the shared lock and merges back the half it does not own, so session state and recovery metadata are preserved independently. A pending reboot warning also now survives a game launching before you reboot.
- **The Log Viewer no longer stutters the UI** - it re-read and re-filtered the entire log file on the UI thread every 3 seconds, which could hitch the window on a large log. The read and filter now run on a background thread and only update the view when done, coalescing overlapping refreshes.
- **One-time game tips are recorded without racing other settings writes** - dismissing a one-time tip happens on the detection thread during a game, and previously used a separate load-then-save that could lose (or be lost to) a concurrent settings write. It now uses a transactional, lock-guarded settings update.
- **Two session optimizers no longer fight over the same MMCSS registry values** - both the Network Optimizer and Session System Tweaks wrote the multimedia `SystemResponsiveness` and `NetworkThrottlingIndex` values to the same key, each capturing its own pre-session value. Whichever ran second captured the first's already-optimized value as the baseline, so a clean revert depended on the exact apply/revert order and could break under crash recovery or a partial revert, leaving those values changed after a game closed or overwriting a persistent MMCSS tweak you had enabled. Session System Tweaks (which always runs) is now the single session owner of both values and the Network Optimizer no longer writes them; the values set during a session are identical, so there is no behaviour change while a game runs, but the revert is now correct regardless of module order.
- **External command-line tools can no longer hang an optimization or be left running** - GameShift runs Windows tools (powercfg, netsh, schtasks, bcdedit, fsutil, sc, PowerShell) in roughly seventeen places, each reading the tool's output. Most read standard output synchronously on the calling thread with no timeout, so a tool that wedged without exiting could block that thread indefinitely; a few also read the exit code without first confirming the process had exited, which on a timeout left the tool running (orphaned) and reported a misleading error. These now share one runner that drains both output streams off the calling thread, enforces the per-call timeout, and terminates the whole process tree if the tool wedges - so a stuck system tool can no longer hang an apply/revert or be orphaned. Output, exit codes, and timeouts are unchanged for the normal (fast-exiting) case.
- **Settings changes no longer overwrite one another** - the tray Quick Switch, the DPC Doctor page, and the DPC fix engine each held a copy of all settings taken at startup and wrote the whole copy back when changing a single value, which could silently discard unrelated settings changed elsewhere since launch. The DPC Doctor and the DPC fix engine additionally held two separate copies that could drift apart and overwrite each other's applied-fix and pending-reboot tracking. They now read settings fresh and persist each change transactionally - one field at a time, under a shared lock - so concurrent settings changes are preserved and the two DPC components can no longer disagree about which fixes are applied.
- **Suppressed services are reliably restarted after a game** - when GameShift stopped non-essential services for a session, the revert issued a single start with a 10-second wait. A slow service, notably SysMain (SuperFetch), can take longer than that to reach Running, and Windows can abort a start that exceeds its wait hint under the load of many services restarting at once, leaving the service stopped until the next reboot. The revert now retries the start with a longer per-service wait, so a slow or briefly-aborted service is brought back up before the session revert completes.
- **Two session optimizers no longer fight over the PCIe ASPM power setting** - both the power-plan switcher and Session System Tweaks wrote PCIe Active State Power Management to the active power plan, each capturing its own pre-session value. Because the power-plan switcher runs first, the always-on tweak captured the already-disabled value as its baseline, so (exactly like the MMCSS values) a clean revert depended on module order and a partial revert could leave PCIe ASPM disabled after a game closed. Session System Tweaks (which always runs) is now the single session owner of PCIe ASPM and the power-plan switcher no longer writes it; the value set during a session is unchanged, but the revert is now correct regardless of module order.

### Changed

- **Removed dead snapshot/lockfile crash-recovery code** - the older snapshot-based recovery path (an `active_session.json` lockfile) stopped being written when the atomic state journal became the single source of truth, so the startup handler that read it could never fire. Removed that unreachable lockfile reader and restore code (and the stale code comments that referenced it). Crash-recovery behaviour is unchanged - it is handled by the journal-based path, which remains in development and not enabled in the current build, as the README documents.

## [3.8.6] - 2026-06-17

### Security

- **Updates are cryptographically verified before they are applied** - the auto-updater now checks the downloaded file's size and SHA-256 against the digest published by the GitHub release API, and refuses to apply anything it cannot verify (fail closed). The staged update is re-verified again, against a hash sidecar, immediately before it replaces the running executable. Previously the downloaded binary was applied over the elevated app with only a host-name check, so a corrupted or tampered release asset could have run with full privileges.
- **Locked down the crash-recovery journal directory** - `%ProgramData%\GameShift` is now restricted to SYSTEM and Administrators (full control) and standard users (read-only), instead of inheriting the default permissions that let any user add files. This prevents a standard user from planting or tampering with the state journal that the elevated app and the recovery task act on.

### Fixed

- **A game already running when GameShift starts is now detected and optimized** - detection previously only reacted to games launched after GameShift was running, so a game started first got no profile or optimizations for the whole session. GameShift now scans already-running processes at startup. Detection is also idempotent, so a game can never be double-counted.
- **Per-game settings no longer leak between games** - the default profile was shared by reference, so anti-cheat handling or other per-session state from one game could carry into the next. Each game now gets its own copy.
- **Game profiles are saved atomically** - a profile is written to a temp file and moved into place, so a crash or full disk mid-save can no longer truncate it (a truncated profile was silently discarded, reverting your tuned settings to default).
- **All optimization modules are shown** - the Optimizations page listed only 11 of the 17 modules; the six newer modules now appear with live status.
- **The first-run wizard no longer freezes** - the initial game-library scan runs off the UI thread.
- **DPC Doctor stops its kernel trace when you leave the page** - a manually started capture no longer keeps a system-wide ETW trace running in the background.
- **System tools can't hang an optimization** - power-plan and scheduled-task helpers now time out and kill a wedged child process instead of blocking or throwing.
- **The System page refresh is robust** - it no longer overlaps concurrent refreshes or gets stuck on "Loading" if a hardware query fails.
- **Diagnostic log moved to %AppData%** - the startup diagnostic log is written under `%AppData%\GameShift` instead of the install directory.

### Changed

- **Documentation accuracy** - the README no longer advertises the watchdog service and boot-recovery task as shipping features. Automatic recovery after an app crash or blue screen is in development and not enabled in the current build; GameShift reverts changes when your game exits, backed by an atomic state journal.

## [3.8.5] - 2026-06-15

### Fixed

- **No leftover scheduling value when a priority boost can't be applied** - if the per-game priority boost couldn't be applied to the running process (its process ID had been recycled, the process exited at the instant of launch, or an anti-cheat blocked the write), GameShift reported the optimization as failed even though it had already set the system-wide Win32PrioritySeparation scheduling value first. A failed optimization is never reverted, so that value was left changed after the game closed. The scheduling tweak is now tracked and reverted correctly in that case, and a failed per-process boost no longer discards it.
- **No leftover timer value when the timer request fails** - the high-resolution timer optimization wrote the persistent GlobalTimerResolutionRequests registry value before requesting the timer resolution; if that request failed, the optimization reported failure and left the registry value behind. It is now tracked and reverted even when the live timer request fails.
- **Optimizations never leave a partial change behind on an unexpected error** - several session optimizations apply multiple system changes in sequence (network tweaks, session system tweaks, CPU core unparking, MPO toggle, Competitive Mode, scheduled-task suppression). Previously, if an unexpected error interrupted one of them part-way through, the changes already made were reported as a failure and therefore never reverted, so they could persist after the game closed. Each now tracks exactly what it committed and reverts only that on session end (and via crash recovery), even when a later step fails. This also closes two related gaps: Competitive Mode could leave the Discord-overlay registry value changed if an error struck at the instant it was written, and CPU core unparking could leave its low-latency idle (C-state) values applied if that step failed part-way.

## [3.8.4] - 2026-06-15

### Added

- **One-click fix for a leftover AMD timer setting** - if an older GameShift version left the platform-timer BCD tweaks (`disabledynamictick` / `useplatformtick`) set on an AMD CPU, the Dashboard now shows an advisory with a "Fix & Restart" button that reverts them and reboots to restore the efficient per-core timer. The check is skipped entirely on non-AMD systems and runs off the UI thread, and the advisory can be dismissed.

### Fixed

- **Hardened the WMI process-detection fallback** - on systems where the ETW process monitor is unavailable and GameShift falls back to WMI, an exception while handling a process start/stop event ran on a background thread that no global handler covered and could terminate the app. The handlers are now guarded, and the COM-backed WMI event objects are disposed rather than left for finalization.
- **Game start/stop handling is race-free** - the per-game profile start and stop handlers are now serialized, so under the WMI fallback a start and a concurrent stop can no longer throw or leave the session stuck in a state that silently disables further optimization.
- **Temperature monitoring cannot race its own shutdown** - the 2-second sensor poll and disposal are now serialized, so the hardware-monitor driver state can never be torn down while a reading is in flight.
- **No hang on exit** - quitting GameShift at the same instant a game closes could deadlock the shutdown revert; the revert now runs off the UI thread and game monitoring is stopped first, so exit is always clean.
- **Detection no longer optimizes the wrong process** - executables that can live inside a game's install folder (platform stubs like `start_protected_game.exe`, EasyAntiCheat/BattlEye launchers, crash reporters, redistributables/installers) are no longer matched as the game. This stops optimizations from targeting a helper process and prevents a premature full revert when that helper exits mid-game.
- **Self-heals a missed game-exit** - a periodic liveness check reconciles tracked games whose stop event was dropped (possible on the WMI fallback under load), so optimizations reliably revert once the game is actually gone instead of staying applied until the app restarts. It is conservative: a game is only reconciled when its process is provably gone, never on a transient lookup failure.
- **Kernel-memory tweak rolls back a partial apply** - if applying the kernel-memory tweak failed after the first of its two registry writes, the first value was left changed but untracked (un-revertable). It now rolls back on a mid-apply failure, matching the other multi-write tweaks.
- **Per-game firewall rules are never orphaned** - if creating a per-game firewall rule timed out (PowerShell slow to exit), the rule could be created yet recorded as not-created and left behind permanently. The timeout path now marks it for cleanup so the rule is removed on revert.
- **Crash-recovery write failures are surfaced** - if the session journal can't be written when a game starts (disk full, folder not writable, AV lock), GameShift now logs it as an error so it is visible that crash recovery is unavailable for that session, instead of failing silently. The normal game-close revert is unaffected.
- **DPC Doctor sparkline reads safely** - the per-driver latency history is now snapshotted under its lock when the chart is drawn, avoiding a torn frame and a rare index error during live capture.
- **Anti-cheat is reliably recognized at launch** - when a tracked game starts, GameShift now backfills the executable name/path and the known anti-cheat type onto the active profile from its built-in database. This makes kernel-anti-cheat titles identifiable even when the profile lacked that metadata, so the app consistently avoids the external priority/affinity writes those anti-cheats watch for.
- **Priority and affinity changes are guarded against PID reuse** - if a game exited and Windows recycled its process ID before the priority boost or CPU-set pin was applied, GameShift could have acted on an unrelated process now holding that ID. Both paths now verify the live process still matches the detected game before changing anything, and exit cleanly if the process is gone.
- **Session counters are read consistently** - the applied-optimization count and the per-session stats shown in the tray post-session summary (duration, average and peak DPC, optimizations applied and failed) are updated from background threads; their reads and writes are now synchronized so the displayed numbers can no longer be torn or stale.

## [3.8.3] - 2026-06-15

### Fixed

- **Platform-timer kernel tweaks no longer applied on AMD** - the BCD kernel tuning options "Disable Dynamic Tick" and "Use Platform Tick" force Windows onto a platform/periodic timer, which on AMD Ryzen (and any CPU with an invariant TSC) degrades performance instead of helping. Windows itself flags this with HAL event 17 ("the clock interrupt is backed by a platform timer instead of a per-processor source. Performance may be degraded.") and Kernel-Power event 508 ("the system has been constrained to a periodic tick."). GameShift now detects the CPU vendor via CPUID and refuses to apply these two tweaks on AMD across every path that could set them: the kernel tuning list (marked as not recommended in the DPC Doctor page), the "Disable HPET" quick fix, and known-driver auto-fixes. Reverting is always allowed, so a value set before this change can still be removed.
- **Interrupt Optimization can no longer force MSI onto incapable devices** - the opt-in interrupt tweak decided "MSI supported" purely from the presence of a registry key and would even create that key on a device that never advertised MSI, and it forced MSI on the USB host controller. Forcing MSI on a device whose driver does not support it can leave it unable to start after reboot (a black screen, or an unresponsive keyboard/mouse, with the in-app revert then unreachable). It now only ever flips an MSI flag that already exists, never fabricates one, never touches the USB controller's interrupt mode (USB affinity pinning, which is safe, is kept), prefers the discrete GPU over an integrated one, and the consent dialog names the GPU and spells out the reboot/Safe-Mode risk.
- **Interrupt affinity can no longer target a non-existent core** - the core picker could choose a core index that does not exist on low-core CPUs and used a single-group mask that is wrong on systems with more than 64 logical processors. It now validates the chosen core and skips interrupt pinning entirely on CPUs with fewer than 4 or more than 64 logical processors.
- **Re-enabling Memory Integrity now restores your exact prior platform-security setting** - disabling VBS/HVCI cleared `RequirePlatformSecurityFeatures` but never saved the original, and re-enabling deleted it rather than restoring it, leaving managed/OEM machines with weaker enforcement than before. The original value is now captured and restored faithfully on re-enable.
- **Fixed page file no longer shrinks below a safe size or breaks crash dumps** - the page file optimization could pin a fixed 2-4 GB page file on high-RAM systems (lowering the commit limit and preventing kernel crash dumps), read the wrong memory figure, flatten a custom multi-drive page file layout onto the system drive, and applied with no confirmation. It now reads installed RAM correctly, never sizes below 8 GB, preserves page files on other drives, checks free space first, and shows a confirmation dialog.
- **Suspended Discord/Steam/NVIDIA apps are resumed after a crash** - Competitive Mode suspends overlay/helper processes for the session, but if GameShift crashed mid-game their PIDs lived only in memory and the apps stayed frozen until force-killed. The suspended PIDs are now journaled, so the watchdog and boot-recovery resume them after a crash (guarded against PID reuse).
- **Per-game suspended processes are also resumed after a crash** - the per-game process-suspend action had the same gap as Competitive Mode; its suspended PIDs are now journaled and resumed by crash recovery too.
- **No external priority/affinity writes on anti-cheat-protected games** - the per-game profile system set process priority/affinity by opening a handle to the running game even for kernel-anti-cheat titles (Vanguard, EAC, BattlEye, etc.), where such external writes are usually blocked and are the access pattern anti-cheats watch for. It now skips the live game-process writes for kernel-anti-cheat games (those are handled via the registry at launch instead) and still adjusts launcher priorities. Relatedly, the hybrid-CPU pinning now opens a minimal-rights handle instead of a full-access one.
- **Disabling Memory Integrity now warns about Hyper-V/WSL2/Docker** - turning off VBS also turns off the hypervisor, which breaks Hyper-V, WSL2, and Docker. The confirmation dialog now lists any of those that are active so the choice is informed (matching the warning the DPC Doctor page already showed).
- **One-click "Restore All GameShift Tweaks"** - persistent system tweaks could only be reverted one at a time, and their captured originals are stored per-user, so there was no single way to undo everything. Settings now has a "Restore All GameShift Tweaks" button that reverts every tweak GameShift applied back to its original values.

## [3.8.2] - 2026-06-10

### Added

- **Continuous integration** - every push and pull request now builds the solution with warnings treated as errors, runs the full test suite, and verifies formatting on GitHub Actions. Pull requests additionally run the real win-x64 single-file publish, the exact step that broke silently before 3.8.1. Pushing a release tag publishes the exe as a build artifact and drafts the GitHub release automatically.
- **Crash-recovery contract tests** - new tests lock in the invariants behind the journal and watchdog design: every journaled optimization must have a watchdog recovery factory (and vice versa), every factory must build the module it claims to, recovery must never throw or report success on corrupt journal data, and the registry crash-restore path is verified end to end for all four value types (String, DWord, QWord, Binary). Test count: 187 to 205.
- **Revert-symmetry verification harness** - the "restores your exact prior state" promise is now provable on a live machine. A new harness snapshots every piece of persistent state a session can touch (the watched registry surface, the active power scheme and 20 power-setting indexes, every Windows service, every scheduled task), runs a maximum-coverage session through the real optimization engine, reverts it, and requires the final snapshot to match the first byte for byte. Background churn by Windows itself is separated from real residue by checking what the session actually touched, and demand-start service status (process lifetime that Windows manages on its own, like the diagnostic hosts) is reported as informational drift rather than residue. CI runs the full live cycle on a throwaway elevated runner on every push and PR (informational while it builds a track record), and a coverage test fails the build if a future optimization module is added without joining the harness or being explicitly excluded with a reason. The cycle is double-gated locally (environment variable plus elevation), so a normal `dotnet test` never modifies a developer machine.

### Fixed

- **Recovery no longer reports success on corrupt journal data** - found by the new contract tests: the service suppressor and GPU driver optimizer treated an unreadable journal record as "nothing to do" and reported a successful revert. Both now report a failure so the recovery log tells the truth.
- **Cascade-stopped services are now restored on revert** - found by the new revert-symmetry harness on its very first live run: stopping a Windows service also stops every service that depends on it, but only the directly targeted service was recorded, so dependents (for example the Diagnostic System Host) stayed stopped after the session. The suppressor now enumerates running dependents before stopping, records each one for restart, and refuses to stop a service at all if the cascade would take down anything on the never-stop safety list, closing an indirect path around that protection.

## [3.8.1] - 2026-06-02

### Performance

- **Faster cold start** - ReadyToRun is enabled again, so the published app ships pre-compiled to native code and skips JIT-compiling its startup path on launch.

### Fixed

- **A second launch no longer crashes on exit** - starting GameShift while it's already running correctly tells the first instance to show its window, but the second process then tried to release a single-instance mutex it never owned - throwing on the way out (exit code 4 and a stray crash log). It now releases the mutex only when it actually owns it.

## [3.8.0] - 2026-06-02

### Performance

- **Faster, smoother startup** - the main window now appears within a fraction of a second showing a brief loading state, while core services (hardware detection, performance counters, registry/WMI queries) initialize on a background thread instead of blocking the UI thread. Previously the window didn't paint until all startup work had finished.
- **Faster cold start** - single-file compression is now off, so the self-contained bundle no longer unpacks to a temp directory on every launch and the app starts straight from disk. Slightly larger download in exchange for a faster start.
- **Less work on every launch** - "Start with Windows" registration now checks the existing scheduled task on disk and only re-creates it when it's actually missing or stale, instead of spawning `schtasks.exe` on every launch.

### Changed

- **Simpler navigation by default** - Easy Mode is now the default, the sidebar is grouped into sections, optimization labels are plainer and less jargon-heavy, and the first-run wizard was streamlined - a less overwhelming layout out of the box.

### Fixed

- **Fixed two spots that could crash the app** - the Quick Optimize button and the ping monitor's one-second timer now fully guard against exceptions, so a hiccup in either can't take the whole app down with it.
- **Optimizations now revert to your exact prior state** - a pass over the apply/revert code fixed cases where reverting wrote a guessed Windows default (or left a registry value/key behind) instead of restoring what was there before, or deleting what GameShift created. Covers the page file, Win32 priority separation, process priority, CPU parking, MMCSS, GPU/USB interrupt-affinity keys, and the Discord-overlay tweak.
- **Core isolation actually reserves cores now** - the `ReservedCpuSets` bitmask was indexed by CPU Set ID (which starts at 0x100) instead of logical-processor number, so Windows reserved nothing. It now reserves the cores you select.
- **External-tool tweaks no longer report false success** - `fsutil` (last-access timestamps), `schtasks`, and firewall-rule commands now check their exit code and kill on timeout, so a failed apply or revert is reported as failed instead of silently "succeeding." MMCSS apply is now atomic too (a partial write is rolled back).
- **Windows Defender exclusions are no longer clobbered** - the per-game Defender exclusion action only removes exclusions it actually created, never your pre-existing ones, and skips entirely if it can't read the current list.
- **Safer background-process handling** - FACEIT anti-cheat is now protected from ProBalance restraint; background processes are tracked by name as well as PID so a reused PID can't reset the wrong process; and EcoQoS is only cleared on processes GameShift actually throttled.
- **V-Cache and LP-core pinning run by default again** - `HybridCpuDetector` was gated behind a P-core-only toggle that's off by default, which silently disabled AMD X3D V-Cache CCD pinning and Intel LP-core exclusion for everyone. It's now driven by topology plus the relevant per-feature toggles.
- **Second audit pass - more apply/revert fixes** - extends the revert-to-exact-state work across the remaining modules: CPU core-unparking and C-state limiting now skip (or restore) settings they couldn't read instead of leaving gaming values applied; per-game GPU registry overrides preserve the original value *type* (DWORD/QWORD/binary) on revert instead of forcing it to a string; interrupt-handling MSI is only undone when it was actually changed; MMCSS, NTFS 8.3 and other registry tweaks no longer throw or silently delete on an unexpected value type; and Background Mode now restores the Win11 high-resolution-timer registry key when you turn the mode off.
- **Partial tweaks no longer get stuck half-applied** - if a multi-value tweak (Disable MPO, Disable Game DVR) fails partway through, it rolls back what it wrote (or records the partial baseline) instead of leaving changes behind with no way to revert them.
- **Tweaks stop reporting false success** - Disable Memory Compression, Enable Large Pages and Disable USB Selective Suspend now report a real failure (instead of an "applied" state that can never be reverted) when the underlying command fails, and external commands (`netsh`, `powercfg`, PowerShell) are killed if they hang instead of leaking a stuck process.
- **Defender exclusions target your actual install** - the per-game Defender exclusion now also covers the real detected game folder, not just the default install path, and only records an exclusion as "ours" once the command actually succeeds.
- **Battery-aware on laptops** - Disable Power Throttling is now skipped while running on battery (it's system-wide and hurts battery life), with a clearer description; the session power-plan overrides remain AC-only by design.
- **Quieter, more honest diagnostics** - failed I/O-priority and working-set reverts are logged instead of swallowed; one-time game tips only appear after their "don't show again" state is saved; and the two per-game process actions now share a single anti-cheat blocklist so they can't drift apart.

### Crash Recovery

- **Five more optimizations survive a main-app crash** - `GpuDriverOptimizer`, `SessionSystemTweaksOptimizer`, `TimerResolutionManager`, `CompetitiveMode`, and `ServiceSuppressor` now implement `IJournaledOptimization` and are registered with the watchdog. The persistent state they change (GPU driver + TDR registry, MMCSS/USB-suspend/PCIe-ASPM, the Win11 global timer key, the Discord overlay key, and stopped services) is now restored after a crash instead of being left applied. The watchdog recovers 13 optimization classes, up from 8.
- **No more double-apply baseline corruption** - `OptimizationEngine` now skips any optimization that's already applied, so running "Optimize Now" and then launching a game can't overwrite the captured original values and break revert.
- **Per-game actions survive a crash too** - firewall rules, Defender exclusions, GPU-registry overrides and fullscreen-optimization keys created for a game are now journaled as self-describing records, so the watchdog removes or restores them after a crash instead of leaving them applied. Previously only `IOptimization` modules were recoverable; GameActions ran through a separate in-memory path that was lost on a crash.
- **Demoted background processes recover after a crash** - the memory/IO-priority, EcoQoS and E-core routing applied to background processes (Search, Defender's scanner, OneDrive, browsers, etc.) are reset to Windows defaults *by name* during crash recovery, so a still-running process isn't left demoted. Background routing also rescans during a session so processes launched after the game are covered too.

## [3.7.0] - 2026-04-22

### Added -- Crash Recovery Coverage

- **Seven additional optimizations now journaled** - `VisualEffectReducer`, `NetworkOptimizer`, `ScheduledTaskSuppressor`, `PowerPlanSwitcher`, `CpuParkingManager`, `ProcessPriorityBooster`, and `HybridCpuDetector` all now implement `IJournaledOptimization`. Combined with the pre-existing `MpoToggle`, the watchdog can now restore eight classes of optimizations from the journal after a main-app crash. Registry entries, service states, network adapter properties, power plan sub-settings, IFEO fallback keys, and scheduled task enablement all survive crashes.
- **Watchdog-to-main-app recovery signaling** - when the watchdog performs a recovery (due to heartbeat timeout or disconnect), it now stamps `LastRecoveryTimestamp` in the journal. The main app's `OptimizationEngine.DeactivateProfileAsync` checks this via `WasRecoveredDuringCurrentSession()` and skips its own LIFO revert when the watchdog has already rolled everything back. Prevents double-revert races and incorrect state when the main app is slow (e.g., ThreadPool starvation) but still alive.
- **Reboot and Windows-update warnings on startup** - if a previous DPC fix required a system reboot but hasn't been acknowledged, or if Windows updated between sessions, users now see an informational dialog on app launch. Journal fields (`HasPendingRebootFixes`, `BuildChangedWarning`) are now read and cleared by the App layer.

### Added -- Test Infrastructure

- **Injectable file paths for tests** - `JournalManager`, `SettingsManager`, and `SessionHistoryStore` now accept override paths via internal constructors / properties, isolated from production `%ProgramData%` and `%AppData%` paths. `TempPath` test helper auto-cleans temp directories.
- **48 new unit tests** - JournalManager (17), WatchdogRevertEngine (9), BootRecoveryTaskManager (8), SessionHistoryStore (8), SanitizeGameId (62 via Phase 3 partial), GitHubUrlValidator (18 via Phase 3 partial). Total test count: 71 → 187.
- **Shared `GitHubUrlValidator`** - extracted URL allowlist validation from `UpdateChecker` and `UpdateDownloader` into a reusable public helper, also comprehensively unit-tested.

### Fixed

- **Two false-positive unit tests rewritten** - `ConcurrentActivate_Deactivate_ThreadSafe` previously only asserted `Assert.True(true)`; now uses an interleave-detecting mock that catches semaphore removal. `ActivateProfile_CapturesSnapshot_BeforeApplyingOptimizations` previously only checked call count; now verifies the engine actually passes a non-null `SystemStateSnapshot` to `ApplyAsync`. Both were verified to fail when their respective production code is broken.
- **`DashboardViewModel` subscriptions leaked on shutdown** - `DashboardPage.OnUnloaded` only called `StopTimers()`, so `SessionTracker.SessionEnded`, `AllActivities.CollectionChanged`, and the `Update`/`Hero`/`Vbs` sub-ViewModel subscriptions were never released. `App.OnExit` now invokes `MainWindow.CleanupLongLivedPageViewModels()` which calls the full `DashboardViewModel.Cleanup()`; the page's Unloaded handler detects real teardown via a new `MainWindow.IsClosingForReal` flag.
- **`SettingsViewModel.LoadSettings` missed 3 property notifications** - after loading, `AdvancedMode`, `IsEasyMode`, and `BgProBalanceEnabled` were assigned to backing fields without firing `PropertyChanged`, so the UI could display stale values after Import Settings. Notifications are now raised for all three.
- **OptimizationsPage category color strip was always empty** - the inner-row `Border` used `AncestorLevel=2` which walked to the outer `ItemsControl` whose DataContext is the ViewModel (no `CategoryColor`). Corrected to `AncestorLevel=1` so the binding resolves against the `OptimizationGroup`.
- **First-run wizard "Start with Windows" setting was saved but not applied** - `OnFinishClicked` persisted `StartWithWindows` to settings but did not call `StartupManager.SetStartWithWindows(...)`, so the scheduled task / registry entry was only created on the next Save from the Settings page. Now applied immediately on wizard finish.

## [3.6.3] - 2026-04-18

### Security

- **PATH hijacking eliminated** - all 14 external executables (`powercfg`, `schtasks`, `bcdedit`, `powershell`, `shutdown`, `netsh`, `sc`, `cmd`, `fsutil`, `secedit`) were invoked by bare filename, which Windows resolves via PATH. Since GameShift runs as administrator, a malicious executable in any writable PATH directory (common with developer tools) could execute as admin. All system tool invocations now use absolute paths via `Path.Combine(Environment.SpecialFolder.System, "tool.exe")`.
- **Named pipe access control** - `SingleInstancePipe` and `WatchdogPipeServer` were created with default ACLs, allowing any local user to connect and trigger window activation or force optimization reverts mid-game. Both pipes now restrict access to Administrators and SYSTEM only via explicit `PipeSecurity`.
- **Update download URL validation** - the `browser_download_url` and `html_url` from the GitHub API were trusted blindly. A tampered API response could redirect downloads to a malicious server. URLs are now validated against an allowlist (`github.com`, `*.githubusercontent.com`) over HTTPS only.
- **Update batch script TOCTOU hardened** - the update script was written to a predictable filename (`gameshift-update.cmd`) in the application directory. Now uses a random GUID filename to prevent race-condition replacement attacks.
- **PowerShell injection prevention** - `FirewallRuleAction._direction` was interpolated unquoted; now validated as `Inbound`/`Outbound` only and quoted. `DpcFixEngine` adapter property values are validated as numeric before interpolation.
- **Profile path traversal** - `ProfileManager` constructed file paths from unsanitized game IDs, allowing crafted IDs (e.g., `..\..\Windows\System32\evil`) to write/read arbitrary `.json` files as admin. Game IDs are now sanitized via `SanitizeGameId()`.

### Fixed

- **Optimization row click leaked to expand/collapse** - after fixing the v3.6.2 toggle bug, clicking the CheckBox also toggled the row's expand state. The row click handler now walks the visual tree to skip clicks originating from a CheckBox.
- **Settings file corruption on crash** - `SettingsManager.Save()` and `SessionHistoryStore.Save()` used non-atomic `File.WriteAllText`, risking corruption if the process crashed mid-write. Both now write to a `.tmp` file then atomically rename via `File.Move(overwrite: true)`.
- **First-run wizard reset all settings** - completing the wizard created a brand-new `AppSettings` object with only 3 properties set, overwriting all existing BackgroundMode/notification/profile settings with defaults. Now loads existing settings first and overlays only the wizard values.
- **3 additional process-launching deadlocks** - `TaskDeferralService`, `PowerPlanManager`, and `HardwareScanner` had the same stdout/stderr deadlock pattern as the 10 fixed in v3.6.2. All three now read stderr concurrently in a background task.
- **20 WMI ManagementObject COM handle leaks** across 9 files - foreach loops over `searcher.Get()` did not dispose individual `ManagementObject` instances. All loops now dispose properly. Files: `GpuDetector`, `HardwareScanner` (3 loops), `HybridCpuDetector`, `GpuDriverOptimizer`, `DpcTroubleshooter`, `DisableMemoryCompression`, `SystemInfoGatherer` (8 loops), `PowerPlanConfigurator` (3 loops).
- **`AntiCheatDetector` ServiceController array leak** - `ServiceController.GetServices()` returned an array of native handles that were never disposed.
- **DPC trace engine peak update race** - `_systemPeakDpc` was read and updated in two separate Interlocked operations, allowing a higher value from a concurrent thread to be overwritten by a lower value. Now uses a proper CAS loop.
- **DPC capture seconds non-volatile** - `CaptureSeconds` was read from the UI thread and written from the timer thread without synchronization. Now uses `Interlocked.Increment` and `Volatile.Read`.
- **DPC latency monitor non-volatile flag** - `_isMonitoring` was written from `Start`/`Stop` and read from timer/ETW callbacks without `volatile`.
- **Hybrid CPU detector lazy init race** - the `IsHybridCpu`/`PCoreCount`/`PCoreAffinityMask` properties checked `_isHybrid == null` without synchronization, allowing concurrent threads to call `Detect()` simultaneously. Now uses double-checked locking.
- **DriverVersionTracker advisory list race** - the `ActiveAdvisories` list was cleared and rebuilt incrementally, allowing UI readers to see a partially-populated list and throw `InvalidOperationException`. Now built locally and swapped via atomic reference assignment.
- **AntiCheatDetector cache race** - the static `_detected` flag is now `volatile` to ensure cross-thread visibility of detection results.
- **KnownGamesStore.GetAllGames returned live wrapper** - `_games.AsReadOnly()` returned a wrapper around the live list. Concurrent mutations from another thread could throw during enumeration. Now returns a snapshot via `_games.ToList().AsReadOnly()`.
- **Dark title bar missing on UpdateWindow and FirstRunWizardWindow** - both windows had dark backgrounds but white Windows 11 title bars. `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` is now applied at construction.
- **TemperatureMonitor showed `float.MaxValue` before first reading** - `MinCpuTemp`/`MinGpuTemp` were initialized to `float.MaxValue` and visible to the UI before any reading arrived. Now initialized to 0 with a `_hasFirstReading` flag that sets min/max directly on the first sample.
- **DPC Doctor driver status badge always green** - the badge background was hardcoded to dark green regardless of severity, making Critical drivers look "good" at a glance. Now uses `DataTrigger`s to color the background by severity.
- **Laptop detection failed silently on some Windows versions** - `Win32_SystemEnclosure.ChassisTypes` returns `int[]` on some systems and `ushort[]` on others; the cast `is ushort[]` failed silently on the former. Now uses `Array` + `Convert.ToInt32` to handle both.
- **Riot Games detection only checked C:\\** - users with games on D:\\ or other drives got false negatives, potentially causing GameShift to disable VBS when Vanguard was actually installed. Now also checks the registry for Riot Client install path and the `vgc` (Vanguard) service existence.
- **`SessionTracker` and `DetectionOrchestrator` never unsubscribed events** - both subscribed to detector events in their constructors but had no cleanup path. Could fire callbacks against disposed services during shutdown. `SessionTracker` now implements `IDisposable`; `DetectionOrchestrator` has a `Cleanup()` method called from `App.OnExit`.
- **`SingleInstancePipe.ReadLineAsync` had no timeout** - a malicious or hung client could connect and never send a line, blocking the read indefinitely. Now uses a 5-second timeout per read.
- **Duplicate `UnhandledException` handler** - the static constructor and `OnStartup` both registered handlers for `AppDomain.CurrentDomain.UnhandledException`, causing duplicate crash log entries. The duplicate registration was removed.
- **Two implemented system tweaks were not registered** - `OptimizeNtfsMemoryUsage` and `OptimizeKernelMemory` were fully implemented but missing from `SystemTweaksManager._tweaks`, making them unreachable. Now registered.
- **`App.OnExit` async-void could be killed mid-revert** - WPF may terminate the process before `await DeactivateProfileAsync()` completes. Now blocks synchronously via `.GetAwaiter().GetResult()` to ensure optimization revert finishes before exit.
- **PowerShell `WaitForExit` without timeout in 2 game actions** - `DefenderExclusionAction` and `FirewallRuleAction` could hang indefinitely if PowerShell stalled. Added 15-second timeout with process kill on hang.
- **`FirewallRuleAction.RuleExists` missing quote escaping** - `_ruleName` was interpolated without `Replace("'", "''")`, unlike `Apply` and `Revert`.
- **`DisableGameDvr.Revert` assumed integer registry values** - called `GetInt32()` on all original values; string-typed values threw `InvalidOperationException`. Now checks `ValueKind` first.
- **`TaskDeferralService` re-enabled user-disabled tasks** - did not check whether a task was already disabled before disabling it; then unconditionally re-enabled all tracked tasks. Now skips already-disabled tasks.
- **Steam library path dedup was case-sensitive** - `D:\\Steam` and `d:\\steam` were treated as different paths.
- **`PowerPlanManager.CheckIdle` TickCount overflow** - `Environment.TickCount` is 32-bit signed and wraps at ~24.8 days. Now uses `Environment.TickCount64` masked to 32 bits to match `LASTINPUTINFO.dwTime`.
- **`PowerPlanSwitcher` activated plans as side effect of existence check** - used `PowerSetActiveScheme` to test if a plan existed, accidentally switching the active plan. Now uses `powercfg /query` with no side effects.
- **`TimerResolutionManager` Win10 revert passed wrong resolution** - the revert path passed the original system resolution instead of the resolution actually applied, failing to release the timer lock. Now stores and uses the applied resolution.
- **`BenchmarkService` median off-by-one** - did not average the two middle values for even-sized arrays.
- **osu! profile `SuspendDiscordOverlay` was inert** - set to `true` but `EnableCompetitiveMode = false` meant CompetitiveMode never applied, so the flag had no effect. Set to `false` to be honest about what the profile does.
- **Removed `SetLastError = true` from ntdll P/Invokes** - NT Native API functions return NTSTATUS, not Win32 last error. The flag caused unnecessary `GetLastError()` calls returning stale data.

### Removed

- **Dead code cleanup** - removed 11 unused items: `GpuDetector.ClearCache()`, `HardwareScanner.GetSummary()`, `XboxLibraryScanner.LaunchGame()` (and unused COM interop types), `OptimizationStatus` class, `TrayIconManager.OnTrayLeftClick`/`_hasError`/`_flyoutWindow`, `HeroOptimizeViewModel.Start()`/`Stop()`, 14 unused `GS.Spacing.*` theme resources, duplicate `AdvancedVisibility` converter, misleading "binary search" comment in `DpcTraceEngine`.

### Changed

- **`AdlxManager`** - marked as a stub explicitly; ADLX integration is not yet implemented.
- **`CompetitivePresets`** - added a class-level note that hardcoded game install paths assume default installation directories.

## [3.6.2] - 2026-04-16

### Fixed
- **Dashboard optimization toggles could not be clicked** - the CheckBox toggle on each optimization row was intercepted by a tunneling event handler that prevented the click from reaching the control. Users could not enable or disable individual optimizations from the Dashboard.
- **NvAPI driver settings pointer truncation on 64-bit** - the `nvapi_QueryInterface` delegate returned `int` (32-bit) instead of `IntPtr`, truncating 64-bit function pointers and causing access violations. This was the root cause of NvAPI DRS being disabled. Fixed the delegate signature to return `IntPtr`.
- **Game detection crash on built-in profile match** - when a game was detected via built-in profile name matching (tertiary strategy), the code added the game to the known games list while iterating it, throwing `InvalidOperationException`. Now defers the addition until after iteration completes.
- **Game detection thread safety** - the known games list was accessed from both UI and ETW/WMI callback threads without synchronization, risking corruption or crashes under load. All access is now protected by a lock.
- **Dangling process handles in ProcessSnapshotService** - captured OS process handles were invalidated immediately after the Process objects were disposed, leaving all callers (MemoryOptimizer, EfficiencyModeController, IoPriorityManager) operating on stale handles. Removed the cached handle; callers now open dedicated handles with correct access rights.
- **Crash recovery lockfile failed to serialize** - `SystemStateSnapshot.ProcessAffinities` used `IntPtr` values which `System.Text.Json` cannot serialize. Changed to `long` so the crash recovery lockfile writes correctly.
- **Process handle leaks in HybridCpuDetector** - both `ApplyViaCpuSets` and `ApplyWithFallback` obtained Process objects without disposing them, leaking native handles on every gaming session.
- **Update installer path injection** - special characters (`%`, `&`, `^`) in the GameShift install path could break or exploit the update batch script. Paths are now escaped before interpolation, and the hidden-window error path uses `timeout` instead of `pause` (which would hang forever).
- **UI deadlock on tray pause** - `TrayIconManager.TogglePause()` called `.Wait()` synchronously on the UI thread, risking deadlock if the async deactivation touched the Dispatcher. Changed to proper `async`/`await`.
- **Deadlock game profile never applied** - `BuiltInProfiles` used `"deadlock.exe"` but the actual Valve executable is `"project8.exe"`. Session-level optimizations (priority, affinity, launcher demotion) were silently skipped. Now matches both names.
- **Timer resolution lock not released on Background Mode stop** - `TimerResolutionService.Stop()` passed the original system resolution to `NtSetTimerResolution` instead of the resolution that was actually set, so the API failed to release the lock. Now correctly passes the applied resolution.
- **DPC Doctor reboot without confirmation** - clicking "Restart Now" immediately executed `shutdown /r /t 10` with no confirmation dialog. Added a Yes/No prompt.
- **Dashboard event handlers accumulated on page navigation** - navigating away from and back to the Dashboard added duplicate event subscriptions each time, causing redundant UI updates. `StartTimers()` now unsubscribes before re-subscribing.
- **10 process-launching methods could deadlock** - `RunPowercfg`, `RunBcdedit`, `RunSchtasks`, `RunProcess`, and `RunPowerShell` helpers across the codebase redirected stderr but never drained it, risking deadlock when the pipe buffer filled. All 10 methods now read stderr concurrently in a background task.
- **PingMonitor crash on stop** - a race between `Stop()` disposing the `Ping` object and the `async void` timer callback using it could throw `ObjectDisposedException` and crash the process. Added a `_stopping` flag and `ObjectDisposedException` guard.
- **PingMonitor kept pinging after leaving Dashboard** - `PingMonitorViewModel.Stop()` unsubscribed the event but did not call `_pingMonitor.Stop()`, so ICMP pings continued indefinitely in the background.
- **Journal file corruption under concurrent access** - `JournalManager` methods that mutate session state and write to disk had no synchronization. Concurrent calls (e.g., optimization engine + game detection) could produce corrupt JSON. Added lock protection to all public methods.
- **Settings file race condition** - `SettingsManager.Load()` and `Save()` accessed the settings file without locking. Concurrent calls could lose writes. Added file-level lock synchronization.
- **Logger reconfigured on every settings load** - `SettingsManager.Load()` called `ConfigureLogger()` unconditionally, creating a new Serilog logger (and leaking file handles) on every call. Now only reconfigures when logging settings actually change.
- **Game directory prefix match too broad** - `D:\Games\Ark` would match `D:\Games\ArkSurvival\something.exe` because the install directory comparison lacked a trailing path separator. Now appends `\` before comparing.
- **DPC fix revert assumed DWORD registry type** - reverting a DPC fix always wrote the previous value as `RegistryValueKind.DWord`, but some values are QWORD or String. Now detects the appropriate type.
- **DPC net adapter fix hardcoded previous value** - `ApplyNetAdapterFix` assumed all adapter properties defaulted to `"1"`. Now queries the actual current value via PowerShell before overwriting.
- **Game DVR revert failed on string registry values** - `DisableGameDvr.Revert` called `GetInt32()` on all original values. String-typed values threw `InvalidOperationException`, silently failing the entire revert. Now checks `ValueKind` first.
- **Task deferral re-enabled user-disabled tasks** - `TaskDeferralService` did not check whether a scheduled task was already disabled before disabling it, then unconditionally re-enabled it after gaming. Now skips already-disabled tasks.
- **PowerShell game actions could hang indefinitely** - `DefenderExclusionAction` and `FirewallRuleAction` called `WaitForExit()` with no timeout. Added 15-second timeout with process kill on hang.
- **Firewall rule existence check missing quote escaping** - `RuleExists()` did not escape single quotes in rule names, unlike `Apply()` and `Revert()`.
- **ActivityLogViewModel permanent event leak** - subscribed to a static `ObservableCollection.CollectionChanged` with a lambda that could never be unsubscribed, preventing GC of every ActivityLogViewModel instance. Now stores the handler in a field and unsubscribes on page unload.
- **SystemViewModel temperature monitor leak** - never unsubscribed from `TemperatureMonitor.TemperatureUpdated`, accumulating stale subscriptions on each page navigation. Added `Cleanup()` called from page `Unloaded`.
- **OptimizationsPage never cleaned up event subscriptions** - unlike DpcDoctorPage and DashboardPage, this page had no `Unloaded` handler to call `Cleanup()`. Added one.
- **Crash log overwritten on double-crash** - `WriteCrashLog` used `File.WriteAllText`, so a crash during recovery overwrote the original crash log. Changed to `File.AppendAllText` to preserve all entries.
- **FirstRunWizardWindow showed "v3.0"** - version badge was hardcoded instead of reading from the assembly. Now reads the version dynamically.
- **Power plan activated as side effect of existence check** - `FindOrCreatePerformancePlan` called `PowerSetActiveScheme` to test if a plan existed, unintentionally switching the active plan. Now uses `powercfg /query` which has no side effects.
- **TimerResolutionManager Win10 revert passed wrong resolution** - the Win10 revert path passed the original system resolution instead of the resolution that was applied, failing to release the timer lock.

## [3.6.1] - 2026-04-07

### Fixed
- **Start with Windows not launching the app** - GameShift requires admin elevation, but `HKCU\Run` entries cannot trigger UAC prompts at logon, so Windows silently blocked the startup. Replaced the registry entry with a scheduled task that uses `HighestAvailable` run level, which launches elevated without a UAC prompt. Existing users will have their settings migrated automatically on next launch.

## [3.6.0] - 2026-04-07

### Added
- **One-click "Optimize Now" hero button on Dashboard** - prominent button at the top of the Dashboard applies all recommended optimizations in one click. Shows a preview of what will be applied (click to expand the full list). Automatically reflects current state on launch - if optimizations are already active, shows "Optimized" with a revert option.
- **Easy Mode toggle** - "Easy Mode" checkbox on the Dashboard and Settings page. When enabled, hides advanced pages (DPC Doctor, Optimizations, Profiles, Game Library, System, Logs, Setup Wizard) and detailed settings sections for a simpler experience. App defaults to Advanced Mode (all pages visible) for new installs.

### Fixed
- **ProcessSnapshotService race condition** - callers could iterate disposed Process objects when a cache refresh happened concurrently. Replaced with ProcessSnapshot value objects that are safe to use from any thread.
- **CompetitiveMode safety timer race** - the 6-hour safety timeout and normal revert could run simultaneously, corrupting the suspended process list. Added lock synchronization.
- **NetworkOptimizer/DpcFixEngine process deadlock** - stderr was redirected but never read concurrently with stdout. If netsh/bcdedit wrote more than 4KB to stderr, the pipe buffer filled and the process hung. Now reads both streams concurrently.
- **PowerShell commands fail on paths with apostrophes** - game paths containing single quotes (common with GOG, custom Steam libraries) broke firewall rules and Defender exclusions. Now escapes quotes in path strings.
- **DpcTraceEngine thread safety** - per-driver DPC stats were mutated from the ETW thread and timer thread without synchronization. Added per-stats locking and Interlocked access for system peak value.
- **Process handle leaks** - ProcessPriorityBooster and CompetitiveMode leaked Win32 process handles by not disposing Process objects from GetProcessById. Added `using` to all call sites.
- **DpcLatencyMonitor duplicate alerts** - `_lastAlertTime` was written from two threads without synchronization, allowing multiple spike alerts within the 30-second cooldown. Protected with existing lock.
- **6 optimization toggles silently did nothing** - Dashboard toggle switches for Scheduled Tasks, CPU Unparking, I/O Priority, Efficiency Mode, CPU Scheduling, and Session Tweaks changed visually but never persisted to the profile. Added missing cases to the switch statement.
- **VbsHvciToggle bcdedit could freeze app** - bcdedit was called with no timeout. Added 10-second timeout with process kill on hang.
- **SystemPerformanceMonitor not stopped on page navigate-away** - kept sampling at 1-second intervals when Dashboard was not visible. Now starts/stops with page lifecycle.
- **Power plan not reverted after crash** - if GameShift crashed during a gaming session, the system stayed on Ultimate Performance indefinitely. Added crash recovery for the active power plan via `CleanupStalePowerPlan`, restoring the original plan (or falling back to Balanced) on next launch.
- **PowerPlanSwitcher silently failed on OEM systems** - Ultimate Performance plan template missing on some OEM builds (Surface, Lenovo). Added 5-step fallback chain: Ultimate Performance -> High Performance -> scan existing plans -> duplicate Ultimate -> duplicate High Performance.
- **PowerPlanSwitcher stdout pipe deadlock** - `RedirectStandardOutput=true` but never read before `WaitForExitAsync` during plan creation. Now reads both streams concurrently.
- **Session optimizations applied no power sub-settings** - when Background Mode was off, switching to Ultimate Performance left all sub-settings at stock defaults. Now applies key session overrides (EPP=0, boost policy, USB suspend, USB 3 link power, PCIe ASPM, NVMe idle timeout, wireless power saving) and reverts them on session end.
- **No fallback when reverting to a deleted original plan** - if the user's original power plan was removed during gaming, revert failed and left the system on Ultimate Performance. Now falls back to Balanced.
- **DpcFixEngine GUID detection used brittle substring match** - `"e8bf"` substring check to distinguish plan GUIDs from sub-setting GUIDs replaced with proper `Guid.TryParse`.
- **PowerPlanManager Balanced plan fallback** - idle timeout switching to Balanced now falls back to the original plan if Balanced is not available on the system (some OEM builds).
- **PowerPlanManager custom plan creation fallback** - `FindOrCreateCustomPlan` now falls back to duplicating High Performance if Ultimate Performance template is missing.
- **Custom power plan recreated on launch** - Background Mode now deletes and recreates the "GameShift Performance" power plan on every startup, ensuring existing users always get the latest sub-setting overrides after an update.

### Changed
- **Pinned all package versions** - replaced wildcard versions (`4.*`, `1.*`, `0.9.*`, `8.*`) with exact versions for reproducible builds: WPF-UI 4.0.3, Hardcodet.NotifyIcon.Wpf 2.0.1, CommunityToolkit.Mvvm 8.3.2, LibreHardwareMonitorLib 0.9.6.
- **Extracted ServiceRegistry** - replaced 21 scattered `App.*` static properties with a single `App.Services` typed registry. All 15 consumer files updated.
- **Split App.xaml.cs** (964 -> 528 lines) - extracted `CrashRecoveryHandler`, `ServiceFactory`, and `EventWiringHelper` into dedicated service classes under `Services/`.
- **Split DashboardViewModel** (1887 -> 970 lines) - extracted 6 focused sub-ViewModels: `UpdateManagementViewModel`, `HeroOptimizeViewModel`, `DpcMonitoringViewModel`, `PerformanceMonitorViewModel`, `PingMonitorViewModel`, `VbsAdvisoryViewModel`. DashboardViewModel composes them via `Update`, `Hero`, `Dpc`, `Perf`, `Ping`, `Vbs` properties.

## [3.5.3] - 2026-04-04

### Fixed
- **Start with Windows not working on Windows 11** - the startup registration only wrote to `HKCU\...\CurrentVersion\Run`, but Windows 11 additionally gates startup apps via the `StartupApproved\Run` registry key. A missing or disabled entry there silently blocks launch. Now writes the enabled flag to `StartupApproved\Run` alongside the `Run` entry.
- **Optimize Interrupt Handling tweak silently did nothing** - the Display Adapter class GUID had a typo (`bfe1801` instead of `be10318`), so the PCI device scan never matched any GPU. MSI mode and interrupt affinity pinning were never applied.
- **VRAM showing as 0 or incorrect for GPUs with 8 GB+** - `Win32_VideoController.AdapterRAM` is a uint32 and overflows above 4 GB. Now reads the accurate QWORD `HardwareInformation.qwMemorySize` from the display adapter registry key, falling back to the WMI value for integrated GPUs.
- **Disable Memory Integrity tweak not fully effective on Windows 11** - previously only set the HVCI scenario key, leaving VBS itself running. Now also disables `EnableVirtualizationBasedSecurity` and clears `RequirePlatformSecurityFeatures` to fully shut down VBS. Updated description to note that UEFI-locked VBS may require additional BIOS changes.
- **Disable MPO tweak incomplete on 24H2+** - only set `OverlayTestMode = 5`, which is insufficient on newer Windows 11 builds. Now also sets `OverlayMinFPS = 0` (fixes 24H2 Chromium freezing) and `DisableOverlays = 1` under GraphicsDrivers (25H2 forward-compatibility). All values are properly backed up and reverted.
- **Update window scrollbar clipped** - the scrollbar in the "What's new" release notes area was cut off when scrolling. Also made the update window larger (580x500) so content is less cramped.

## [3.5.2] - 2026-04-01

### Fixed
- **ReBAR detection broken on NVIDIA GPUs** - the old approach checked `RMApertureSizeInMB` in the driver registry key which doesn't exist on modern NVIDIA drivers. Now uses `nvidia-smi -q` to read BAR1 total size directly (32768 MiB on RTX 4090 = ReBAR active, 256 = ReBAR off). AMD detection via registry (`EnableLargeBar`, `KMD_EnableInternalLargePage`) kept as-is.

### Changed
- **Tray icon single-click opens main window** - clicking the system tray icon now opens the GameShift dashboard directly instead of the small status flyout popup. Right-click context menu unchanged.

## [3.5.1] - 2026-04-01

### Fixed
- **Native crash on game launch** - NvAPI DRS in-process calls caused access violations in nvapi64.dll due to struct layout mismatch with newer NVIDIA drivers. Disabled until struct validation is complete. Registry-based NVIDIA settings (Low Latency Mode, Shader Cache) still apply.
- **100% CPU utilization during gaming** - `IDLEDISABLE=1` forced all cores to C0 state, causing Task Manager to report 100% CPU even when idle. Replaced with C-state depth limiting (C1 max, 2us wake latency) that preserves low latency without the side effects.
- **ProBalance CPU overhead** - sampling interval increased from 2s to 5s, reducing kernel calls per cycle
- **USB HID over-targeting** - session tweaks were writing to all 90 HID class subkeys instead of only actual USB devices
- **Stale saved profiles** - existing profiles had `DisableProcessorIdle: true` from old default, causing the 100% CPU issue even after code fix

### Changed
- `DisableProcessorIdle` default changed from `true` to `false` (opt-in only)
- When enabled, applies 8 hidden C-state limiting power settings instead of `IDLEDISABLE=1`: IDLESTATEMAX=1, IDLEPROMOTE=100, IDLEDEMOTE=100, IDLESCALING=0, CS_TIME_CHECK=20000, LATENCYHINTPERF=100, LATENCYHINTPERF1=100, latency unparked cores=100
- Update popup always shows "Download & Install" button (asset resolution: .exe > .zip > zipball)

## [3.5.0] - 2026-03-31

### Added - Crash Recovery & State Journal
- State journal system with atomic writes to `%ProgramData%\GameShift\state.json` - records original and applied values for every optimization
- IJournaledOptimization interface (command pattern) for structured apply/revert with serialized state
- Watchdog Windows Service (`GameShift.Watchdog`) monitors the main app via named pipe heartbeat (5s interval, 15s timeout), reverts all optimizations on crash
- Boot recovery scheduled task runs at system startup (30s delay) to revert after BSOD or power loss
- Windows Update detection - compares OS build in journal vs current build and flags persistent settings for re-verification
- Registry change monitoring via `RegNotifyChangeKeyValue` - detects external modifications to managed keys during gaming and optionally re-applies

### Added - ETW Process Monitoring
- Replaced WMI `Win32_ProcessStartTrace` with ETW kernel process provider for sub-millisecond game detection latency
- WMI retained as automatic fallback if ETW session creation fails (64-session system limit)
- ETW session cleanup in boot recovery and watchdog crash recovery paths

### Added - CPU Scheduling
- `CpuSchedulingOptimizer` routes game processes to P-cores and background processes to E-cores via `SetProcessDefaultCpuSetMasks` (GROUP_AFFINITY masks)
- Power throttling control - HighQoS (disable throttling) on game process, EcoQoS on background processes
- Non-hybrid CPU fallback - skips CPU Set assignment entirely, HighQoS still applied
- `SetProcessDefaultCpuSetMasks` API gated to Windows 11 22H2+ (build 22621)
- Core Isolation - advanced opt-in feature reserves P-cores exclusively for gaming via `ReservedCpuSets` registry (OS-enforced, requires reboot)
- Visual core map in DPC Doctor page for selecting which P-cores to reserve
- Safety rails: minimum 1 P-core unreserved, E-cores cannot be reserved, non-hybrid CPUs disabled

### Added - Interrupt Affinity & MSI Mode
- GPU interrupt affinity pinned to last P-core (avoids Core 0 contention) via `DevicePolicy=4` + `AssignmentSetOverride`
- USB host controller interrupt affinity for reduced input latency
- MSI mode detection and enablement for GPU and USB controllers
- Full rollback by deleting Affinity Policy subkeys

### Added - Kernel Tuning
- Six BCD settings via bcdedit: `disabledynamictick`, `useplatformtick`, `tscsyncpolicy`, `x2apicpolicy`, `hypervisorlaunchtype`, `useplatformclock`
- Hypervisor dependency check (Hyper-V, WSL2, Docker) before offering disable
- Competitive tier applies all six; Casual tier applies only safe universal settings
- Hard blocklist prevents `testsigning`, `disableintegritychecks`, `nointegritychecks`
- Integrated into DPC Doctor page with per-setting apply/revert buttons and risk badges

### Added - GPU Optimization Depth
- NVIDIA NvAPI DRS profile management via P/Invoke against nvapi64.dll - max pre-rendered frames=1, power management=max perf, shader cache=unlimited, low latency=ultra
- AMD registry tweaks - EnableUlps=0, PP_SclkDeepSleepDisable=1, FlipQueueSize=1 frame
- ADLX stub for future AMD Anti-Lag SDK integration (graceful fallback to registry)
- TDR timeout extension (TdrDelay=8, TdrDdiDelay=10) to prevent false GPU resets during shader compilation
- NVIDIA nvlddmkm tweaks - DisableDynamicPstate=1 (force P-State 0), RmCudaSchedulingMode=1 (CUDA spin)
- HAGS awareness - detected and displayed on dashboard, advisory for RTX 30+/RX 7000+ when disabled
- Resizable BAR/SAM detection via driver registry keys, advisory when supported but not enabled
- Hardware Advisories section on dashboard showing HAGS and ReBAR status with recommendation banners

### Added - System Tweaks (Session-Based)
- MMCSS SystemProfile configuration - GPU Priority=8, Priority=6, Scheduling Category=High, SFIO Priority=High, Clock Rate=10000, NetworkThrottlingIndex=0xFFFFFFFF, SystemResponsiveness=10
- USB selective suspend disable for HID gaming peripherals during sessions
- PCIe ASPM disable via powercfg during gaming (restored on exit)

### Added - System Tweaks (Persistent)
- NTFS memory usage optimization (NtfsMemoryUsage=2)
- Kernel memory management - DisablePagingExecutive=1, LargeSystemCache=0

### Added - ProBalance
- Dynamic background CPU restraint during gaming sessions
- Monitors CPU usage every 2 seconds, demotes processes exceeding 15% for 3 consecutive samples to BelowNormal
- Automatically restores original priority when CPU drops below threshold for 5 samples
- Safety list protects game, anti-cheat, audio, system critical, and GameShift processes
- Configurable toggle in Settings (default: ON)

### Changed - Memory Management
- Replaced timed standby list purge with threshold-based purging - only clears when both standby exceeds threshold AND free memory is critically low
- Auto-scaled thresholds based on total RAM
- Added targeted EmptyWorkingSet on background processes (protects game assets)
- Added hard minimum working set on game process via SetProcessWorkingSetSizeEx
- Memory priority uses MEMORY_PRIORITY_VERY_LOW (1) instead of Low (2) for background processes
- Removed FlushModifiedPages (flushing dirty pages to disk during gaming adds I/O overhead)

### Changed - Code Quality
- Standardized logging prefixes to [ClassName] bracket style across all optimizers
- Merged duplicate WMI ProcessStartTrace watchers into single GameDetector event
- Consolidated periodic Process.GetProcesses() calls into shared ProcessSnapshotService
- Dashboard monitors pause during active game sessions to eliminate polling overhead
- Optimization Intensity system - Competitive vs Casual profiles with per-game control
- BackgroundMode exclusion logic moved from individual optimizers to OptimizationEngine

## [3.0.4] - 2026-03-14

### Fixed
- MPO detection and timer resolution for Windows 11 24H2+ (OverlayTestMode=5 no longer sufficient, added DisableOverlays and EnableOverlay keys)

## [3.0.3] - 2026-03-12

### Changed
- Updated README for 3.0.x features

## [3.0.2] - 2026-03-10

### Fixed
- Resource leaks in WMI watchers and process handles
- Crash recovery reliability improvements
- UX improvements and cleanup

## [3.0.1] - 2026-03-08

### Fixed
- Game detection for standalone launcher installs (executables outside scanned library directories)
- Power plan expanded with display, sleep, and multimedia overrides

## [3.0.0] - 2026-03-06

### Added
- Startup update popup with GitHub release check
- 62+ power plan overrides covering processor tuning, storage, USB, wireless, idle resiliency, interrupt steering, and vendor-aware scheduling
- Anti-cheat IFEO fallback for kernel-level anti-cheat (EAC, BattlEye, RICOCHET, Vanguard)
- Full README rewrite with badges, architecture docs, and FAQ

### Changed
- Removed unused auto-generated Class1.cs
- Replaced magic strings in IsOptimizationEnabled with shared constants
- Renamed GameProfiles.GameProfile to GameSessionConfig for clarity

## [2.6.1] - 2026-03-05

### Added
- Minecraft: Java Edition built-in profile with javaw.exe command-line detection guidance
- Final Fantasy XIV built-in profile with extended session memory management

## [2.6.0] - 2026-03-04

### Added
- 10 new built-in game profiles: Counter-Strike 2, Fortnite, Apex Legends, Rust, Elden Ring, Elden Ring: Nightreign, Call of Duty, Cyberpunk 2077, Arknights: Endfield, Wuthering Waves
- Background Mode with standby list cleaning, timer resolution, power plan persistence, and task deferral
- System Tweaks panel with 7 registry-based optimizations (Game DVR, HAGS, MPO, MMCSS, Win32PrioritySeparation, Memory Integrity, Power Throttling)
- Overwatch 2 competitive preset with game-specific actions
- Intel hybrid CPU detection for 12th-14th gen P-core affinity pinning

## [2.5.2] - 2026-02-28

### Fixed
- Stability improvements and bug fixes

## [2.5.1] - 2026-02-25

### Fixed
- Minor bug fixes and performance improvements

## [2.1.0] - 2026-02-01

### Added
- Real-time hardware monitoring (CPU, GPU, RAM, VRAM, network)
- DPC Doctor with ETW-based per-driver latency attribution and automated fixes
- Session history with per-game duration, optimization details, and DPC statistics
- Dashboard with live DPC latency graph and spike detection

## [2.0.0] - 2026-01-15

### Added
- Per-game JSON profiles stored in `%AppData%/GameShift/profiles/`
- Auto game detection via Steam, Epic Games, and GOG library scanning
- WMI-based real-time process monitoring for game launch/exit detection
- 7 core optimization modules: Service Suppression, Power Plan, Timer Resolution, Process Priority, Memory Optimization, Visual Effects, Network Tuning
- SystemStateSnapshot for full reversibility of all changes
- First-run setup wizard with hardware scan and library detection
- System tray integration with session status and quick controls
