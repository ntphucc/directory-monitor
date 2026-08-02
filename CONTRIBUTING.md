# Contributing

Thank you for improving Directory Monitor. Keep changes focused, testable, and safe for a public repository.

## Development setup

Use Windows and install the .NET SDK selected by `global.json`.

```powershell
git clone https://github.com/ntphucc/directory-monitor.git
cd directory-monitor
dotnet restore DirectoryMonitor.slnx --locked-mode
dotnet build DirectoryMonitor.slnx --configuration Release --no-restore
dotnet test DirectoryMonitor.slnx --configuration Release --no-build --no-restore
```

Open `DirectoryMonitor.slnx` in a compatible Visual Studio version for WPF development.

## Change workflow

1. Create a short-lived branch from `main`.
2. Keep commits small and use descriptive prefixes such as `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, or `chore:`.
3. Add or update tests for behavior changes.
4. Update documentation and release notes for user-visible changes.
5. Run the locked restore, Release build, and complete test suite.
6. Open a pull request using the repository template.

## Architecture rules

- Keep business models independent of WPF and database providers.
- Add infrastructure behind interfaces owned by the Application layer.
- Do not perform I/O in `FileSystemWatcher` callbacks or on the WPF UI thread.
- Persist an event and its destination delivery records atomically.
- Preserve per-destination isolation: one failing destination must not block another.
- Keep English and Vietnamese localization dictionaries in key parity.

## Testing

The default suite must not require external PostgreSQL or SQL Server instances. Optional live database tests use explicit environment variables documented in `docs/TEST_REPORT.md`.

For UI changes, verify English and Vietnamese, Light and Dark themes, keyboard navigation, tooltips, and 100%/125% display scaling when relevant.

## Public repository safety

Never commit:

- Connection strings, passwords, tokens, certificates, or private keys.
- SQLite databases, WAL/SHM files, logs, `.dm` exports, update downloads, or build artifacts.
- Customer names, UNC shares, internal hostnames/IP addresses, screenshots containing private paths, or production event data.

Use placeholders such as `SERVER_NAME`, `DATABASE_NAME`, `USER_NAME`, and `PASSWORD` in examples. Sanitize logs before attaching them to issues or pull requests.

Security vulnerabilities must follow `SECURITY.md` and must not be filed as public issues.
