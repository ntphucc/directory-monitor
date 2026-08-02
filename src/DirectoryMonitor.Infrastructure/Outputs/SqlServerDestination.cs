using System.Text.Json;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class SqlServerDestination : ILogDestination
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _ready = new();
    public DestinationType Type => DestinationType.SqlServer;

    public async Task EnsureReadyAsync(OutputDestination destination, CancellationToken cancellationToken = default)
    {
        if (_ready.TryGetValue(destination.Id, out var version) && version == destination.LastModificationUtc) return;
        await using var connection = new SqlConnection(ConnectionString(destination)); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(SchemaSql, connection, transaction) { CommandTimeout = 60 }; await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); _ready[destination.Id] = destination.LastModificationUtc;
    }

    public async Task DeliverAsync(OutputDestination destination, IReadOnlyList<MonitorEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(destination, cancellationToken);
        await using var connection = new SqlConnection(ConnectionString(destination)); await connection.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var e in events)
        {
            await using (var directory = new SqlCommand("IF EXISTS(SELECT 1 FROM dbo.Directories WHERE Id=@id) UPDATE dbo.Directories SET Path=@path,LastModification=SYSUTCDATETIME() WHERE Id=@id ELSE INSERT dbo.Directories(Id,MachineId,Path,IsDisabled,IsDeleted,CreationTime,LastModification) VALUES(@id,@machine,@path,0,0,SYSUTCDATETIME(),SYSUTCDATETIME());", connection, transaction)) { directory.Parameters.AddWithValue("@id", e.DirectoryId); directory.Parameters.AddWithValue("@machine", e.MachineId); directory.Parameters.AddWithValue("@path", e.RootPath); await directory.ExecuteNonQueryAsync(cancellationToken); }
            await using var insert = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.EventInfos WHERE Id=@id OR (MachineId=@machine AND SequenceNumber=@seq)) INSERT dbo.EventInfos(Id,SequenceNumber,EventTime,EventTypeId,DirectoryId,EntryType,Path,OldPath,FileSize,UserName,MachineId,MachineName,IsRecovered,CreationTime) VALUES(@id,@seq,@time,@type,@directory,@entry,@path,@old,@size,@user,@machine,@machineName,@recovered,SYSUTCDATETIME());", connection, transaction);
            Add(insert, e); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ConnectionString(OutputDestination destination) { var options = JsonSerializer.Deserialize<DatabaseDestinationOptions>(destination.ConfigurationJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Invalid SQL Server configuration."); return SecretProtector.Unprotect(options.ProtectedConnectionString); }
    private static void Add(SqlCommand command, MonitorEvent e) { command.Parameters.AddWithValue("@id", e.Id); command.Parameters.AddWithValue("@seq", e.SequenceNumber); command.Parameters.AddWithValue("@time", e.EventTimeUtc); command.Parameters.AddWithValue("@type", (short)e.EventType); command.Parameters.AddWithValue("@directory", e.DirectoryId); command.Parameters.AddWithValue("@entry", e.EntryType.ToString()); command.Parameters.AddWithValue("@path", e.Path); command.Parameters.AddWithValue("@old", (object?)e.OldPath ?? DBNull.Value); command.Parameters.AddWithValue("@size", (object?)e.FileSize ?? DBNull.Value); command.Parameters.AddWithValue("@user", e.UserName); command.Parameters.AddWithValue("@machine", e.MachineId); command.Parameters.AddWithValue("@machineName", e.MachineName); command.Parameters.AddWithValue("@recovered", e.IsRecovered); }

    private const string SchemaSql = """
        EXEC sp_getapplock @Resource='directory_monitor_schema_v1',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=60000;
        IF OBJECT_ID('dbo.__DirectoryMonitorSchemaHistory') IS NULL CREATE TABLE dbo.__DirectoryMonitorSchemaHistory([Version] int NOT NULL PRIMARY KEY,AppliedAt datetimeoffset NOT NULL DEFAULT SYSUTCDATETIME());
        IF OBJECT_ID('dbo.Directories') IS NULL CREATE TABLE dbo.Directories(Id uniqueidentifier NOT NULL PRIMARY KEY,MachineId uniqueidentifier NOT NULL,Path nvarchar(2048) NOT NULL,IsDisabled bit NOT NULL DEFAULT 0,IsDeleted bit NOT NULL DEFAULT 0,DeletedTime datetimeoffset NULL,CreationTime datetimeoffset NOT NULL,LastModification datetimeoffset NOT NULL);
        IF OBJECT_ID('dbo.EventTypes') IS NULL CREATE TABLE dbo.EventTypes(Id smallint NOT NULL PRIMARY KEY,Name nvarchar(32) NOT NULL UNIQUE,CreationTime datetimeoffset NOT NULL DEFAULT SYSUTCDATETIME());
        IF OBJECT_ID('dbo.EventInfos') IS NULL BEGIN CREATE TABLE dbo.EventInfos(Id uniqueidentifier NOT NULL PRIMARY KEY,SequenceNumber bigint NOT NULL,EventTime datetimeoffset NOT NULL,EventTypeId smallint NOT NULL,DirectoryId uniqueidentifier NOT NULL,EntryType nvarchar(16) NOT NULL,Path nvarchar(2048) NOT NULL,OldPath nvarchar(2048) NULL,FileSize bigint NULL,UserName nvarchar(256) NOT NULL,MachineId uniqueidentifier NOT NULL,MachineName nvarchar(256) NOT NULL,IsRecovered bit NOT NULL,CreationTime datetimeoffset NOT NULL,CONSTRAINT FK_EventInfos_EventTypes FOREIGN KEY(EventTypeId) REFERENCES dbo.EventTypes(Id),CONSTRAINT FK_EventInfos_Directories FOREIGN KEY(DirectoryId) REFERENCES dbo.Directories(Id)); CREATE UNIQUE INDEX UX_EventInfos_MachineSequence ON dbo.EventInfos(MachineId,SequenceNumber); CREATE INDEX IX_EventInfos_EventTime ON dbo.EventInfos(EventTime); CREATE INDEX IX_EventInfos_Directory ON dbo.EventInfos(DirectoryId); END;
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.EventInfos') AND name='IX_EventInfos_EventType') CREATE INDEX IX_EventInfos_EventType ON dbo.EventInfos(EventTypeId);
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.EventInfos') AND name='IX_EventInfos_MachineEventTime') CREATE INDEX IX_EventInfos_MachineEventTime ON dbo.EventInfos(MachineId,EventTime);
        MERGE dbo.EventTypes AS target USING (VALUES(1,N'New'),(2,N'Change'),(3,N'Modify Properties'),(4,N'Delete')) source(Id,Name) ON target.Id=source.Id WHEN MATCHED THEN UPDATE SET Name=source.Name WHEN NOT MATCHED THEN INSERT(Id,Name) VALUES(source.Id,source.Name);
        IF NOT EXISTS(SELECT 1 FROM dbo.__DirectoryMonitorSchemaHistory WHERE [Version]=1) INSERT dbo.__DirectoryMonitorSchemaHistory([Version]) VALUES(1);
        IF NOT EXISTS(SELECT 1 FROM dbo.__DirectoryMonitorSchemaHistory WHERE [Version]=2) INSERT dbo.__DirectoryMonitorSchemaHistory([Version]) VALUES(2);
        """;
}
