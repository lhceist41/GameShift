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
- Which part it affects (main app, watchdog, boot recovery, etc.)
- A suggested fix, if you have one

It's just me here, so I can't promise a corporate-style response time. But I read every report, and I'll get back to you as soon as I reasonably can. Security stuff jumps the queue.

## Scope

GameShift operates with administrator privileges and modifies system-level settings including services, registry keys, power plans, BCD boot configuration, process priority, CPU scheduling, interrupt affinity, and ETW sessions. The following components and concerns are in scope for security reports:

### Application components

- **GameShift.App**  - Main WPF application (runs as administrator)
- **GameShift.Watchdog**  - Windows Service monitoring the main app via named pipe heartbeat (runs as SYSTEM)
- **Boot Recovery Task**  - Scheduled task running at startup under SYSTEM to restore state after crashes
- **State Journal**  - Optimization state persisted to `%ProgramData%\GameShift\state.json`

### In-scope vulnerabilities

- Privilege escalation beyond intended functionality
- Abuse of the watchdog service or named pipe (`\\.\pipe\GameShiftWatchdog`) for unauthorized system modifications
- Unauthorized or unintended registry, BCD, or filesystem modifications
- State journal tampering leading to incorrect system state restoration
- ETW session abuse or information disclosure
- Data exfiltration or unintended network activity
- Vulnerabilities in third-party dependencies (NvAPI, ADLX, LibreHardwareMonitor, TraceEvent)

## Security Design

- GameShift makes **no network calls** except to check for updates on GitHub Releases
- No telemetry is collected or transmitted
- Application data is stored locally in `%AppData%\GameShift\` (user settings and profiles)
- System recovery data is stored in `%ProgramData%\GameShift\` (state journal, watchdog logs)
- All system modifications are recorded in an atomic state journal with original values for deterministic rollback
- A three-layer crash recovery system (state journal, watchdog service, boot recovery task) ensures modifications are never left orphaned
- Registry changes are monitored via `RegNotifyChangeKeyValue` to detect external tampering during sessions
- The watchdog named pipe accepts only heartbeat signals - it does not accept or execute commands
- BCDEdit modifications are gated behind user confirmation and tracked in pending reboot fixes
- VBS/HVCI disable is blocked when Riot Vanguard is detected (safety interlock)
- The "Disable Memory Integrity" system tweak now also disables VBS (`EnableVirtualizationBasedSecurity`) and clears `RequirePlatformSecurityFeatures`. UEFI-locked VBS may require additional BIOS changes.
- Source code is fully open and auditable
