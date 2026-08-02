using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Monitoring;
using DirectoryMonitor.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DirectoryMonitor.Tests;

public sealed class FileMonitorIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task Created_file_is_published_without_blocking_watcher()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N")); var root = Path.Combine(testRoot, "watched"); Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db")); await store.InitializeAsync();
            await store.UpsertDirectoryAsync(new MonitoredDirectory { Path = root, EventKinds = MonitoredEventKinds.All });
            var stream = new EventStream(); var completion = new TaskCompletionSource<MonitorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var health = new DirectoryHealthStream();
            stream.EventReceived += (_, e) => { if (e.Path.EndsWith("created.txt", StringComparison.OrdinalIgnoreCase)) completion.TrySetResult(e); };
            await using var monitor = new FileMonitorService(store, new SnapshotService(), stream, health, NullLogger<FileMonitorService>.Instance);
            await monitor.StartAsync(); await File.WriteAllTextAsync(Path.Combine(root, "created.txt"), "hello");
            Assert.Equal(DirectoryMonitorStatus.Monitoring, Assert.Single(health.Current).Value.Status);
            var evt = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(MonitorEventType.New, evt.EventType); Assert.Equal(EntryType.File, evt.EntryType);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    [Fact(Timeout = 20_000)]
    public async Task Creating_a_file_does_not_publish_a_change_for_its_parent_directory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "watched");
        var parent = Path.Combine(root, "existing-directory");
        Directory.CreateDirectory(parent);
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db"));
            await store.InitializeAsync();
            await store.UpsertDirectoryAsync(new MonitoredDirectory { Path = root, EventKinds = MonitoredEventKinds.All });
            var observed = new ConcurrentQueue<MonitorEvent>();
            var fileCreated = new TaskCompletionSource<MonitorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new EventStream();
            var file = Path.Combine(parent, "created.txt");
            stream.EventReceived += (_, item) =>
            {
                observed.Enqueue(item);
                if (item.Path == file && item.EventType == MonitorEventType.New) fileCreated.TrySetResult(item);
            };
            await using var monitor = new FileMonitorService(store, new SnapshotService(), stream, new DirectoryHealthStream(), NullLogger<FileMonitorService>.Instance);
            await monitor.StartAsync();

            await File.WriteAllTextAsync(file, "new file");
            await fileCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(1_200);

            Assert.DoesNotContain(observed, item => item.EntryType == EntryType.Directory && item.EventType == MonitorEventType.Change);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    [Fact(Timeout = 20_000)]
    public async Task Offline_file_creation_is_recovered_without_a_parent_directory_change()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "watched");
        var parent = Path.Combine(root, "existing-directory");
        Directory.CreateDirectory(parent);
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db"));
            await store.InitializeAsync();
            await store.UpsertDirectoryAsync(new MonitoredDirectory { Path = root, EventKinds = MonitoredEventKinds.All });
            await using (var baseline = new FileMonitorService(store, new SnapshotService(), new EventStream(), new DirectoryHealthStream(), NullLogger<FileMonitorService>.Instance))
            {
                await baseline.StartAsync();
                await baseline.StopAsync();
            }

            var file = Path.Combine(parent, "offline.txt");
            await File.WriteAllTextAsync(file, "created while stopped");
            var observed = new ConcurrentQueue<MonitorEvent>();
            var recoveredFile = new TaskCompletionSource<MonitorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new EventStream();
            stream.EventReceived += (_, item) =>
            {
                observed.Enqueue(item);
                if (item.Path == file && item.EventType == MonitorEventType.New && item.IsRecovered) recoveredFile.TrySetResult(item);
            };
            await using var recovered = new FileMonitorService(store, new SnapshotService(), stream, new DirectoryHealthStream(), NullLogger<FileMonitorService>.Instance);

            await recovered.StartAsync();
            await recoveredFile.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(observed, item => item.EntryType == EntryType.Directory && item.EventType == MonitorEventType.Change);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    [Fact(Timeout = 30_000)]
    public async Task Content_properties_rename_and_delete_are_classified()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N")); var root = Path.Combine(testRoot, "watched"); Directory.CreateDirectory(root);
        var original = Path.Combine(root, "original.txt"); var renamed = Path.Combine(root, "renamed.txt"); await File.WriteAllTextAsync(original, "baseline");
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db")); await store.InitializeAsync();
            await store.UpsertDirectoryAsync(new MonitoredDirectory { Path = root, EventKinds = MonitoredEventKinds.All });
            var channel = Channel.CreateUnbounded<MonitorEvent>(); var stream = new EventStream(); stream.EventReceived += (_, e) => channel.Writer.TryWrite(e);
            await using var monitor = new FileMonitorService(store, new SnapshotService(), stream, new DirectoryHealthStream(), NullLogger<FileMonitorService>.Instance);
            await monitor.StartAsync();

            await File.AppendAllTextAsync(original, " changed");
            Assert.Equal(MonitorEventType.Change, (await WaitForAsync(channel.Reader, e => e.Path == original && e.EventType == MonitorEventType.Change)).EventType);

            File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.Hidden);
            Assert.Equal(MonitorEventType.ModifyProperties, (await WaitForAsync(channel.Reader, e => e.Path == original && e.EventType == MonitorEventType.ModifyProperties)).EventType);

            File.Move(original, renamed);
            var rename = await WaitForAsync(channel.Reader, e => e.Path == renamed && e.OldPath == original);
            Assert.Equal(MonitorEventType.Change, rename.EventType);

            File.Delete(renamed);
            Assert.Equal(MonitorEventType.Delete, (await WaitForAsync(channel.Reader, e => e.Path == renamed && e.EventType == MonitorEventType.Delete)).EventType);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    [Fact(Timeout = 15_000)]
    public async Task Disabled_and_unavailable_directories_publish_runtime_health()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db")); await store.InitializeAsync();
            var disabled = new MonitoredDirectory { Path = testRoot, IsDisabled = true };
            var unavailable = new MonitoredDirectory { Path = Path.Combine(testRoot, "missing") };
            await store.UpsertDirectoryAsync(disabled); await store.UpsertDirectoryAsync(unavailable);
            var health = new DirectoryHealthStream();
            await using var monitor = new FileMonitorService(store, new SnapshotService(), new EventStream(), health, NullLogger<FileMonitorService>.Instance);

            await monitor.StartAsync();

            Assert.Equal(DirectoryMonitorStatus.Disabled, health.Current[disabled.Id].Status);
            Assert.Equal(DirectoryMonitorStatus.Unavailable, health.Current[unavailable.Id].Status);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    [Fact(Timeout = 20_000)]
    public async Task Active_directory_recovers_through_reconnecting_to_monitoring()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var watched = Path.Combine(testRoot, "watched");
        Directory.CreateDirectory(watched);
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db")); await store.InitializeAsync();
            var directory = new MonitoredDirectory { Path = watched };
            await store.UpsertDirectoryAsync(directory);
            var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var health = new DirectoryHealthStream();
            health.HealthChanged += (_, item) =>
            {
                if (item.DirectoryId != directory.Id) return;
                if (item.Status == DirectoryMonitorStatus.Reconnecting) reconnecting.TrySetResult();
            };
            await using var monitor = new FileMonitorService(store, new SnapshotService(), new EventStream(), health, NullLogger<FileMonitorService>.Instance);

            await monitor.StartAsync();
            Assert.Equal(DirectoryMonitorStatus.Monitoring, health.Current[directory.Id].Status);
            var monitoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            health.HealthChanged += (_, item) =>
            {
                if (item.DirectoryId == directory.Id && item.Status == DirectoryMonitorStatus.Monitoring) monitoring.TrySetResult();
            };
            Directory.Delete(watched, true);
            await monitor.ReloadAsync();
            Assert.Equal(DirectoryMonitorStatus.Reconnecting, health.Current[directory.Id].Status);
            Directory.CreateDirectory(watched);

            await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await monitoring.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DirectoryMonitorStatus.Monitoring, health.Current[directory.Id].Status);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    [Fact(Timeout = 15_000)]
    public async Task Snapshot_access_failure_publishes_error_health()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.WatcherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var store = new SqliteConfigurationStore(Path.Combine(testRoot, "state.db")); await store.InitializeAsync();
            var directory = new MonitoredDirectory { Path = testRoot };
            await store.UpsertDirectoryAsync(directory);
            var health = new DirectoryHealthStream();
            await using var monitor = new FileMonitorService(store, new ThrowingSnapshotService(), new EventStream(), health, NullLogger<FileMonitorService>.Instance);

            await monitor.StartAsync();

            Assert.Equal(DirectoryMonitorStatus.Error, health.Current[directory.Id].Status);
            Assert.Contains("denied", health.Current[directory.Id].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(testRoot, true); } catch { } }
    }

    private static async Task<MonitorEvent> WaitForAsync(ChannelReader<MonitorEvent> reader, Func<MonitorEvent, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await reader.WaitToReadAsync(timeout.Token)) while (reader.TryRead(out var item)) if (predicate(item)) return item;
        throw new TimeoutException("Expected filesystem event was not observed.");
    }

    private sealed class ThrowingSnapshotService : ISnapshotService
    {
        public Task<SnapshotScanResult> ScanAsync(MonitoredDirectory directory, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("Access denied by test.");
    }
}
