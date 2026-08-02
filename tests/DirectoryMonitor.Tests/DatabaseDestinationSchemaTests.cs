using System.Reflection;
using System.Text.Json;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Outputs;
using DirectoryMonitor.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DirectoryMonitor.Tests;

public sealed class DatabaseDestinationSchemaTests
{
    [Fact]
    public void PostgreSql_schema_uses_public_and_does_not_create_the_legacy_schema()
    {
        var sql = SchemaSql<PostgreSqlDestination>();

        Assert.Contains("public.\"Directories\"", sql);
        Assert.Contains("public.\"EventTypes\"", sql);
        Assert.Contains("public.\"EventInfos\"", sql);
        Assert.Contains("public.\"__DirectoryMonitorSchemaHistory\"", sql);
        Assert.DoesNotContain("directory_monitor.\"", sql);
        Assert.DoesNotContain("CREATE SCHEMA", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public.\"Directories\"", ConstantSql<PostgreSqlDestination>("UpsertDirectorySql"));
        Assert.Contains("public.\"EventInfos\"", ConstantSql<PostgreSqlDestination>("InsertEventSql"));
    }

    [Fact]
    public void SqlServer_schema_uses_dbo_for_every_application_table()
    {
        var sql = SchemaSql<SqlServerDestination>();

        Assert.Contains("dbo.Directories", sql);
        Assert.Contains("dbo.EventTypes", sql);
        Assert.Contains("dbo.EventInfos", sql);
        Assert.Contains("dbo.__DirectoryMonitorSchemaHistory", sql);
    }

    [Fact]
    public void Database_inserts_are_idempotent_for_both_event_id_and_machine_sequence()
    {
        var postgres = ConstantSql<PostgreSqlDestination>("InsertEventSql");
        var sqlServerSource = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Infrastructure", "Outputs", "SqlServerDestination.cs"));

        Assert.Contains("ON CONFLICT DO NOTHING", postgres);
        Assert.DoesNotContain("ON CONFLICT(\"Id\")", postgres);
        Assert.Contains("Id=@id OR (MachineId=@machine AND SequenceNumber=@seq)", sqlServerSource);
    }

    [Fact]
    public async Task PostgreSql_destination_creates_and_writes_public_event_infos_when_test_connection_is_configured()
    {
        var connectionString = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var monitorEvent = CreateEvent();
        var destination = CreateDestination(DestinationType.PostgreSql, connectionString);
        await new PostgreSqlDestination().DeliverAsync(destination, [monitorEvent]);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var verify = new NpgsqlCommand("SELECT COUNT(*) FROM public.\"EventInfos\" WHERE \"Id\"=@id;", connection))
        {
            verify.Parameters.AddWithValue("id", monitorEvent.Id);
            Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
        }
        await using var cleanup = new NpgsqlCommand("DELETE FROM public.\"EventInfos\" WHERE \"Id\"=@event; DELETE FROM public.\"Directories\" WHERE \"Id\"=@directory;", connection);
        cleanup.Parameters.AddWithValue("event", monitorEvent.Id);
        cleanup.Parameters.AddWithValue("directory", monitorEvent.DirectoryId);
        await cleanup.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SqlServer_destination_creates_and_writes_dbo_event_infos_when_test_connection_is_configured()
    {
        var connectionString = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var monitorEvent = CreateEvent();
        var destination = CreateDestination(DestinationType.SqlServer, connectionString);
        await new SqlServerDestination().DeliverAsync(destination, [monitorEvent]);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var verify = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.EventInfos WHERE Id=@id;", connection))
        {
            verify.Parameters.AddWithValue("@id", monitorEvent.Id);
            Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
        }
        await using var cleanup = new SqlCommand("DELETE FROM dbo.EventInfos WHERE Id=@event; DELETE FROM dbo.Directories WHERE Id=@directory;", connection);
        cleanup.Parameters.AddWithValue("@event", monitorEvent.Id);
        cleanup.Parameters.AddWithValue("@directory", monitorEvent.DirectoryId);
        await cleanup.ExecuteNonQueryAsync();
    }

    private static string SchemaSql<T>()
        => ConstantSql<T>("SchemaSql");

    private static string ConstantSql<T>(string name)
        => (string)(typeof(T).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!);

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DirectoryMonitor.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Directory Monitor repository root.");
    }

    private static OutputDestination CreateDestination(DestinationType type, string connectionString)
        => new()
        {
            Name = "Integration test",
            Type = type,
            ConfigurationJson = JsonSerializer.Serialize(new DatabaseDestinationOptions
            {
                ProtectedConnectionString = SecretProtector.Protect(connectionString)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

    private static MonitorEvent CreateEvent()
        => new()
        {
            SequenceNumber = DateTime.UtcNow.Ticks,
            EventType = MonitorEventType.New,
            EntryType = EntryType.File,
            DirectoryId = Guid.NewGuid(),
            RootPath = @"C:\DirectoryMonitorIntegrationTest",
            Path = @"C:\DirectoryMonitorIntegrationTest\event.txt",
            UserName = Environment.UserName,
            MachineId = Guid.NewGuid(),
            MachineName = Environment.MachineName
        };
}
