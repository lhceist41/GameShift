# Changelog

All notable changes to GameShift are documented here.

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
