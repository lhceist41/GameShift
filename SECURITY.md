# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 3.5.x   | ✅        |
| < 3.5   | ❌        |

## Reporting a Vulnerability

If you discover a security vulnerability in GameShift, please report it responsibly.

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, use [GitHub's private vulnerability reporting](https://github.com/lhceist41/GameShift/security/advisories/new) or email the maintainer directly.

### What to include

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Affected component (main app, watchdog service, boot recovery, etc.)
- Suggested fix (if any)

### Response timeline

- **Acknowledgment:** Within 48 hours
- **Assessment:** Within 7 days
- **Fix release:** As soon as practical, depending on severity

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
- The watchdog named pipe accepts only heartbeat signals  - it does not accept or execute commands
- BCDEdit modifications are gated behind user confirmation and tracked in pending reboot fixes
- VBS/HVCI disable is blocked when Riot Vanguard is detected (safety interlock)
- Source code is fully open and auditable
