# Security Policy

## Supported versions

GameShift is a one-person project, so I keep this simple: I support the latest release. If you're on an older build, please update before reporting something. There's a good chance it's already fixed.

## Reporting a vulnerability

GameShift runs with admin rights and touches a lot of the system, so if you find a security hole I genuinely want to hear about it.

**Please don't open a public issue for security problems.** That just tells everyone how to exploit it before there's a fix.

Instead, use [GitHub's private vulnerability reporting](https://github.com/lhceist41/GameShift/security/advisories/new). It comes straight to me and stays private until it's sorted out.

Helpful things to include:

- What the vulnerability is
- How to reproduce it
- What an attacker could actually do with it
- Which part it affects (main app, updater, state journal, DPC Doctor, etc.)
- A suggested fix, if you have one

It's just me here, so I can't promise a corporate-style response time. But I read every report, and I'll get back to you as soon as I reasonably can. Security stuff jumps the queue.

## Scope

GameShift operates with administrator privileges and modifies system-level settings including services, registry keys, power plans, BCD boot configuration, process priority, CPU scheduling, interrupt affinity, and ETW sessions. The following components and concerns are in scope for security reports:

### Application components

- **GameShift.App**  - Main WPF application (runs as administrator)
- **Updater**  - Checks GitHub Releases, downloads a new `GameShift.App.exe` when you accept an update, verifies its SHA-256, and replaces the running exe
- **State Journal**  - Optimization state persisted to `%ProgramData%\GameShift\state.json`

The source also contains **GameShift.Watchdog** (a Windows service that would run as SYSTEM) and a **boot-recovery scheduled task** (would also run as SYSTEM). Neither ships in releases or is installed by the app yet. Reports about them are still welcome, since they're meant to ship later.

### In-scope vulnerabilities

- Privilege escalation beyond intended functionality
- Files GameShift reads from places a standard user can write (settings, profiles, its own folder, downloads) being used to steer an elevated action
- Abuse of the watchdog code or its named pipe (`\\.\pipe\GameShiftWatchdog`) for unauthorized system modifications
- Unauthorized or unintended registry, BCD, or filesystem modifications
- State journal tampering leading to incorrect system state restoration
- ETW session abuse or information disclosure
- Data exfiltration or unintended network activity
- Vulnerabilities in third-party dependencies (NvAPI, ADLX, LibreHardwareMonitor, TraceEvent)

## Security Design

- GameShift's only network activity is checking GitHub Releases for updates (and downloading one when you accept it) and the dashboard's latency monitor, which pings a configurable host (`8.8.8.8` by default)
- No telemetry is collected or transmitted
- Keep `GameShift.App.exe` in a folder only administrators can change, such as `C:\Program Files\GameShift`. GameShift runs elevated, so a folder standard users can write to (Downloads, the Desktop, or a folder created directly under `C:\`) lets them influence what runs with admin rights
- Application data is stored locally in `%AppData%\GameShift\` (user settings and profiles)
- System recovery data is stored in `%ProgramData%\GameShift\` (state journal), and GameShift sets that folder's permissions so standard users can only read it
- All system modifications are recorded in an atomic state journal with original values for deterministic rollback
- Every change is reverted, with verification, when your game exits. Automatic recovery after an app crash or blue screen (the watchdog service and boot-recovery task) is still in development and not enabled in releases
- Registry changes are monitored via `RegNotifyChangeKeyValue` to detect external tampering during sessions
- The watchdog named pipe accepts only heartbeat signals - it does not accept or execute commands
- BCDEdit modifications are gated behind user confirmation and tracked in pending reboot fixes
- VBS/HVCI disable is blocked when Riot Vanguard is detected (safety interlock)
- The "Disable Memory Integrity" system tweak now also disables VBS (`EnableVirtualizationBasedSecurity`) and clears `RequirePlatformSecurityFeatures`. UEFI-locked VBS may require additional BIOS changes.
- Source code is fully open and auditable
