# Changelog

All notable changes to Directory Monitor are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- GitHub Actions CI with Windows Release build, tests, and coverage artifacts.
- CodeQL scanning, Dependabot updates, issue forms, pull request checklist, and CODEOWNERS.
- Repository-wide SDK pinning, central NuGet package versions, formatting rules, and locked dependency restore.
- Public architecture, contribution, and security documentation.

## [1.1.8] - 2026-08-02

### Added

- External reloadable `appsettings.json` for SQLite and Serilog configuration.
- Daily operational logging, lifecycle logging, and global exception handling.
- System, Light, and Dark theme modes with improved control contrast.
- Directory and Output context actions, duplication, double-click editing, and safe delete behavior.
- Event-grid copy actions and UI-only event clearing.
- JSON, XML, and encrypted `.dm` import/export for directories, visible events, and outputs.

### Changed

- Database and imported configuration remain disabled until explicitly reviewed and enabled.
- Imported events use machine-scoped sequence identity and are never replayed to outputs.

[Unreleased]: https://github.com/ntphucc/directory-monitor/compare/v1.1.8...HEAD
[1.1.8]: https://github.com/ntphucc/directory-monitor/releases/tag/v1.1.8
