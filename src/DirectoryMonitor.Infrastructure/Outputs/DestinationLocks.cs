using System.Collections.Concurrent;

namespace DirectoryMonitor.Infrastructure.Outputs;

internal static class DestinationLocks
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = [];
    public static SemaphoreSlim For(Guid destinationId) => Locks.GetOrAdd(destinationId, _ => new SemaphoreSlim(1, 1));
}
