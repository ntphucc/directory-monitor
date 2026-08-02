using System.Text.Json;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Security;
using Npgsql;
using System.Collections.Concurrent;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class PostgreSqlDestination : ILogDestination
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _ready = new();
    public DestinationType Type => DestinationType.PostgreSql;

    public async Task EnsureReadyAsync(OutputDestination destination, CancellationToken cancellationToken = default)
    {
        if (_ready.TryGetValue(destination.Id, out var version) && version == destination.LastModificationUtc) return;
        await using var connection = new NpgsqlConnection(ConnectionString(destination)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SchemaSql, connection, transaction); await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); _ready[destination.Id] = destination.LastModificationUtc;
    }

    public async Task DeliverAsync(OutputDestination destination, IReadOnlyList<MonitorEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(destination, cancellationToken);
        await using var connection = new NpgsqlConnection(ConnectionString(destination)); await connection.OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var e in events)
        {
            await using (var directory = new NpgsqlCommand(UpsertDirectorySql, connection, transaction)) { directory.Parameters.AddWithValue("id", e.DirectoryId); directory.Parameters.AddWithValue("machine", e.MachineId); directory.Parameters.AddWithValue("path", e.RootPath); await directory.ExecuteNonQueryAsync(cancellationToken); }
            await using var insert = new NpgsqlCommand(InsertEventSql, connection, transaction);
            Add(insert, e); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ConnectionString(OutputDestination destination) { var options = JsonSerializer.Deserialize<DatabaseDestinationOptions>(destination.ConfigurationJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Invalid PostgreSQL configuration."); return SecretProtector.Unprotect(options.ProtectedConnectionString); }
    private static void Add(NpgsqlCommand command, MonitorEvent e) { command.Parameters.AddWithValue("id", e.Id); command.Parameters.AddWithValue("seq", e.SequenceNumber); command.Parameters.AddWithValue("time", e.EventTimeUtc.UtcDateTime); command.Parameters.AddWithValue("type", (short)e.EventType); command.Parameters.AddWithValue("directory", e.DirectoryId); command.Parameters.AddWithValue("entry", e.EntryType.ToString()); command.Parameters.AddWithValue("path", e.Path); command.Parameters.AddWithValue("old", (object?)e.OldPath ?? DBNull.Value); command.Parameters.AddWithValue("size", (object?)e.FileSize ?? DBNull.Value); command.Parameters.AddWithValue("user", e.UserName); command.Parameters.AddWithValue("machine", e.MachineId); command.Parameters.AddWithValue("machineName", e.MachineName); command.Parameters.AddWithValue("recovered", e.IsRecovered); }

    private const string UpsertDirectorySql = """
        INSERT INTO public."Directories"("Id","MachineId","Path","IsDisabled","IsDeleted","CreationTime","LastModification") VALUES(@id,@machine,@path,false,false,now(),now())
        ON CONFLICT("Id") DO UPDATE SET "Path"=EXCLUDED."Path","LastModification"=now();
        """;

    private const string InsertEventSql = """
        INSERT INTO public."EventInfos"("Id","SequenceNumber","EventTime","EventTypeId","DirectoryId","EntryType","Path","OldPath","FileSize","UserName","MachineId","MachineName","IsRecovered","CreationTime")
        VALUES(@id,@seq,@time,@type,@directory,@entry,@path,@old,@size,@user,@machine,@machineName,@recovered,now()) ON CONFLICT DO NOTHING;
        """;

    private const string SchemaSql = """
        SELECT pg_advisory_xact_lock(hashtext('directory_monitor_public_schema_v1'));
        CREATE TABLE IF NOT EXISTS public."__DirectoryMonitorSchemaHistory"("Version" integer PRIMARY KEY,"AppliedAt" timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS public."Directories"("Id" uuid PRIMARY KEY,"MachineId" uuid NOT NULL,"Path" text NOT NULL,"IsDisabled" boolean NOT NULL DEFAULT false,"IsDeleted" boolean NOT NULL DEFAULT false,"DeletedTime" timestamptz NULL,"CreationTime" timestamptz NOT NULL,"LastModification" timestamptz NOT NULL);
        CREATE TABLE IF NOT EXISTS public."EventTypes"("Id" smallint PRIMARY KEY,"Name" varchar(32) NOT NULL UNIQUE,"CreationTime" timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS public."EventInfos"("Id" uuid PRIMARY KEY,"SequenceNumber" bigint NOT NULL,"EventTime" timestamptz NOT NULL,"EventTypeId" smallint NOT NULL REFERENCES public."EventTypes"("Id"),"DirectoryId" uuid NOT NULL REFERENCES public."Directories"("Id"),"EntryType" varchar(16) NOT NULL,"Path" text NOT NULL,"OldPath" text NULL,"FileSize" bigint NULL,"UserName" text NOT NULL,"MachineId" uuid NOT NULL,"MachineName" text NOT NULL,"IsRecovered" boolean NOT NULL,"CreationTime" timestamptz NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "UX_EventInfos_MachineSequence" ON public."EventInfos"("MachineId","SequenceNumber");
        CREATE INDEX IF NOT EXISTS "IX_EventInfos_EventTime" ON public."EventInfos"("EventTime");
        CREATE INDEX IF NOT EXISTS "IX_EventInfos_Directory" ON public."EventInfos"("DirectoryId");
        CREATE INDEX IF NOT EXISTS "IX_EventInfos_EventType" ON public."EventInfos"("EventTypeId");
        CREATE INDEX IF NOT EXISTS "IX_EventInfos_MachineEventTime" ON public."EventInfos"("MachineId","EventTime");
        INSERT INTO public."EventTypes"("Id","Name") VALUES(1,'New'),(2,'Change'),(3,'Modify Properties'),(4,'Delete') ON CONFLICT("Id") DO UPDATE SET "Name"=EXCLUDED."Name";
        INSERT INTO public."__DirectoryMonitorSchemaHistory"("Version") VALUES(1) ON CONFLICT DO NOTHING;
        INSERT INTO public."__DirectoryMonitorSchemaHistory"("Version") VALUES(2) ON CONFLICT DO NOTHING;
        """;
}
