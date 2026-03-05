# Security Policy

## Supported Versions

| Version | Supported |
|:--------|:---------:|
| 2.6.x   | ✅        |
| < 2.6   | ❌        |

## Reporting a Vulnerability

If you discover a security vulnerability in GameShift, please report it responsibly.

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, email the maintainer directly or use [GitHub's private vulnerability reporting](https://github.com/lhceist41/GameShift/security/advisories/new).

### What to include

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

### Response timeline

- **Acknowledgment:** Within 48 hours
- **Assessment:** Within 7 days
- **Fix release:** As soon as practical, depending on severity

## Scope

GameShift modifies system-level settings (services, registry keys, power plans, process priority) and requires administrator privileges. The following are in scope for security reports:

- Privilege escalation beyond intended functionality
- Unauthorized registry or filesystem modifications
- Data exfiltration or unintended network activity
- Vulnerabilities in third-party dependencies

## Security Design

- GameShift makes **no network calls** and sends **no telemetry**
- All data is stored locally in `%AppData%\GameShift\`
- All system modifications are recorded in `SystemStateSnapshot` for full reversibility
- Source code is open and auditable
