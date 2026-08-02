using System.Collections.Concurrent;
using System.Security.Principal;
using System.Threading.Channels;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure.Monitoring;

public sealed class FileMonitorService : IFileMonitorService, IAsyncDisposable
{
    private readonly IConfigurationStore _store;
    private readonly ISnapshotService _snapshots;
    private readonly IEventStream _events;
    private readonly IDirectoryHealthStream _health;
    private readonly ILogger<FileMonitorService> _logger;
    private readonly Channel<RawEvent> _channel = Channel.CreateBounded<RawEvent>(new BoundedChannelOptions(20_000) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Guid, WatchContext> _watchers = [];
    private readonly ConcurrentDictionary<Guid, byte> _everMonitored = [];
    private readonly ConcurrentDictionary<string, SnapshotEntry> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _reconcileRequests = [];
    private CancellationTokenSource? _cts;
    private Task? _processor;
    private Task? _dailyReconciler;
    private Guid _machineId;

    public FileMonitorService(IConfigurationStore store, ISnapshotService snapshots, IEventStream events, IDirectoryHealthStream health, ILogger<FileMonitorService> logger)
        => (_store, _snapshots, _events, _health, _logger) = (store, snapshots, events, health, logger);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return;
        _machineId = await _store.GetMachineIdAsync(cancellationToken);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processor = ProcessLoopAsync(_cts.Token);
        await ReloadAsync(cancellationToken);
        _dailyReconciler = DailyReconcileLoopAsync(_cts.Token);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var context in _watchers.Values) context.Dispose();
        _watchers.Clear();
        _metadata.Clear();
        foreach (var directory in await _store.GetDirectoriesAsync(false, cancellationToken))
        {
            if (directory.IsDeleted) continue;
            if (directory.IsDisabled) { PublishHealth(directory.Id, DirectoryMonitorStatus.Disabled); continue; }
            try
            {
                await ReconcileAsync(directory, true, cancellationToken);
                TryStartWatcher(directory);
            }
            catch (Exception ex)
            {
                PublishHealth(directory.Id, DirectoryMonitorStatus.Error, ex.Message);
                _logger.LogError(ex, "Cannot initialize monitor for {Path}", directory.Path);
                RequestReconcile(directory.Id, TimeSpan.FromSeconds(5));
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null) return;
        _cts.Cancel();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        if (_processor is not null) await IgnoreCancellation(_processor);
        if (_dailyReconciler is not null) await IgnoreCancellation(_dailyReconciler);
        _cts.Dispose(); _cts = null;
    }

    private bool TryStartWatcher(MonitoredDirectory directory)
    {
        if (!System.IO.Directory.Exists(directory.Path))
        {
            var status = _everMonitored.ContainsKey(directory.Id) ? DirectoryMonitorStatus.Reconnecting : DirectoryMonitorStatus.Unavailable;
            PublishHealth(directory.Id, status, "The path is unavailable.");
            RequestReconcile(directory.Id, TimeSpan.FromSeconds(5));
            return false;
        }
        try
        {
            var watcher = new FileSystemWatcher(directory.Path)
            {
                IncludeSubdirectories = directory.MonitorSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false
            };
            watcher.Created += (_, e) => Enqueue(directory, e.FullPath, null, WatcherChangeTypes.Created);
            watcher.Changed += (_, e) => Enqueue(directory, e.FullPath, null, WatcherChangeTypes.Changed);
            watcher.Deleted += (_, e) => Enqueue(directory, e.FullPath, null, WatcherChangeTypes.Deleted);
            watcher.Renamed += (_, e) => Enqueue(directory, e.FullPath, e.OldFullPath, WatcherChangeTypes.Renamed);
            watcher.Error += (_, e) => { var error = e.GetException(); PublishHealth(directory.Id, DirectoryMonitorStatus.Reconnecting, error.Message); _logger.LogWarning(error, "Watcher overflow/error for {Path}", directory.Path); OperationalEventLog.TryWrite(9001, $"Watcher error for {directory.Path}\n{error.Message}", _logger); RequestReconcile(directory.Id, TimeSpan.Zero); };
            watcher.EnableRaisingEvents = true;
            _watchers[directory.Id] = new WatchContext(directory, watcher);
            _everMonitored[directory.Id] = 0;
            PublishHealth(directory.Id, DirectoryMonitorStatus.Monitoring);
            return true;
        }
        catch (Exception ex) { PublishHealth(directory.Id, DirectoryMonitorStatus.Error, ex.Message); _logger.LogError(ex, "Cannot watch {Path}", directory.Path); OperationalEventLog.TryWrite(9001, $"Cannot watch {directory.Path}\n{ex.Message}", _logger); RequestReconcile(directory.Id, TimeSpan.FromSeconds(5)); return false; }
    }

    private void Enqueue(MonitoredDirectory directory, string path, string? oldPath, WatcherChangeTypes change)
    {
        if (!_channel.Writer.TryWrite(new RawEvent(directory, path, oldPath, change, DateTimeOffset.UtcNow))) { PublishHealth(directory.Id, DirectoryMonitorStatus.Reconnecting, "The watcher event queue overflowed."); RequestReconcile(directory.Id, TimeSpan.Zero); }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        var debounce = new Dictionary<string, RawEvent>(StringComparer.OrdinalIgnoreCase);
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_channel.Reader.TryRead(out var raw))
            {
                if (raw.Change == WatcherChangeTypes.Changed) debounce[$"{raw.Directory.Id:N}|{raw.Path}"] = raw;
                else await ProcessRawAsync(raw, false, cancellationToken);
            }
            var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-500);
            foreach (var key in debounce.Where(item => item.Value.ObservedAt <= cutoff).Select(item => item.Key).ToArray())
            {
                var raw = debounce[key]; debounce.Remove(key); await ProcessRawAsync(raw, false, cancellationToken);
            }
            foreach (var request in _reconcileRequests.Where(item => item.Value <= DateTimeOffset.UtcNow).ToArray())
            {
                var id = request.Key;
                if (!_reconcileRequests.TryRemove(id, out _)) continue;
                if (_watchers.TryGetValue(id, out var context))
                {
                    context.Dispose(); _watchers.TryRemove(id, out _);
                    await RetryDirectoryAsync(context.Directory, cancellationToken);
                }
                else
                {
                    var directory = (await _store.GetDirectoriesAsync(false, cancellationToken)).FirstOrDefault(d => d.Id == id);
                    if (directory is not null && !directory.IsDisabled && !directory.IsDeleted) await RetryDirectoryAsync(directory, cancellationToken);
                }
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private void RequestReconcile(Guid directoryId, TimeSpan delay)
    {
        var due = DateTimeOffset.UtcNow.Add(delay);
        _reconcileRequests.AddOrUpdate(directoryId, due, (_, existing) => due < existing ? due : existing);
    }

    private async Task RetryDirectoryAsync(MonitoredDirectory directory, CancellationToken cancellationToken)
    {
        if (_everMonitored.ContainsKey(directory.Id)) PublishHealth(directory.Id, DirectoryMonitorStatus.Reconnecting, "Re-establishing the watcher.");
        try
        {
            await ReconcileAsync(directory, true, cancellationToken);
            TryStartWatcher(directory);
        }
        catch (Exception ex)
        {
            PublishHealth(directory.Id, DirectoryMonitorStatus.Error, ex.Message);
            _logger.LogWarning(ex, "Monitor recovery failed for {Path}", directory.Path);
            RequestReconcile(directory.Id, TimeSpan.FromSeconds(5));
        }
    }

    private void PublishHealth(Guid directoryId, DirectoryMonitorStatus status, string? message = null)
        => _health.Publish(new DirectoryHealth(directoryId, status, message, DateTimeOffset.UtcNow));

    private async Task ProcessRawAsync(RawEvent raw, bool recovered, CancellationToken cancellationToken)
    {
        var directory = raw.Directory;
        if (IsUnderReparsePoint(directory.Path, raw.Path)) return;
        var relative = Path.GetRelativePath(directory.Path, raw.Path);
        if (!GlobMatcher.IsMatch(relative, directory.IncludePatterns, directory.ExcludePatterns)) return;
        var type = raw.Change switch { WatcherChangeTypes.Created => MonitorEventType.New, WatcherChangeTypes.Deleted => MonitorEventType.Delete, WatcherChangeTypes.Renamed => MonitorEventType.Change, _ => ClassifyChange(directory.Id, raw.Path) };
        if (type is null)
        {
            var current = GetMetadata(directory.Id, raw.Path, null);
            if (current is not null) _metadata[raw.Path] = current;
            return;
        }
        if (!directory.EventKinds.HasFlag(type.Value.ToFlag())) return;

        var entry = GetMetadata(directory.Id, raw.Path, raw.Change == WatcherChangeTypes.Deleted ? _metadata.GetValueOrDefault(raw.Path) : null);
        if (entry is null && raw.Change != WatcherChangeTypes.Deleted) return;
        var entryType = entry?.EntryType ?? EntryType.File;
        if (directory.EntryTypeFilter == EntryTypeFilter.FilesOnly && entryType == EntryType.Directory) return;
        if (directory.EntryTypeFilter == EntryTypeFilter.DirectoriesOnly && entryType == EntryType.File) return;
        if (raw.Change == WatcherChangeTypes.Deleted) _metadata.TryRemove(raw.Path, out _); else if (entry is not null) _metadata[raw.Path] = entry;
        if (raw.Change == WatcherChangeTypes.Renamed && raw.OldPath is not null) _metadata.TryRemove(raw.OldPath, out _);

        var monitorEvent = new MonitorEvent
        {
            SequenceNumber = await _store.NextSequenceAsync(cancellationToken), EventTimeUtc = raw.ObservedAt, EventType = type.Value, EntryType = entryType,
            DirectoryId = directory.Id, RootPath = directory.Path, Path = raw.Path, OldPath = raw.OldPath, FileSize = entry?.Size,
            UserName = WindowsIdentity.GetCurrent().Name, MachineId = _machineId, MachineName = Environment.MachineName, IsRecovered = recovered
        };
        await _store.EnqueueEventAsync(monitorEvent, directory.DestinationIds, cancellationToken);
        _events.Publish(monitorEvent);
    }

    private MonitorEventType? ClassifyChange(Guid directoryId, string path)
    {
        var old = _metadata.GetValueOrDefault(path); var current = GetMetadata(old?.DirectoryId ?? directoryId, path, null);
        if (current?.EntryType == EntryType.Directory)
            return old is not null && old.Attributes != current.Attributes ? MonitorEventType.ModifyProperties : null;
        if (old is not null && current is not null && old.Size == current.Size && old.LastWriteTimeUtc == current.LastWriteTimeUtc && old.Attributes != current.Attributes) return MonitorEventType.ModifyProperties;
        return MonitorEventType.Change;
    }

    private SnapshotEntry? GetMetadata(Guid directoryId, string path, SnapshotEntry? fallback)
    {
        try
        {
            var attributes = File.GetAttributes(path); var type = attributes.HasFlag(FileAttributes.Directory) ? EntryType.Directory : EntryType.File;
            long? size = type == EntryType.File ? new FileInfo(path).Length : null; var write = type == EntryType.File ? File.GetLastWriteTimeUtc(path) : System.IO.Directory.GetLastWriteTimeUtc(path);
            return new SnapshotEntry(directoryId, path, type, size, write, attributes);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Filesystem metadata is unavailable for {Path}", path); return fallback; }
    }

    private bool IsUnderReparsePoint(string root, string path)
    {
        try
        {
            var current = System.IO.Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrWhiteSpace(current))
            {
                var normalized = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) break;
                if (File.GetAttributes(normalized).HasFlag(FileAttributes.ReparsePoint)) return true;
                if (string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(normalized);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not inspect reparse-point ancestry for {Path}", path); }
        return false;
    }

    private async Task ReconcileAsync(MonitoredDirectory directory, bool emitRecovered, CancellationToken cancellationToken)
    {
        var previous = await _store.LoadSnapshotAsync(directory.Id, cancellationToken);
        foreach (var pair in previous) _metadata[pair.Key] = pair.Value;
        var scan = await _snapshots.ScanAsync(directory, cancellationToken);
        var current = scan.Entries;
        foreach (var pair in current) _metadata[pair.Key] = pair.Value;
        if (emitRecovered && previous.Count > 0)
        {
            foreach (var entry in current.Values.Where(entry => !previous.ContainsKey(entry.Path))) await EmitSnapshotAsync(directory, entry, MonitorEventType.New, cancellationToken);
            if (scan.IsComplete)
                foreach (var entry in previous.Values.Where(entry => !current.ContainsKey(entry.Path))) await EmitSnapshotAsync(directory, entry, MonitorEventType.Delete, cancellationToken);
            foreach (var entry in current.Values)
            {
                if (!previous.TryGetValue(entry.Path, out var old)) continue;
                if (entry.EntryType == EntryType.Directory)
                {
                    if (old.Attributes != entry.Attributes) await EmitSnapshotAsync(directory, entry, MonitorEventType.ModifyProperties, cancellationToken);
                    continue;
                }
                if (old.Size != entry.Size || old.LastWriteTimeUtc != entry.LastWriteTimeUtc) await EmitSnapshotAsync(directory, entry, MonitorEventType.Change, cancellationToken);
                else if (old.Attributes != entry.Attributes) await EmitSnapshotAsync(directory, entry, MonitorEventType.ModifyProperties, cancellationToken);
            }
        }
        var persisted = scan.IsComplete
            ? current.Values
            : previous.Values.Where(item => !current.ContainsKey(item.Path)).Concat(current.Values);
        await _store.ReplaceSnapshotAsync(directory.Id, persisted.ToArray(), cancellationToken);
    }

    private async Task EmitSnapshotAsync(MonitoredDirectory directory, SnapshotEntry entry, MonitorEventType type, CancellationToken cancellationToken)
    {
        if (!directory.EventKinds.HasFlag(type.ToFlag())) return;
        var monitorEvent = new MonitorEvent { SequenceNumber = await _store.NextSequenceAsync(cancellationToken), EventType = type, EntryType = entry.EntryType, DirectoryId = directory.Id, RootPath = directory.Path, Path = entry.Path, FileSize = entry.Size, UserName = WindowsIdentity.GetCurrent().Name, MachineId = _machineId, MachineName = Environment.MachineName, IsRecovered = true };
        await _store.EnqueueEventAsync(monitorEvent, directory.DestinationIds, cancellationToken); _events.Publish(monitorEvent);
    }

    private async Task DailyReconcileLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            if (DateTime.Now.Hour != 2) continue;
            foreach (var context in _watchers.Values.Where(w => w.Directory.EnableDailyReconciliation))
            {
                try { await ReconcileAsync(context.Directory, true, cancellationToken); }
                catch (Exception ex) { _logger.LogError(ex, "Daily reconciliation failed for {Path}", context.Directory.Path); PublishHealth(context.Directory.Id, DirectoryMonitorStatus.Error, ex.Message); RequestReconcile(context.Directory.Id, TimeSpan.FromSeconds(5)); }
            }
            await Task.Delay(TimeSpan.FromHours(1), cancellationToken);
        }
    }

    private async Task IgnoreCancellation(Task task) { try { await task; } catch (OperationCanceledException ex) { _logger.LogDebug(ex, "Monitoring background task cancelled during shutdown"); } }
    public async ValueTask DisposeAsync() => await StopAsync();
    private sealed record RawEvent(MonitoredDirectory Directory, string Path, string? OldPath, WatcherChangeTypes Change, DateTimeOffset ObservedAt);
    private sealed record WatchContext(MonitoredDirectory Directory, FileSystemWatcher Watcher) : IDisposable { public void Dispose() => Watcher.Dispose(); }
}
