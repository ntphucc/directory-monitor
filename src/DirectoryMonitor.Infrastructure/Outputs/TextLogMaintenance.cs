using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class TextLogMaintenance(IConfigurationStore store, ILogger<TextLogMaintenance> logger) : ITextLogMaintenance
{
    public async Task ClearActiveLogsAsync(long throughSequence, CancellationToken cancellationToken = default)
    {
        var textDestinations = (await store.GetDestinationsAsync(cancellationToken)).Where(item => item.Type == DestinationType.Text).ToArray();
        foreach (var destination in textDestinations)
        {
            var destinationLock = DestinationLocks.For(destination.Id); await destinationLock.WaitAsync(cancellationToken);
            try
            {
                await store.CancelPendingAsync([destination.Id], throughSequence, cancellationToken);
            var path = Environment.ExpandEnvironmentVariables(TextLogDestination.Deserialize(destination).FilePath); if (string.IsNullOrWhiteSpace(path)) continue;
            var fileLock = TextFileLocks.For(path); await fileLock.WaitAsync(cancellationToken);
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, string.Empty, cancellationToken); }
            catch (Exception ex) { logger.LogError(ex, "Unable to clear active text log for destination {DestinationId}", destination.Id); }
            finally { fileLock.Release(); }
            }
            finally { destinationLock.Release(); }
        }
    }
}
