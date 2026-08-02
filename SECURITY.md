# Security policy

## Supported versions

Security fixes are applied to the latest published minor release.

| Version | Supported |
|---|---|
| 1.1.x | Yes |
| Earlier versions | No |

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting flow:

https://github.com/ntphucc/directory-monitor/security/advisories/new

Include the affected version, impact, reproduction steps, and a minimal proof of concept. Remove database credentials, connection strings, private paths, machine names, customer data, and encrypted transfer files before submitting any evidence.

The maintainer will acknowledge the report, investigate it privately, and coordinate disclosure after a fix is available. Please do not disclose the issue publicly before that coordination is complete.

## Security scope

Reports involving credential protection, update verification, import/export encryption, database migrations, path launching, installer elevation, log redaction, or unauthorized filesystem disclosure are especially useful.

Operational support requests and non-security defects should use the public issue templates instead.
