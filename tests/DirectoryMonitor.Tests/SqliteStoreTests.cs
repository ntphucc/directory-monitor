using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace DirectoryMonitor.Tests;

public sealed class SqliteStoreTests : IAsyncLifetime
{
    private static readonly object EnvironmentLock = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.Tests", Guid.NewGuid().ToString("N"));
    private SqliteConfigurationStore _store = null!;

    public async Task InitializeAsync() { Directory.CreateDirectory(_folder); _store = new SqliteConfigurationStore(Path.Combine(_folder, "test.db")); await _store.InitializeAsync(); }
    public Task DisposeAsync() { try { Directory.Delete(_folder, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task Directory_and_destination_round_trip()
    {
        var destination = new OutputDestination { Name = "Text", Type = DestinationType.Text };
        await _store.UpsertDestinationAsync(destination);
        var directory = new MonitoredDirectory { Path = @"D:\Data", DestinationIds = [destination.Id] };
        await _store.UpsertDirectoryAsync(directory);
        Assert.Equal(directory.Path, Assert.Single(await _store.GetDirectoriesAsync()).Path);
        Assert.Equal(destination.Name, Assert.Single(await _store.GetDestinationsAsync()).Name);
    }

    [Fact]
    public async Task Event_is_persisted_and_delivered_idempotently()
    {
        var destination = new OutputDestination { Name = "Text", Type = DestinationType.Text };
        await _store.UpsertDestinationAsync(destination);
        var evt = new MonitorEvent { SequenceNumber = await _store.NextSequenceAsync(), DirectoryId = Guid.NewGuid(), RootPath = @"D:\Data", Path = @"D:\Data\a.txt", MachineId = await _store.GetMachineIdAsync(), MachineName = "TEST", UserName = "tester", EventType = MonitorEventType.New };
        await _store.EnqueueEventAsync(evt, [destination.Id]);
        await _store.EnqueueEventAsync(evt, [destination.Id]);
        Assert.Single(await _store.GetPendingDeliveriesAsync(destination.Id, 100));
        Assert.Equal("0/1", (await _store.GetDeliveryStatusesAsync([evt.Id]))[evt.Id]);
        Assert.Equal("Delivering", Assert.Single(await _store.GetDestinationsAsync()).Status);
        await _store.MarkDeliveredAsync(destination.Id, [evt.Id]);
        Assert.Equal(0, await _store.GetPendingCountAsync());
        Assert.Equal("Delivered", (await _store.GetDeliveryStatusesAsync([evt.Id]))[evt.Id]);
        Assert.Equal("Ready", Assert.Single(await _store.GetDestinationsAsync()).Status);
    }

    [Fact]
    public async Task Application_setting_round_trips()
    {
        Assert.Null(await _store.GetSettingAsync("UiBufferLimit"));
        await _store.SetSettingAsync("UiBufferLimit", "25000");
        Assert.Equal("25000", await _store.GetSettingAsync("UiBufferLimit"));
    }

    [Fact(Timeout = 30_000)]
    public async Task Outbox_sustains_more_than_100_events_per_second()
    {
        var destination = new OutputDestination { Name = "Load test", Type = DestinationType.Text };
        await _store.UpsertDestinationAsync(destination);
        var machineId = await _store.GetMachineIdAsync();
        var directoryId = Guid.NewGuid();
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
        {
            var evt = new MonitorEvent { SequenceNumber = await _store.NextSequenceAsync(), DirectoryId = directoryId, RootPath = @"D:\Data", Path = $@"D:\Data\{index}.txt", MachineId = machineId, MachineName = "TEST", UserName = "tester", EventType = MonitorEventType.Change };
            await _store.EnqueueEventAsync(evt, [destination.Id]);
        }
        started.Stop();
        Assert.Equal(1_000, await _store.GetPendingCountAsync(destination.Id));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"Outbox took {started.Elapsed} for 1,000 events.");
    }

    [Fact]
    public async Task Deleting_destination_removes_directory_route_and_delivery_rows()
    {
        var destination = new OutputDestination { Name = "Disposable", Type = DestinationType.Text };
        await _store.UpsertDestinationAsync(destination);
        var directory = new MonitoredDirectory { Path = @"D:\Data", DestinationIds = [destination.Id] };
        await _store.UpsertDirectoryAsync(directory);
        var evt = new MonitorEvent { SequenceNumber = await _store.NextSequenceAsync(), DirectoryId = directory.Id, RootPath = directory.Path, Path = @"D:\Data\a.txt", MachineId = await _store.GetMachineIdAsync(), MachineName = "TEST", UserName = "tester", EventType = MonitorEventType.New };
        await _store.EnqueueEventAsync(evt, [destination.Id]);
        await _store.DeleteDestinationAsync(destination.Id, discardPending: true);
        Assert.Empty(await _store.GetDestinationsAsync());
        Assert.Empty(Assert.Single(await _store.GetDirectoriesAsync()).DestinationIds);
    }

    [Fact]
    public async Task Disabled_destination_reports_status_and_keeps_pending_event()
    {
        var destination = new OutputDestination { Name = "Disabled output", Type = DestinationType.Text, IsDisabled = true };
        await _store.UpsertDestinationAsync(destination);
        var evt = new MonitorEvent
        {
            SequenceNumber = await _store.NextSequenceAsync(), DirectoryId = Guid.NewGuid(), RootPath = @"D:\Data",
            Path = @"D:\Data\pending.txt", MachineId = await _store.GetMachineIdAsync(), MachineName = "TEST",
            UserName = "tester", EventType = MonitorEventType.New
        };

        await _store.EnqueueEventAsync(evt, [destination.Id]);

        var loaded = Assert.Single(await _store.GetDestinationsAsync());
        Assert.Equal("Disabled", loaded.Status);
        Assert.Equal(1, loaded.PendingCount);
        Assert.Equal(1, await _store.GetPendingCountAsync(destination.Id));
    }

    [Fact]
    public async Task Imported_events_use_machine_and_sequence_as_the_unique_identity_and_create_no_deliveries()
    {
        var firstMachine = Guid.NewGuid(); var secondMachine = Guid.NewGuid();
        var events = new[]
        {
            new MonitorEvent { Id = Guid.NewGuid(), SequenceNumber = 42, MachineId = firstMachine, DirectoryId = Guid.NewGuid(), RootPath = @"D:\A", Path = @"D:\A\one.txt", MachineName = "A", EventType = MonitorEventType.New },
            new MonitorEvent { Id = Guid.NewGuid(), SequenceNumber = 42, MachineId = secondMachine, DirectoryId = Guid.NewGuid(), RootPath = @"D:\B", Path = @"D:\B\two.txt", MachineName = "B", EventType = MonitorEventType.New }
        };
        Assert.Equal(2, await _store.ImportEventsAsync(events));
        Assert.Equal(0, await _store.GetPendingCountAsync());
        Assert.Equal(0, await _store.ImportEventsAsync(events));
    }

    [Fact]
    public async Task Legacy_sequence_unique_schema_migrates_without_losing_event_or_delivery()
    {
        var path = Path.Combine(_folder, "legacy-sequence.db");
        var machineId = Guid.NewGuid(); var eventId = Guid.NewGuid(); var destinationId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE AppState(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO AppState VALUES('MachineId','{machineId:D}'),('Sequence','1');
                CREATE TABLE Destinations(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Type INTEGER NOT NULL,IsDisabled INTEGER NOT NULL,ConfigurationJson TEXT NOT NULL,CreationTime TEXT NOT NULL,LastModification TEXT NOT NULL);
                INSERT INTO Destinations VALUES('{destinationId:D}','Legacy',1,0,'[]','{now}','{now}');
                CREATE TABLE Events(Id TEXT PRIMARY KEY,SequenceNumber INTEGER NOT NULL UNIQUE,EventTime TEXT NOT NULL,EventType INTEGER NOT NULL,EntryType INTEGER NOT NULL,DirectoryId TEXT NOT NULL,RootPath TEXT NOT NULL,Path TEXT NOT NULL,OldPath TEXT NULL,FileSize INTEGER NULL,UserName TEXT NOT NULL,MachineId TEXT NOT NULL,MachineName TEXT NOT NULL,IsRecovered INTEGER NOT NULL,CreationTime TEXT NOT NULL);
                INSERT INTO Events VALUES('{eventId:D}',1,'{now}',1,1,'{Guid.NewGuid():D}','D:\\Legacy','D:\\Legacy\\a.txt',NULL,1,'tester','{machineId:D}','LEGACY',0,'{now}');
                CREATE TABLE Deliveries(EventId TEXT NOT NULL,DestinationId TEXT NOT NULL,State INTEGER NOT NULL,Attempts INTEGER NOT NULL,NextAttempt TEXT NOT NULL,LastError TEXT NULL,PRIMARY KEY(EventId,DestinationId),FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE CASCADE,FOREIGN KEY(DestinationId) REFERENCES Destinations(Id));
                INSERT INTO Deliveries VALUES('{eventId:D}','{destinationId:D}',0,0,'{now}',NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrated = new SqliteConfigurationStore(path);
        await migrated.InitializeAsync();
        Assert.Equal(1, await migrated.GetPendingCountAsync(destinationId));
        var imported = new MonitorEvent { Id = Guid.NewGuid(), SequenceNumber = 1, MachineId = Guid.NewGuid(), DirectoryId = Guid.NewGuid(), RootPath = @"D:\Other", Path = @"D:\Other\b.txt", MachineName = "OTHER", EventType = MonitorEventType.New };
        Assert.Equal(1, await migrated.ImportEventsAsync([imported]));
        Assert.Equal(1, await migrated.GetPendingCountAsync(destinationId));
    }

    [Fact]
    public async Task Legacy_database_is_backed_up_to_app_data_without_deleting_original()
    {
        var legacyPath = Path.Combine(_folder, "legacy", "directory-monitor.db");
        var newPath = Path.Combine(_folder, "App", "Data", "directory-monitor.db");
        var legacy = new SqliteConfigurationStore(legacyPath);
        await legacy.InitializeAsync();
        var machineId = await legacy.GetMachineIdAsync();
        var destination = new OutputDestination { Name = "Legacy output", Type = DestinationType.Text };
        var directory = new MonitoredDirectory { Path = @"D:\Legacy", DestinationIds = [destination.Id] };
        await legacy.UpsertDestinationAsync(destination);
        await legacy.UpsertDirectoryAsync(directory);
        await legacy.ReplaceSnapshotAsync(directory.Id,
        [
            new SnapshotEntry(directory.Id, @"D:\Legacy\existing.txt", EntryType.File, 12, DateTimeOffset.UtcNow, FileAttributes.Normal)
        ]);
        var evt = new MonitorEvent
        {
            SequenceNumber = await legacy.NextSequenceAsync(), DirectoryId = directory.Id, RootPath = directory.Path,
            Path = @"D:\Legacy\new.txt", MachineId = machineId, MachineName = "TEST", UserName = "tester",
            EventType = MonitorEventType.New
        };
        await legacy.EnqueueEventAsync(evt, [destination.Id]);
        await legacy.SetSettingAsync("UiBufferLimit", "25000");

        var migrated = new SqliteConfigurationStore(newPath, legacyPath);
        await migrated.InitializeAsync();

        Assert.True(File.Exists(newPath));
        Assert.True(File.Exists(legacyPath));
        Assert.Equal(machineId, await migrated.GetMachineIdAsync());
        Assert.Equal(directory.Path, Assert.Single(await migrated.GetDirectoriesAsync()).Path);
        Assert.Equal(destination.Name, Assert.Single(await migrated.GetDestinationsAsync()).Name);
        Assert.Single(await migrated.LoadSnapshotAsync(directory.Id));
        Assert.Equal(1, await migrated.GetPendingCountAsync(destination.Id));
        Assert.Equal("25000", await migrated.GetSettingAsync("UiBufferLimit"));
    }

    [Fact]
    public async Task Update_backup_is_consistent_and_preserves_configuration_snapshot_identity_and_pending_outbox()
    {
        var machineId = await _store.GetMachineIdAsync();
        var destination = new OutputDestination { Name = "Backup output", Type = DestinationType.Text };
        var directory = new MonitoredDirectory { Path = @"D:\Backup", DestinationIds = [destination.Id] };
        await _store.UpsertDestinationAsync(destination);
        await _store.UpsertDirectoryAsync(directory);
        await _store.ReplaceSnapshotAsync(directory.Id,
        [
            new SnapshotEntry(directory.Id, @"D:\Backup\existing.txt", EntryType.File, 42, DateTimeOffset.UtcNow, FileAttributes.Normal)
        ]);
        var evt = new MonitorEvent
        {
            SequenceNumber = await _store.NextSequenceAsync(), DirectoryId = directory.Id, RootPath = directory.Path,
            Path = @"D:\Backup\pending.txt", MachineId = machineId, MachineName = "TEST", UserName = "tester",
            EventType = MonitorEventType.New
        };
        await _store.EnqueueEventAsync(evt, [destination.Id]);
        await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, "https://updates.test/manifest.json");

        var backupPath = await _store.CreateBackupAsync(Path.Combine(_folder, "update", "backup.db"));
        var backup = new SqliteConfigurationStore(backupPath);
        await backup.InitializeAsync();

        Assert.Equal(machineId, await backup.GetMachineIdAsync());
        Assert.Equal(directory.Path, Assert.Single(await backup.GetDirectoriesAsync()).Path);
        Assert.Equal(destination.Name, Assert.Single(await backup.GetDestinationsAsync()).Name);
        Assert.Single(await backup.LoadSnapshotAsync(directory.Id));
        Assert.Equal(1, await backup.GetPendingCountAsync(destination.Id));
        Assert.Equal("https://updates.test/manifest.json", await backup.GetSettingAsync(UpdateSettingNames.ManifestUrl));
    }

    [Fact]
    public void Default_and_environment_override_database_paths_are_resolved_correctly()
    {
        lock (EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA");
            try
            {
                Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_DATA", null);
                var defaultStore = new SqliteConfigurationStore();
                Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data", "directory-monitor.db")), defaultStore.DatabasePath);
                IConfigurationStore contract = defaultStore;
                Assert.Equal(defaultStore.DatabasePath, contract.DatabasePath);

                var overrideFolder = Path.Combine(_folder, "override");
                Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_DATA", overrideFolder);
                var overriddenStore = new SqliteConfigurationStore();
                Assert.Equal(Path.GetFullPath(Path.Combine(overrideFolder, "directory-monitor.db")), overriddenStore.DatabasePath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_DATA", original);
            }
        }
    }

    [Fact]
    public void Configured_connection_string_resolves_relative_to_app_and_environment_override_has_priority()
    {
        lock (EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA");
            try
            {
                Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_DATA", null);
                Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CustomData", "monitor.db")),
                    SqliteConfigurationStore.ResolveConfiguredDatabasePath("Data Source=CustomData\\monitor.db;Mode=ReadWriteCreate"));
                Assert.Throws<InvalidOperationException>(() => SqliteConfigurationStore.ResolveConfiguredDatabasePath(string.Empty));
                var overrideFolder = Path.Combine(_folder, "environment-data");
                Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_DATA", overrideFolder);
                Assert.Equal(Path.Combine(overrideFolder, "directory-monitor.db"),
                    SqliteConfigurationStore.ResolveConfiguredDatabasePath("Data Source=ignored.db"));
            }
            finally { Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_DATA", original); }
        }
    }
}
