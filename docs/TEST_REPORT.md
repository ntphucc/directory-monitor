# Directory Monitor — Test report

Date: 2026-08-02  
Build target: .NET 9, `net9.0-windows`, Windows x64

## Automated result

- Release build: passed with 0 warnings and 0 errors.
- Automated tests: 78 passed, 0 failed.
- Coverage collection: 49.2% line coverage and 41.6% branch coverage; CI retains the Cobertura report and TRX results as artifacts.
- WPF startup smoke test: process remained healthy and initialized SQLite.
- Self-contained startup smoke test: passed.
- SQLite outbox load test: 1,000 durable events in under 0.3 seconds on the development machine, above 100 events/second.

Covered areas include glob matching, text templates/JSON escaping, SQLite configuration/outbox/idempotency/settings/delivery state, destination deletion cleanup, snapshot completeness and file-only filtering, and live filesystem classification for New, Change, Modify Properties, rename/OldPath, and Delete. Realtime and offline-recovery tests verify that adding a file does not also emit a derived `Change` for its parent directory. The suite also verifies directory runtime states, Reconnecting recovery, disabled-output pending retention, appsettings SQLite path resolution, `DIRECTORY_MONITOR_DATA`, legacy ProgramData migration, Settings database-path/Open-folder/restart-warning wiring, FIFO event batching, oldest-event buffer trimming, locked chronological Event grid order, automatic scroll wiring, icon-only Directory status mapping/accessibility, complete DataGrid text-column vertical alignment, installer Data/Logs/appsettings permissions, localization key parity, English designer defaults, and DynamicResource coverage.

Presentation coverage also verifies global vertical centering/stretching for every `ListBoxItem`, the localized main-window Exit action, confirmation through the shared clean-shutdown path, and preservation of the standard close-to-tray behavior.

Version 1.1.8 coverage additionally verifies Light/Dark palette parity, System/Light/Dark UI wiring, Directory/Output context and double-click actions, Event-grid column removal/order and UI-only clear behavior, Serilog/appsettings configuration wiring, JSON/XML credential omission, encrypted `.dm` round-trip, wrong-password/tamper rejection, forced-disabled imports, dangling destination cleanup, no-delivery event import, and migration from legacy unique `SequenceNumber` to `(MachineId, SequenceNumber)` while preserving pending delivery. A runtime smoke test confirmed the external appsettings file loads, Serilog configuration replaces the bootstrap logger, and SQLite is initialized successfully.

Repository-quality coverage verifies the CI and CodeQL workflows, least-privilege workflow permissions, central NuGet package versions, per-project lock files, warnings-as-errors, required public governance documents, sensitive runtime-file exclusions, and DTD-safe XML import configuration.

Solution coverage verifies that `DirectoryMonitor.slnx` is the only solution file, contains all five projects, and is used by the release build script. Restore and Release tests were executed directly through the SLNX solution.

Update coverage includes startup/daily/monthly/never scheduling, six-hour retry, blank/HTTP URLs, stable version comparison, exact-version skip, malformed/oversized manifests, HTTPS-to-HTTP redirect rejection, streamed installer size/SHA-256 verification, invalid partial-file cleanup, SQLite update backup integrity, installer launch arguments, portable DB import guards, original-user relaunch, update UI markup, localization coverage, centralized versioning, and manifest generation metadata.

Database-output UI coverage includes PostgreSQL, SQL Server Windows Authentication, and SQL Login examples; authentication switching without overwriting user input; existing SQL Login detection; Use sample behavior; placeholder blocking before provider resolution; and acceptance of customized connection strings.

Database schema coverage verifies that PostgreSQL DDL/DML uses `public`, never creates the legacy `directory_monitor` schema, and that SQL Server uses `dbo`. Database insert checks cover idempotency by event UUID and `(MachineId, SequenceNumber)`, preventing duplicate-key retries. Presentation checks cover the compact Last error preview, full selectable multiline details, tooltip, and Copy error action. Optional live delivery tests run when `DIRECTORY_MONITOR_TEST_POSTGRES` or `DIRECTORY_MONITOR_TEST_SQLSERVER` is configured; those connections were not present for this local run.

## Environment-dependent acceptance still required

- PostgreSQL 14+ live server permissions, migration, retry, and reconnect.
- SQL Server 2019+ live server permissions, Integrated Security/SQL authentication, migration, retry, and reconnect.
- Windows 10, Windows 11, and Windows Server 2022/2025 installer/upgrade matrix.
- Real HTTPS hosting, proxy behavior, UAC cancel/approve, portable-to-installed migration, and in-place installer upgrade.
- UNC disconnect/reconnect against the customer's file server and permissions model.
- Ten-minute 100 events/second end-to-end soak, one-million-entry reconciliation memory measurement, and Windows Event Viewer rendering after installer registration.

These checks require the customer's target infrastructure and are intentionally not reported as passed by the local automated suite.
