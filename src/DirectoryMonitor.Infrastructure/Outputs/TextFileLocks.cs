using System.Collections.Concurrent;

namespace DirectoryMonitor.Infrastructure.Outputs;

internal static class TextFileLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    public static SemaphoreSlim For(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
}
