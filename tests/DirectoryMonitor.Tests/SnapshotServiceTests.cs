using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Monitoring;

namespace DirectoryMonitor.Tests;

public sealed class SnapshotServiceTests
{
    [Fact]
    public async Task Missing_root_produces_incomplete_scan()
    {
        var directory = new MonitoredDirectory { Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
        var result = await new SnapshotService().ScanAsync(directory);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Scan_respects_file_only_filter()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.SnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "child"));
        await File.WriteAllTextAsync(Path.Combine(root, "child", "a.txt"), "a");
        try
        {
            var result = await new SnapshotService().ScanAsync(new MonitoredDirectory { Path = root, EntryTypeFilter = EntryTypeFilter.FilesOnly });
            Assert.True(result.IsComplete);
            var entry = Assert.Single(result.Entries.Values);
            Assert.Equal(EntryType.File, entry.EntryType);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
