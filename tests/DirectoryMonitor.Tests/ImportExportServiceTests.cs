using System.Text.Json;
using System.Text;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Security;
using DirectoryMonitor.Infrastructure.Storage;
using DirectoryMonitor.Infrastructure.Transfer;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryMonitor.Tests;

public sealed class ImportExportServiceTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.TransferTests", Guid.NewGuid().ToString("N"));
    private SqliteConfigurationStore _store = null!;
    private ImportExportService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _store = new SqliteConfigurationStore(Path.Combine(_folder, "data.db"));
        await _store.InitializeAsync();
        _service = new ImportExportService(_store, NullLogger<ImportExportService>.Instance);
    }

    public Task DisposeAsync() { try { Directory.Delete(_folder, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task Json_and_xml_exports_omit_database_credentials()
    {
        const string connectionString = "Host=db.example;Database=monitor;Username=app;Password=top-secret";
        var output = new OutputDestination
        {
            Name = "Postgres", Type = DestinationType.PostgreSql,
            ConfigurationJson = JsonSerializer.Serialize(new DatabaseDestinationOptions { ProtectedConnectionString = SecretProtector.Protect(connectionString) }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        foreach (var format in new[] { TransferFormat.Json, TransferFormat.Xml })
        {
            var path = Path.Combine(_folder, "export." + format.ToString().ToLowerInvariant());
            await _service.ExportAsync(path, format, new TransferSelection { Outputs = true }, [], [], [output], null);
            var content = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("top-secret", content);
            Assert.DoesNotContain("db.example", content);
            var read = await _service.ReadAsync(path, null);
            var transferred = Assert.Single(read.Outputs);
            Assert.True(transferred.RequiresCredentials);
            Assert.Null(transferred.ConnectionString);
        }
    }

    [Fact]
    public async Task Dm_round_trip_preserves_secret_and_rejects_wrong_password_or_tampering()
    {
        const string connectionString = "Server=db;Database=monitor;User ID=app;Password=secret";
        var output = new OutputDestination
        {
            Name = "SQL", Type = DestinationType.SqlServer,
            ConfigurationJson = JsonSerializer.Serialize(new DatabaseDestinationOptions { ProtectedConnectionString = SecretProtector.Protect(connectionString) }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var path = Path.Combine(_folder, "backup.dm");
        await _service.ExportAsync(path, TransferFormat.Dm, new TransferSelection { Outputs = true }, [], [], [output], "correct horse");
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)), StringComparison.OrdinalIgnoreCase);
        var read = await _service.ReadAsync(path, "correct horse");
        Assert.Equal(connectionString, Assert.Single(read.Outputs).ConnectionString);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ReadAsync(path, "wrong password"));

        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 0x5A; await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ReadAsync(path, "correct horse"));
    }

    [Fact]
    public async Task Import_forces_configuration_disabled_removes_dangling_routes_and_never_creates_deliveries_for_events()
    {
        var outputId = Guid.NewGuid(); var directoryId = Guid.NewGuid();
        var monitorEvent = new TransferEvent { Id = Guid.NewGuid(), SequenceNumber = 7, MachineId = Guid.NewGuid(), DirectoryId = directoryId, Path = @"D:\Imported\a.txt", RootPath = @"D:\Imported", EventType = MonitorEventType.New, EntryType = EntryType.File, EventTimeUtc = DateTimeOffset.UtcNow, CreationTimeUtc = DateTimeOffset.UtcNow };
        var envelope = new TransferEnvelopeV1
        {
            Outputs = [new TransferOutput { Id = outputId, Name = "Text", Type = DestinationType.Text, ConfigurationJson = JsonSerializer.Serialize(new TextDestinationOptions { FilePath = @"D:\Logs\events.log" }), CreationTimeUtc = DateTimeOffset.UtcNow }],
            Directories = [new TransferDirectory { Id = directoryId, Path = @"D:\Imported", DestinationIds = [outputId, Guid.NewGuid()], EventKinds = MonitoredEventKinds.All, EntryTypeFilter = EntryTypeFilter.FilesAndDirectories, CreationTimeUtc = DateTimeOffset.UtcNow }],
            Events = [monitorEvent]
        };
        var summary = await _service.ImportAsync(envelope, new TransferSelection { Directories = true, Events = true, Outputs = true });
        Assert.Equal(1, summary.Outputs.Added); Assert.Equal(1, summary.Directories.Added); Assert.Equal(1, summary.Events.Added);
        Assert.True(Assert.Single(await _store.GetDestinationsAsync()).IsDisabled);
        var directory = Assert.Single(await _store.GetDirectoriesAsync()); Assert.True(directory.IsDisabled); Assert.Equal([outputId], directory.DestinationIds);
        Assert.Equal(0, await _store.GetPendingCountAsync());
        Assert.NotEmpty(summary.Warnings);
    }
}
