Directory Monitor stores directory-monitor.db, snapshots, and the durable outbox in this folder.
Keep this folder during repair or upgrade, and do not modify its files while the application is running.

The default connection string is configured in appsettings.json beside the executable. The database is created during the first successful application startup and is not included as a prebuilt file.
Changing the SQLite connection string while the app is running requires an application restart.
Use Settings > SQLite database > Open folder to open this exact location.
SQLite may create directory-monitor.db-wal and directory-monitor.db-shm while the app is running; keep these files with the database.

On first startup after an upgrade, the application can migrate the legacy database from
%ProgramData%\Directory Monitor by using SQLite backup. The legacy database is retained.
