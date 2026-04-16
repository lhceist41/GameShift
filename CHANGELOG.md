# Changelog

All notable changes to GameShift are documented here.

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
