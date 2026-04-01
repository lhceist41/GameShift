# Changelog

All notable changes to GameShift are documented here.

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
