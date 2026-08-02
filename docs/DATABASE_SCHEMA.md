# Directory Monitor — Database schema

The application creates and migrates its target tables when the user selects **Test / Test & Save**. PostgreSQL uses the default `public` schema; SQL Server uses the default `dbo` schema. The initial connection must be allowed to create and alter tables, indexes, and foreign keys in that schema.

PostgreSQL versions before 1.1.3 could create a separate `directory_monitor` schema. Version 1.1.3 leaves that schema untouched and writes new events only to `public`; no legacy rows are copied or deleted.

## Migration metadata

`__DirectoryMonitorSchemaHistory` stores each applied integer migration version and its UTC application time. PostgreSQL serializes migration with an advisory transaction lock. SQL Server uses `sp_getapplock` owned by the migration transaction.

## Directories

| Column | PostgreSQL | SQL Server | Notes |
|---|---|---|---|
| Id | uuid | uniqueidentifier | Primary key |
| MachineId | uuid | uniqueidentifier | Stable installation ID |
| Path | text | nvarchar(2048) | Monitored root |
| IsDisabled | boolean | bit | Configuration state |
| IsDeleted | boolean | bit | Soft-delete state |
| DeletedTime | timestamptz | datetimeoffset | Nullable UTC time |
| CreationTime | timestamptz | datetimeoffset | UTC |
| LastModification | timestamptz | datetimeoffset | UTC |

## EventTypes

Seed data is fixed: `1 New`, `2 Change`, `3 Modify Properties`, `4 Delete`. `Id` is the primary key and `Name` is unique.

## EventInfos

The table stores event identity, per-machine sequence, UTC event time, event and entry type, directory reference, current and old paths, optional file size, the Windows account running the app, machine identity/name, recovery flag, and UTC insert time.

Indexes:

- Unique `(MachineId, SequenceNumber)`.
- `EventTime`.
- `DirectoryId`.
- `EventTypeId`.
- `(MachineId, EventTime)`.

Event delivery is idempotent by both event UUID and `(MachineId, SequenceNumber)`. If either identity already exists, the delivery is considered complete instead of retrying a permanent duplicate-key conflict. A database migration failure leaves events in the local SQLite outbox for retry; it never writes to a schema that failed initialization.

Example verification queries:

```sql
SELECT * FROM public."EventInfos" ORDER BY "EventTime";
```

```sql
SELECT * FROM dbo.EventInfos ORDER BY EventTime;
```
