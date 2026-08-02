# Directory Monitor

Windows desktop application for monitoring filesystem changes and delivering normalized events to text files, PostgreSQL, SQL Server, and Windows Application Event Log.

Current application version: **1.1.8**.

## Requirements

- Windows 10/11 or Windows Server 2022/2025 with Desktop Experience.
- .NET 9 SDK for development. Release publishing is self-contained (`win-x64`).
- Database credentials need DDL permissions during first schema initialization or migration.

## Build and run

```powershell
dotnet restore DirectoryMonitor.slnx
dotnet build DirectoryMonitor.slnx
dotnet test DirectoryMonitor.slnx
dotnet run --project src/DirectoryMonitor.Presentation
```

The SQLite connection string is configured in the external `appsettings.json`. Its default stores configuration, snapshots, imported events, and the durable outbox beside the executable in:

```text
<Directory Monitor.exe folder>\Data\directory-monitor.db
```

For development or tests, override the folder with `DIRECTORY_MONITOR_DATA`. On first startup after an upgrade, if the new database does not exist, the application copies the legacy `%ProgramData%\Directory Monitor\directory-monitor.db` database with SQLite backup and leaves the legacy file untouched.

`appsettings.json` is watched for changes. A changed SQLite connection string is shown as **restart required** and does not switch the active outbox while the app is running. Serilog settings reload live; daily UTF-8 logs are written by default to `<app>\Logs` and retained for 30 days. Connection strings and import/export passwords are never written to operational logs.

The database is created during the first successful startup, so a newly extracted package initially contains only `Data\README.txt`. The Settings tab shows the resolved absolute database path and provides an **Open folder** button. SQLite can create `directory-monitor.db-wal` and `directory-monitor.db-shm` beside the database while the app is running.

## Outputs

- **Text:** UTF-8 append-only logs with Plain Text, JSON Lines, CSV, or a custom token template.
- **PostgreSQL 14+:** creates/migrates tables under the default `public` schema automatically.
- **SQL Server 2019+:** creates/migrates tables under `dbo` automatically.
- **Windows Event Log:** writes to `Windows Logs > Application` with source `Directory Monitor`; the installer registers the source.

Database delivery is idempotent for both the event UUID and the per-machine sequence. If an already delivered event is found again, it is treated as delivered instead of remaining in the retry queue. On the Outputs tab, select a failed output to see the complete multiline error in a selectable details panel or copy it with **Copy error**.

Connection strings are encrypted with Windows DPAPI CurrentUser. A database destination is tested and initialized before it can be saved.

## Updates

Automatic updates are opt-in: the manifest URL is empty by default. In **Settings > Application updates**, enter an anonymous HTTPS manifest URL and select Every startup, Daily, Monthly, or Never. Manual **Check now** works for every schedule, including Never.

The app accepts only HTTPS manifest/installer URLs, stable `major.minor.patch` versions, installers up to 1 GB, and downloads whose byte size and SHA-256 exactly match the manifest. Before elevation it creates a consistent SQLite backup. The first installer update can move a portable installation into Program Files without overwriting an existing target database.

See [docs/UPDATE_MANIFEST.md](docs/UPDATE_MANIFEST.md) for publishing and security guidance.

## Release

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```

Published files are written to `artifacts/publish`. Inno Setup 6 is required to produce the installer used by automatic updates. Package and installer names use the version in `Directory.Build.props`.

The repository uses `DirectoryMonitor.slnx` as its only solution file.

All ListBox items use vertically centered content. The main title bar includes an explicit **Exit** button that confirms before stopping monitoring; the standard window **X** continues to minimize the app to the system tray.

The Settings tab supports **System**, **Light**, and **Dark** themes. System mode follows Windows theme changes while the app is running.

## Import and export

Settings provides Import/Export for directories, the currently filtered in-memory Event grid, and outputs:

- JSON and XML are readable formats and intentionally omit PostgreSQL/SQL Server credentials.
- `.dm` is the complete portable format. It is encrypted using AES-256-GCM with a password-derived key (PBKDF2-HMAC-SHA256, 600,000 iterations).
- Imported directories and outputs are always Disabled. Imported events are stored locally with status `Imported`, create no delivery records, and are never replayed to outputs.
- Import conflicts with an existing ID are skipped and reported.

See [DIRECTORY_MONITOR_PLAN.md](DIRECTORY_MONITOR_PLAN.md) for the customer-facing implementation plan.

Handover documents are in `docs`: database schema, text tokens, operations guide, and the current test report.
