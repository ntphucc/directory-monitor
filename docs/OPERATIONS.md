# Directory Monitor — Operations guide

## Install and first run

Use the per-machine installer when available, or extract the portable package and run `Directory Monitor.exe`. The self-contained `win-x64` release does not require a separate .NET runtime. The installer registers the `Directory Monitor` Windows Event Log source and grants normal users modify access only to `{app}\Data`, `{app}\Logs`, and the external `{app}\appsettings.json` file.

On first run, the app enables startup for the current Windows account through `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. This can be changed on the Settings tab. Startup launches minimized to the tray. Closing the window also minimizes to the tray. Use **Exit** on the main title bar or the tray menu to stop monitoring; both ask for confirmation and then shut down watchers and output workers cleanly.

## Configure monitoring

Create output destinations first, then add a directory and select its outputs. A directory accepts a local absolute path or UNC path. UNC access uses the permissions of the Windows account running the app; credentials are not stored separately.

The first scan establishes a baseline and emits no historical events. Reconciliation after restart, reconnect, watcher overflow, or the daily schedule marks inferred events as recovered. An incomplete or access-denied scan never infers Delete events.

Creating, modifying, or deleting a child file can make Windows update the parent directory timestamp. Directory Monitor suppresses that derived directory `Change` event in both live monitoring and reconciliation, while continuing to report the file event and real directory attribute changes.

`UserName` identifies the account running Directory Monitor. It is not proof of the user or process that changed a file.

## Output failures

The Directories grid shows live runtime states: Monitoring, Disabled, Unavailable, Reconnecting, or Error. These transient states are recalculated at startup and are not stored in SQLite. The Outputs tab shows Ready, Delivering, Retrying, or Disabled, its pending count, and the latest error. Select an output row to open the complete multiline error in the read-only details panel; use **Copy error** when the full provider message is needed for support. Each output retries independently with exponential delay up to five minutes. Disabling an output pauses delivery while retaining queued events.

PostgreSQL error `23505` means a unique identity already exists. Directory Monitor uses both the event UUID and `(MachineId, SequenceNumber)` as idempotency keys. Re-delivery of an existing identity is treated as success, so an already stored event does not remain in `Retrying` indefinitely.

Database connection strings are protected with Windows DPAPI CurrentUser and can only be decrypted by the same Windows account. A database destination must pass schema initialization before it can be saved.

The Output editor displays copy-safe examples for PostgreSQL and both SQL Server authentication modes. Select the matching authentication method, choose **Use sample**, and replace every uppercase placeholder before testing. The app blocks Test/Save while `SERVER_NAME`, `DATABASE_NAME`, `USER_NAME`, or `PASSWORD` remains. Keep certificate validation enabled and change SSL or `TrustServerCertificate` only under DBA guidance.

PostgreSQL tables are created in the default `public` schema; SQL Server tables are created in `dbo`. Verify delivered events with one of these queries:

```sql
SELECT * FROM public."EventInfos" ORDER BY "EventTime";
```

```sql
SELECT * FROM dbo.EventInfos ORDER BY EventTime;
```

Versions before 1.1.3 could create a PostgreSQL schema named `directory_monitor`. Version 1.1.3 does not read, copy, modify, or delete that legacy schema; new database output is written only to `public`.

## Clear all events

Clear all events removes only the in-memory UI buffer after confirmation. It never truncates text output, changes SQLite Events/Deliveries, deletes database/Event Log records, or cancels pending delivery. Events produced after the clear point continue to appear normally.

## Data and recovery

Configuration, machine identity, snapshots, and the durable outbox are stored in `<Directory Monitor.exe folder>\Data\directory-monitor.db` using SQLite WAL mode. Preserve the `Data` directory during repair or upgrade. Events already committed to the outbox survive application or destination failures.

The active SQLite connection string comes from `DirectoryMonitor:Sqlite:ConnectionString` in `appsettings.json`; relative paths resolve from the executable directory. `DIRECTORY_MONITOR_DATA` has highest priority for development/test. The file reloads automatically, but a SQLite path change only displays a restart-required warning—the running process continues using its original database to avoid splitting the durable outbox.

Serilog configuration also lives in `appsettings.json` and reloads live. The default sink creates UTF-8 daily files at `<app>\Logs\directory-monitor-YYYYMMDD.log`, retains 30 files, and records startup, shutdown, watcher/output/update/import errors, and global exceptions. Logs intentionally exclude connection strings, passwords, `.dm` content, and ordinary filesystem events.

The database is created on the first successful startup and is not prebuilt into the installer or portable package. Open **Settings > SQLite database** to view the exact resolved path and use **Open folder**. While the app is running, SQLite can also create `directory-monitor.db-wal` and `directory-monitor.db-shm`; these are normal database files and must remain with the main database.

If this database does not exist but the legacy `%ProgramData%\Directory Monitor\directory-monitor.db` does, startup uses SQLite backup to migrate the complete database into `App\Data`. The old database is retained as a recovery copy. If `App\Data` is not writable, startup reports the folder error and does not fall back to a temporary database.

## Theme and data transfer

Theme can be System, Light, or Dark. System follows the Windows `AppsUseLightTheme` value and reacts to Windows theme changes while the app is running.

Import/Export is available in Settings. JSON/XML omit database credentials and are suitable for review. `.dm` includes credentials and encrypts the complete payload with AES-256-GCM using a PBKDF2-HMAC-SHA256 key (600,000 iterations); the password is never stored. Imported directories and outputs are always Disabled. Imported events are persisted with no delivery rows, display as Imported, and are never replayed to configured outputs. Existing IDs are skipped and missing destination references are reported and removed.

## Application updates

The manifest URL is blank by default, so the app does not contact an update server until an administrator/operator configures an anonymous HTTPS URL in Settings. The default schedule is Daily; Every startup, Monthly, and Never are also available. **Check now** always works. A failed automatic check remains visible as status only and retries after six hours.

When a newer non-skipped stable version is found, an amber badge appears in the title bar. Open it to review the localized release notes, then choose Update now, Skip this version, or Later. Skipping applies only to that exact version.

Update now downloads to `%LocalAppData%\Directory Monitor\Updates\<version>`. The app rejects non-HTTPS redirects, oversized files, size mismatches, and SHA-256 mismatches. It creates a consistent SQLite backup before asking for UAC approval. Cancelling UAC leaves monitoring running. After the verified installer starts, the app shuts down its watchers and output workers cleanly.

During the first update from a portable copy, the installer imports the backup only when the Program Files target has no database. It never overwrites an existing `{app}\Data\directory-monitor.db`, and the original portable database remains as a recovery copy.

The current builds are not Authenticode-signed. Windows SmartScreen can therefore display a warning even when HTTPS, file size, and SHA-256 validation succeed.
