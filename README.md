<h1 align="center">GameShift</h1>

<p align="center">
  Automatic gaming performance optimizer for Windows.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/.NET-9.0-purple.svg" alt=".NET 9">
  <a href="https://github.com/lhceist41/GameShift/releases/latest"><img src="https://img.shields.io/github/v/release/lhceist41/GameShift?color=green" alt="Latest Release"></a>
  <a href="https://github.com/lhceist41/GameShift/stargazers"><img src="https://img.shields.io/github/stars/lhceist41/GameShift?style=flat" alt="Stars"></a>
</p>

---

## What is GameShift?

GameShift detects when you launch a game, applies system-level performance optimizations, and reverts everything when you're done. No manual tweaking, no leftover changes.

It replaces Process Lasso + LatencyMon + ISLC + Timer Resolution + RTSS in a single tool.

## Features

### Game Detection
- Auto-detects installed games from **Steam**, **GOG**, and **Epic Games** libraries
- Custom executable monitoring via WMI process events
- Per-game profile matching on launch

### Optimizations
GameShift ships with 11 optimization modules. Each one is independently togglable.

| Module | What it does |
|--------|-------------|
| **Service Suppressor** | Stops non-essential Windows services during gameplay |
| **Power Plan Switcher** | Switches to High Performance (or Ultimate) power plan |
| **Timer Resolution** | Sets system timer to 0.5ms for lower input latency |
| **Process Priority Booster** | Elevates game process priority and CPU affinity |
| **Network Optimizer** | Disables Nagle's algorithm, tunes TCP for lower ping |
| **GPU Driver Tweaks** | Applies registry-level NVIDIA/AMD optimizations |
| **Visual Effect Reducer** | Disables Windows transparency, animations, shadows |
| **HAGS Toggle** | Controls Hardware Accelerated GPU Scheduling |
| **MPO Toggle** | Controls Multiplane Overlay (fixes microstutter on some setups) |
| **VBS/HVCI Detection** | Flags Virtualization Based Security impact on performance |
| **Competitive Mode** | Aggressive preset: combines multiple tweaks for ranked play |

Every optimization is reverted automatically when your game closes. A `SystemStateSnapshot` captures the original state before any changes are made, so nothing is left behind.

### Background Mode
Keeps lightweight optimizations running even when no game is active. Each sub-service operates independently and automatically backs off during active gaming sessions to avoid conflicts with the main pipeline.

| Service | What it does |
|---------|-------------|
| **Standby List Cleaner** | Periodic ISLC-style memory cleanup with configurable thresholds |
| **Timer Resolution** | Maintains 0.5ms system timer for snappier desktop responsiveness |
| **Power Plan Manager** | Keeps your preferred power plan active instead of Balanced |
| **Process Priority Persistence** | Auto-applies priority rules to processes as they launch via WMI |
| **Task Deferral** | Suppresses Windows Update, Defender scans, and indexing during gaming hours |

### System Tweaks
One-time registry-level optimizations that persist across reboots. Applied from the Settings page, tracked per-tweak, and fully reversible. Original values are stored so you can revert any tweak cleanly.

| Tweak | What it does | Reboot? |
|-------|-------------|---------|
| **Disable Game DVR** | Stops background recording and overlay hooks | No |
| **Disable HAGS** | Turns off Hardware Accelerated GPU Scheduling | Yes |
| **Disable MPO** | Turns off Multiplane Overlay (fixes frame pacing on many setups) | Yes |
| **Optimize MMCSS** | Tunes Multimedia Class Scheduler for gaming priority | No |
| **Optimize Win32PrioritySeparation** | Boosts foreground process scheduling quantum | No |
| **Disable Memory Integrity** | Turns off HVCI for ~8-15% better performance (requires confirmation) | Yes |
| **Disable Power Throttling** | Prevents Windows from throttling background processes | No |

"Apply All Recommended" applies everything except the security tweak (Memory Integrity), which always requires explicit confirmation.

### Game Profiles
Per-game process-level optimizations that activate automatically when a supported game launches and revert when it closes. Runs parallel to the main optimization pipeline.

| Game | Priority | P-Core Only | Launcher Demotion | Memory Cleaning |
|------|----------|-------------|-------------------|-----------------|
| **Overwatch 2** | High | Yes | Battle.net, Agent | 512 MB threshold |
| **Valorant** | High | Yes | RiotClientServices | - |
| **League of Legends** | High | No | RiotClientServices, LeagueClient | - |
| **Deadlock** | High | Yes | - | 1 GB threshold |
| **osu!** | High | No | - | - |
| **Arknights: Endfield** | High | Yes | GRYPHLINK launcher + CEF processes | 1 GB threshold |
| **Wuthering Waves** | High | Yes | Kuro/Epic launchers | 1 GB threshold |
| **Genshin Impact** | High | No | HoYoPlay, legacy launcher | 1 GB threshold |
| **Soulframe** (placeholder) | High | No | Launcher | 1 GB threshold |

Intel hybrid CPU detection (12th-14th gen) automatically pins game processes to P-cores when beneficial. Each profile includes detailed notes and hardware-specific tips visible in the Settings page.

### Game-Specific Presets
Built-in optimization presets tailored to specific games. These go beyond the generic optimization toggles with game-aware actions that activate automatically on launch.

| Game | Presets |
|------|---------|
| **Overwatch 2** | Defender exclusions, fullscreen + DPI override, Battle.net priority reduction, firewall allow rule |
| **Valorant** | Defender exclusions, fullscreen optimization, Riot Client priority reduction, firewall allow rule |
| **League of Legends** | Defender exclusions, fullscreen + DPI override, Riot Client priority reduction, firewall allow rule |
| **Deadlock** | Defender exclusions, fullscreen optimization, firewall allow rule |
| **osu!** | Defender exclusions, fullscreen + DPI override, P-core affinity for single-threaded performance |

Each preset also includes contextual tips that show once based on your hardware (e.g., "enable NVIDIA Reflex" only appears if you have an NVIDIA GPU).

### DPC Doctor
A built-in DPC/ISR latency diagnostic tool - think LatencyMon, but integrated and with automated fixes.

- **ETW kernel tracing** with per-driver DPC/ISR attribution and execution time in microseconds
- **Live driver table** showing DPC count, peak latency, average latency, and health status per driver
- **Known driver database** with plain-English explanations of what each driver does, why it's causing problems, and how it impacts your gaming
- **Automated fixes** for common DPC issues: interrupt moderation, power saving, MSI mode, HAGS, MPO, dynamic tick, USB selective suspend, TCP task offloading
- **One-click rollback** for every fix, persisted across restarts
- **Simple/Technical mode toggle** - switch between gamer-friendly explanations and technical details
- **Post-reboot comparison** to verify fixes actually improved latency
- **GPU MSI mode detection** for NVIDIA and AMD GPUs via PCI device enumeration

Supports 10 drivers out of the box: ndis.sys, nvlddmkm.sys, dxgkrnl.sys, RTKVHD64.sys, atikmdag.sys, USBPORT.sys, ACPI.sys, Wdf01000.sys, storport.sys, and tcpip.sys.

### System Overview
Real-time hardware monitoring page showing:
- CPU usage, temperature, and clock speed
- GPU usage, temperature, VRAM, and clock speed
- RAM usage and availability
- Disk activity
- Network ping latency to configurable target (default: 8.8.8.8)

### Dashboard
- Live DPC latency graph with spike detection and troubleshooter
- GPU info and optimization status at a glance
- Active game session monitoring with per-session DPC statistics
- Activity feed showing what GameShift is doing and when
- One-click navigation to DPC Doctor from spike alerts
- In-app auto-updater with download progress bar

### Per-Game Profiles
- Profiles auto-switch when a game is detected
- Choose which optimizations apply to each game
- Fullscreen optimization toggle per game
- DPC latency threshold per game
- Exe-level overrides for edge cases

### Quality of Life
- **Global hotkey** (Ctrl+Shift+G) to pause/resume monitoring
- **Export/import** settings and profiles across machines
- **First-run wizard** with hardware auto-detection and DPC baseline measurement
- **In-app auto-updater** --download and apply updates without leaving the app
- **In-app log viewer** with search, auto-refresh, and quick access to log files
- **System tray** with flyout status, quick profile switch, and session summary toasts
- **Post-session toasts** showing game duration, optimization count, and DPC stats
- **Single instance** enforcement (second launch brings existing window to front)
- **Remember window position** across restarts
- **Notification preferences** --per-type toast control, suppress during gaming option

## Quick Start

### Requirements
- Windows 10 or later
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Administrator recommended (required for timer resolution, service control, power plan switching, DPC Doctor)

### Install

1. Download the latest release from the [Releases page](https://github.com/lhceist41/GameShift/releases/latest)
2. Run `GameShift.exe`
3. Complete the setup wizard (hardware scan + library detection)
4. Add your games, then just play. GameShift handles the rest.

## Building from Source

```bash
git clone https://github.com/lhceist41/GameShift.git
cd GameShift
dotnet build
dotnet run --project src/GameShift.App
```

To publish a single-file executable:

```bash
dotnet publish src/GameShift.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## How It Works

GameShift uses WMI process creation/deletion events to detect game launches in real time. When a game starts, the detection orchestrator matches it against your library and loads the appropriate profile.

The optimization engine applies each enabled module in sequence. Before making any change, it captures a `SystemStateSnapshot` of the original value (registry key, service state, power plan GUID, etc.). When all games close, every change is reverted using that snapshot.

DPC latency is monitored via Windows performance counters throughout the session. Spikes above your threshold trigger tray warnings so you can identify problematic drivers. For deeper analysis, DPC Doctor uses ETW kernel trace sessions with per-driver attribution --the same technique LatencyMon uses internally.

## Configuration

Settings are stored at:
```
%AppData%\GameShift\settings.json
```

Logs are written to:
```
%AppData%\GameShift\logs\gameshift-YYYYMMDD.log
```

You can export your full configuration (settings + profiles) from the Settings page and import it on another machine.

The default global hotkey is **Ctrl+Shift+G**. You can rebind it in Settings.

## Why not just use X?

| Tool | The problem |
|------|------------|
| **Razer Cortex** | Bloated, unreliable restore, phones home, bundled with Razer software |
| **Process Lasso** | Great at what it does, but it only does one thing (process priority/affinity) |
| **LatencyMon** | Read-only diagnostics with no fix capability. GameShift's DPC Doctor diagnoses and fixes |
| **Windows Game Mode** | Minimal measurable impact. Doesn't touch services, timers, or network stack |
| **Manual registry tweaks** | Works until you forget to revert them and wonder why your desktop is broken |

GameShift does all of this automatically, reverts cleanly, and stays out of your way.

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 9 |
| UI Framework | WPF |
| UI Controls | [WPF-UI](https://github.com/lepoco/wpfui) (Fluent design) |
| System Tray | [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) |
| ETW Tracing | [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) |
| MVVM | CommunityToolkit.Mvvm |
| Logging | Serilog (rolling file sink) |
| WMI/Services | System.Management, System.ServiceProcess |
| Testing | xUnit, Moq |

## Project Structure

```
src/
  GameShift.App/         # WPF application (views, viewmodels, services)
  GameShift.Core/        # Core logic (detection, optimization, profiles, monitoring)
  GameShift.Tests/       # Unit tests
```

## Roadmap

Planned features (no promises, no timeline):

- [ ] In-game overlay HUD (FPS, DPC latency, optimization status)
- [ ] Plugin system for community-built optimization modules
- [ ] Benchmark integration (before/after comparisons)
- [ ] Steam Deck / Linux support via Proton
- [ ] Per-monitor refresh rate switching
- [ ] Discord Rich Presence integration

## Contributing

1. Fork the repo
2. Create a feature branch (`git checkout -b feature/your-feature`)
3. Make your changes
4. Run tests (`dotnet test`)
5. Open a PR against `master`

**Project layout:**
- `GameShift.App` is the UI layer. Views, ViewModels, services, and WPF plumbing live here.
- `GameShift.Core` is the logic layer. Detection, optimization modules, profiles, and monitoring. No UI dependencies.
- `GameShift.Tests` contains xUnit tests with Moq for mocking.

If you're adding a new optimization, implement the `IOptimization` interface in `GameShift.Core/Optimization/` and register it in `OptimizationEngine`.

## License

[MIT](LICENSE)

---

<p align="center">
  <sub>GameShift modifies system settings during gameplay. All changes are reversible, but use at your own risk.</sub>
</p>
