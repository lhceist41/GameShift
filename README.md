<h1 align="center">GameShift</h1>

<p align="center">
  <a href="https://github.com/lhceist41/GameShift/releases/latest"><img src="https://img.shields.io/github/v/release/lhceist41/GameShift?style=for-the-badge&color=00c853" alt="Latest Release"/></a>
  <img src="https://img.shields.io/github/downloads/lhceist41/GameShift/total?style=for-the-badge&color=00f0ff" alt="Total Downloads"/>
  <img src="https://img.shields.io/badge/.NET-9-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 9"/>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078d4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10/11"/>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/lhceist41/GameShift?style=for-the-badge" alt="License"/></a>
  <a href="https://github.com/lhceist41/GameShift/stargazers"><img src="https://img.shields.io/github/stars/lhceist41/GameShift?style=for-the-badge&color=f5c542" alt="Stars"/></a>
</p>

<p align="center">
  <b>Automatic, reversible Windows optimization for every game in your library.</b><br/>
  GameShift detects your games, applies system-level tweaks per title,<br/>
  and restores everything the moment you stop playing.
</p>

---

<details>
<summary><strong>Table of Contents</strong></summary>

- [Features](#-features)
- [How It Works](#%EF%B8%8F-how-it-works)
- [Quick Start](#-quick-start)
- [System Requirements](#-system-requirements)
- [Safety & Transparency](#%EF%B8%8F-safety--transparency)
- [Anti-Cheat Compatibility](#-anti-cheat-compatibility)
- [FAQ](#-faq)
- [Contributing](#-contributing)
- [License](#-license)

</details>

---

## ✨ Features

> [!NOTE]
> Every optimization is fully reversible. GameShift snapshots your settings before applying changes and auto-reverts when your game exits.

### Session Optimizations

These activate when a game launches and revert when the game closes.

| Optimization | What it does |
|:-------------|:-------------|
| **Service Suppression** | Pauses 18+ non-essential Windows services (telemetry, indexing, Xbox services, Print Spooler, Fax, etc.) during gameplay. Supports Tier 1 and Tier 2 service categories. |
| **Power Plan Switching** | Switches to Ultimate Performance scheme during the gaming session. Creates the plan if missing. |
| **Timer Resolution** | Sets system timer to 0.5ms for lower input latency and tighter frame pacing. |
| **Process Priority Booster** | Elevates the game process to High priority and sets optimal Win32PrioritySeparation (0x2A). Auto-detects anti-cheat and falls back to IFEO registry when needed. |
| **Memory Optimizer** | Monitors available RAM and purges the standby list when it drops below threshold. Includes modified page flushing and background process memory priority management. |
| **Visual Effect Reducer** | Disables Windows transparency and animations via registry and SystemParametersInfo. |
| **Network Tuning** | Disables Nagle's algorithm (`TcpAckFrequency`, `TCPNoDelay`) on all interfaces and stops Delivery Optimization. |
| **Hybrid CPU Pinning** | Detects P-cores vs E-cores on Intel 12th-14th gen and AMD hybrid CPUs. Pins the game to performance cores only. Supports AMD X3D V-Cache CCD pinning. |
| **CPU Core Unparking** | Unparks all CPU cores and optionally disables processor idle (forces C0 state) to eliminate C-state transition latency. Vendor-aware parking values for AMD X3D dual-CCD. |
| **MPO Disable** | Disables Multiplane Overlay to fix micro-stutter on multi-monitor setups with mismatched refresh rates. Includes Windows 24H2 fallback via `DisableOverlays`. |
| **Competitive Mode** | Suspends overlay processes (Discord, Steam, NVIDIA), kills GPU-hungry background apps (Widgets, Edge WebView), respects anti-cheat blocklists. |
| **GPU Driver Optimization** | Auto-detects NVIDIA or AMD and applies vendor-specific registry tweaks: low latency mode, 16GB shader cache, maximum performance power mode. |
| **Scheduled Task Suppression** | Disables resource-heavy Windows scheduled tasks (telemetry, defrag, update tasks) during gameplay. Optional Defender scan suppression. |
| **I/O Priority Management** | Lowers I/O priority of background processes during gameplay to reduce disk contention. |
| **Efficiency Mode Control** | Applies Windows 11 Efficiency Mode to background processes during gameplay, constraining them to E-cores on hybrid CPUs. Gracefully skips on Windows 10. |

### Background Mode (Always-On)

These run 24/7 when Background Mode is enabled, independent of gaming sessions.

| Service | What it does |
|:--------|:-------------|
| **Standby List Cleaner** | Polls available RAM every 10 seconds and purges the standby list when it drops below a configurable threshold. |
| **Timer Resolution Lock** | Maintains high timer resolution at all times (0.5ms default). |
| **Custom Power Plan** | Creates a "GameShift Performance" plan cloned from Ultimate Performance with 62+ aggressive overrides covering processor tuning, storage, USB, wireless, idle resiliency, interrupt steering, and vendor-aware heterogeneous scheduling (Intel hybrid, AMD single/dual-CCD). Includes 3-state management: Gaming, Desktop, and Idle (auto-switches to Balanced after configurable idle timeout). |
| **Task Deferral** | Defers Windows scheduled tasks during active gaming sessions. |
| **Process Priority Persistence** | Applies persistent priority rules to background processes (e.g., always keep Chrome at BelowNormal). Respects active Game Profile sessions. |

### Monitoring and Diagnostics

| Feature | Description |
|:--------|:------------|
| **Real-time Performance** | Live CPU, GPU, RAM, VRAM, and network ping telemetry with per-session sparkline graphs. |
| **Temperature Monitoring** | CPU and GPU temperature tracking via LibreHardwareMonitor. |
| **DPC Latency Doctor** | ETW-based per-driver DPC/ISR attribution with automated fixes and one-click rollback. Includes Simple Mode and Technical Mode views. |
| **DPC Latency Monitor** | Passive latency sampling during gaming sessions with configurable spike thresholds and toast notifications. |
| **Session History** | Post-session reports with duration, optimization count, DPC statistics, and per-game tracking. |
| **Driver Version Tracker** | Detects installed GPU and audio drivers, checks against known advisory database, flags problematic versions. |
| **Benchmarking** | PresentMon-based frame time capture for performance measurement. |

### System Tweaks

One-time registry optimizations that persist across reboots (applied once, can be reverted):

- Disable Game DVR and Game Bar
- Disable Hardware Accelerated GPU Scheduling (HAGS)
- Disable Multiplane Overlay (MPO)
- Optimize MMCSS for gaming
- Optimize Win32PrioritySeparation
- Disable Power Throttling
- Disable Memory Integrity (VBS/HVCI) with anti-cheat safety gating
- Disable Full-Screen Optimizations

### Game Library and Profiles

- **Auto-detection** from Steam, Epic Games, GOG Galaxy, and Xbox/Game Pass install directories
- **19 built-in game profiles** with hardware-specific tuning and anti-cheat metadata for titles including Overwatch 2, Valorant, CS2, Fortnite, Apex Legends, Deadlock, osu!, Elden Ring, Elden Ring Nightreign, Arknights: Endfield, Wuthering Waves, Genshin Impact, Cyberpunk 2077, Minecraft Java, FFXIV, Rust, Soulframe, Call of Duty, and League of Legends
- **Per-game toggle control** for every optimization, with sub-toggles for advanced options
- **Competitive presets** for broad system-level optimization profiles
- **Manual game adding** for any executable not in a scanned library

### Application Features

- **Startup update popup** with download-and-install directly from the notification window, skip version support, and progress tracking
- **First-run setup wizard** with library scanning and hardware detection
- **System tray integration** with context menu, status icons, quick profile switching, and auto-start with Windows
- **Global hotkey** (Ctrl+Shift+G) to toggle monitoring pause
- **Auto-updater** with GitHub release checking, in-app download, and staged file replacement
- **Crash recovery** via SystemStateSnapshot lockfile. Detects orphaned sessions on startup and restores original system state including power plans, services, registry values, IFEO entries, processor idle state, CPU parking, and scheduled tasks.
- **Single-instance enforcement** with named pipe communication to bring existing window to front

---

## ⚙️ How It Works

```mermaid
graph LR
    A[Game Launched] --> B[Profile Loaded]
    B --> C[Optimizations Applied]
    C --> D[Gaming Session]
    D --> E[Game Exits]
    E --> F[Settings Restored]
```

GameShift uses WMI process creation events to detect game launches in real time. When a game starts, the detection orchestrator matches it against your library, loads the appropriate profile, and applies each enabled optimization in sequence. Before making any change, a `SystemStateSnapshot` captures the original value so every modification is cleanly reverted when the game closes.

For games with kernel-level anti-cheat (EAC, BattlEye, RICOCHET, TencentACE), GameShift automatically falls back to IFEO (Image File Execution Options) registry-based priority and affinity settings instead of runtime API calls that the anti-cheat would block.

---

## 🚀 Quick Start

### Download (recommended)

1. Grab the latest `GameShift.App.exe` from the [Releases page](https://github.com/lhceist41/GameShift/releases/latest)
2. Run as **Administrator** (required for timer resolution, service control, and power plan switching)
3. Complete the first-run wizard (GameShift auto-detects your installed games and runs a hardware scan)

### Build from Source

**Prerequisites:** .NET 9 SDK, Visual Studio 2022 17.12+, Windows 10 21H2+ (x64)

```bash
git clone https://github.com/lhceist41/GameShift.git
cd GameShift
dotnet restore
dotnet build --configuration Release
dotnet run --project src/GameShift.App
```

---

## 💻 System Requirements

| Requirement | Details |
|:------------|:--------|
| **OS** | Windows 10 version 2004+ / Windows 11 |
| **Architecture** | x64 |
| **RAM** | 4 GB minimum |
| **Runtime** | .NET 9 (bundled with release builds) |
| **Privileges** | Administrator (required to modify services, registry keys, timer resolution, and power plans) |

---

## 🛡️ Safety & Transparency

> [!CAUTION]
> Create a System Restore point before your first use. GameShift modifies system-level settings that are all reversible, but a restore point provides an extra safety net.

### What GameShift changes (and how it reverts)

- **Services**: Temporarily sets non-essential services to Manual start type. Original start types are recorded in the SystemStateSnapshot and restored when the game exits.
- **Power plan**: Creates or switches to the Ultimate Performance power scheme (or the custom "GameShift Performance" plan in Background Mode). Your original power plan GUID is saved and re-applied on revert.
- **Registry keys**: Writes timer resolution, GPU driver settings, network tuning, and IFEO entries. All original values are backed up in the session lockfile before any write.
- **Process priority**: Elevates the game process to High and optionally pins CPU affinity. Changes apply only while the game is running and are released on exit. For anti-cheat-protected games, uses IFEO PerfOptions registry keys that are cleaned up on revert.
- **Scheduled tasks**: Temporarily disables resource-heavy tasks. Task paths are recorded and re-enabled when the session ends.

### Crash recovery

All session state is persisted to `%AppData%/GameShift/active_session.json` as a lockfile. If GameShift crashes mid-session, the next startup detects the orphaned lockfile and restores: processor idle state, CPU parking settings, IFEO registry entries, scheduled tasks, and power plans.

### Why administrator privileges are required

Windows protects service configuration and `HKLM` registry access behind administrator-level permissions. GameShift makes **no network calls** except to check for updates on GitHub, collects **no telemetry**, and stores **all data locally** on your machine.

### Transparency guarantees

- Source code is fully open and auditable (see [`src/`](src/))
- All optimization logic lives in [`src/GameShift.Core/Optimization/`](src/GameShift.Core/Optimization/)

---

## 🔒 Anti-Cheat Compatibility

GameShift includes built-in anti-cheat detection and compatibility for all major systems:

| Anti-Cheat | Status | Approach |
|:-----------|:-------|:---------|
| **Riot Vanguard** (Valorant, LoL) | Fully compatible | VBS/HVCI safety gating prevents conflicts. GameShift blocks VBS disable when Vanguard is detected. |
| **Easy Anti-Cheat** (Fortnite, Apex, Rust, Elden Ring) | Fully compatible | IFEO registry fallback for priority/affinity instead of blocked runtime API calls. |
| **BattlEye** (Arknights Endfield) | Fully compatible | IFEO registry fallback. |
| **RICOCHET** (Call of Duty) | Fully compatible | IFEO registry fallback. |
| **TencentACE** (Wuthering Waves) | Fully compatible | IFEO registry fallback. |
| **Valve Anti-Cheat** (CS2, Deadlock) | Fully compatible | User-mode only, no restrictions on system-level tools. |
| **FACEIT AC** | Fully compatible | VBS/HVCI safety gating for kernel-level enforcement. |

GameShift does **not** inject into game processes, does **not** modify game files, and does **not** hook into game memory. All optimizations operate at the Windows system level.

> [!IMPORTANT]
> GameShift automatically detects anti-cheat systems via Windows service queries, driver files, and registry keys. When kernel-level anti-cheat blocks runtime process manipulation, GameShift switches to registry-based IFEO PerfOptions. Always verify with your specific game's Terms of Service.

---

## ❓ FAQ

<details>
<summary><strong>Will this get me banned?</strong></summary>
<br/>
No. GameShift modifies Windows system settings, not game files, game memory, or game processes. Anti-cheat systems target memory injection, aimbots, and wallhacks. Changing your power plan or clearing the standby list is not detectable and not against any game's Terms of Service. Thousands of players use the same underlying tools (ISLC, Process Lasso, timer resolution utilities) without issue.
</details>

<details>
<summary><strong>Does it work with Game Pass / Xbox games?</strong></summary>
<br/>
Yes. GameShift scans Xbox/Game Pass install directories alongside Steam, Epic, and GOG. System-level optimizations (power plan, timer resolution, memory cleaning, network tuning) work identically on all titles. Process priority elevation may be limited on some UWP-packaged titles due to Windows sandboxing.
</details>

<details>
<summary><strong>Can I use it on a laptop?</strong></summary>
<br/>
Yes. GameShift is particularly useful on laptops where Windows aggressively throttles performance to save battery. The power plan switch and power throttling disable can unlock significant FPS gains. All power plan overrides set both AC (plugged in) and DC (battery) values. The idle timeout in Background Mode automatically switches to Balanced when you step away. Just make sure you are plugged in during gaming sessions.
</details>

<details>
<summary><strong>What happens if my PC crashes during optimization?</strong></summary>
<br/>
All original values are saved to <code>%AppData%/GameShift/active_session.json</code> before any change is made. On next launch, GameShift detects the incomplete session and automatically restores your previous settings, including processor idle state, CPU parking, IFEO registry entries, and scheduled tasks. Services revert to their default start type on reboot regardless, and power plan changes persist only if explicitly saved.
</details>

<details>
<summary><strong>What is the "GameShift Performance" power plan?</strong></summary>
<br/>
When Background Mode is enabled, GameShift creates a custom power plan cloned from Ultimate Performance with 62+ aggressive overrides. These cover processor boost mode, minimum processor state, USB selective suspend, PCI Express link state, hard disk timeout, NVMe power management, wireless adapter power saving, idle resiliency, interrupt steering, and vendor-aware heterogeneous scheduling for Intel hybrid and AMD single/dual-CCD processors. The plan is managed with a 3-state system: Gaming (idle disabled), Desktop (custom plan active), and Idle (auto-switches to Balanced after configurable timeout).
</details>

<details>
<summary><strong>How do I create a custom profile for a game?</strong></summary>
<br/>
Open GameShift, navigate to the Games tab, and click <strong>Add Game</strong>. Browse to the game executable, name the profile, and toggle which optimizations you want applied. The profile is saved as a JSON file in <code>%AppData%/GameShift/profiles/</code> and activates automatically whenever that executable launches.
</details>

<details>
<summary><strong>Why does Windows Defender flag GameShift?</strong></summary>
<br/>
GameShift modifies Windows services, writes to protected registry keys, and changes system timer resolution. These behaviors are sometimes flagged as suspicious by heuristic scanners. The application is open source and you can audit every line. If Defender quarantines the executable, add an exclusion for <code>GameShift.App.exe</code> or build from source yourself.
</details>

---

## 🤝 Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines.

```bash
# 1. Fork the repository

# 2. Create a feature branch
git checkout -b feature/your-feature

# 3. Make your changes and run tests
dotnet test

# 4. Commit
git commit -m "feat: add your feature"

# 5. Push and open a PR
git push origin feature/your-feature
```

Issues and feature requests are always welcome. Open one on the [Issues page](https://github.com/lhceist41/GameShift/issues).

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

---

<p align="center">
  If GameShift improved your gaming experience, consider leaving a ⭐ on the repo.
</p>
