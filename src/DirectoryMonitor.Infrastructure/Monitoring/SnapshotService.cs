using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryMonitor.Infrastructure.Monitoring;

public sealed class SnapshotService : ISnapshotService
{
    private readonly ILogger<SnapshotService> _logger;
    public SnapshotService() : this(NullLogger<SnapshotService>.Instance) { }
    public SnapshotService(ILogger<SnapshotService> logger) => _logger = logger;
    public Task<SnapshotScanResult> ScanAsync(MonitoredDirectory directory, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(directory, cancellationToken), cancellationToken);

    private SnapshotScanResult Scan(MonitoredDirectory directory, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(directory.Path)) return new SnapshotScanResult(result, false);
        var pending = new Stack<string>();
        pending.Push(directory.Path);
        var isComplete = true;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = pending.Pop();
            IEnumerator<string> paths;
            try { paths = System.IO.Directory.EnumerateFileSystemEntries(parent).GetEnumerator(); }
            catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Snapshot scan cannot enumerate {Path}", parent); isComplete = false; continue; }
            catch (IOException ex) { _logger.LogWarning(ex, "Snapshot scan cannot enumerate {Path}", parent); isComplete = false; continue; }

            using (paths)
            {
                while (true)
                {
                    string path;
                    try { if (!paths.MoveNext()) break; path = paths.Current; }
                    catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Snapshot scan enumeration stopped at {Path}", parent); isComplete = false; break; }
                    catch (IOException ex) { _logger.LogWarning(ex, "Snapshot scan enumeration stopped at {Path}", parent); isComplete = false; break; }
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(directory.Path, path);
                        var attributes = File.GetAttributes(path);
                        if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        var entryType = attributes.HasFlag(FileAttributes.Directory) ? EntryType.Directory : EntryType.File;
                        if (entryType == EntryType.Directory && directory.MonitorSubdirectories) pending.Push(path);
                        if (!GlobMatcher.IsMatch(relative, directory.IncludePatterns, directory.ExcludePatterns)) continue;
                        if (directory.EntryTypeFilter == EntryTypeFilter.FilesOnly && entryType == EntryType.Directory) continue;
                        if (directory.EntryTypeFilter == EntryTypeFilter.DirectoriesOnly && entryType == EntryType.File) continue;
                        var info = entryType == EntryType.File ? new FileInfo(path) : null;
                        result[path] = new SnapshotEntry(directory.Id, path, entryType, info?.Length, entryType == EntryType.File ? info!.LastWriteTimeUtc : System.IO.Directory.GetLastWriteTimeUtc(path), attributes);
                    }
                    catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Snapshot scan cannot inspect an entry under {Path}", parent); isComplete = false; }
                    catch (IOException ex) { _logger.LogWarning(ex, "Snapshot scan cannot inspect an entry under {Path}", parent); isComplete = false; }
                }
            }
        }
        return new SnapshotScanResult(result, isComplete);
    }
}
