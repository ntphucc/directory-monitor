using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class OutputDispatcherService(IConfigurationStore store, ILogDestinationFactory factory, IDeliveryStatusStream statusStream, ILogger<OutputDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new Dictionary<Guid, Worker>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var destinations = (await store.GetDestinationsAsync(stoppingToken)).ToDictionary(item => item.Id);
                foreach (var existing in workers.Values.Where(worker => !destinations.TryGetValue(worker.Destination.Id, out var current) || current.IsDisabled || current.LastModificationUtc != worker.Destination.LastModificationUtc).ToArray())
                {
                    existing.Cancellation.Cancel();
                    await IgnoreCancellationAsync(existing.Task);
                    existing.Cancellation.Dispose();
                    workers.Remove(existing.Destination.Id);
                }
                foreach (var destination in destinations.Values.Where(item => !item.IsDisabled && !workers.ContainsKey(item.Id)))
                {
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    workers[destination.Id] = new Worker(destination, linked, DeliverLoopAsync(destination, linked.Token));
                }
                await Task.Delay(1_000, stoppingToken);
            }
        }
        finally
        {
            foreach (var worker in workers.Values) worker.Cancellation.Cancel();
            await Task.WhenAll(workers.Values.Select(worker => IgnoreCancellationAsync(worker.Task)));
            foreach (var worker in workers.Values) worker.Cancellation.Dispose();
        }
    }

    private async Task DeliverLoopAsync(OutputDestination destination, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var destinationLock = DestinationLocks.For(destination.Id);
            await destinationLock.WaitAsync(cancellationToken);
            IReadOnlyList<PendingDelivery> pending = [];
            try
            {
                pending = await store.GetPendingDeliveriesAsync(destination.Id, 100, cancellationToken);
                if (pending.Count > 0)
                {
                    var output = factory.Get(destination.Type);
                    await output.DeliverAsync(destination, pending.Select(item => item.Event).ToArray(), cancellationToken);
                    var eventIds = pending.Select(item => item.Event.Id).ToArray();
                    await store.MarkDeliveredAsync(destination.Id, eventIds, cancellationToken);
                    statusStream.Publish(eventIds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (pending.Count == 0) logger.LogWarning(ex, "Destination {Destination} polling failed", destination.Name);
                else
                {
                    var attempts = pending.Max(item => item.Attempts) + 1;
                    var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
                    var next = DateTimeOffset.UtcNow.AddSeconds(seconds + Random.Shared.NextDouble());
                    await store.MarkFailedAsync(destination.Id, pending.Select(item => item.Event.Id).ToArray(), ex.Message, next, cancellationToken);
                    statusStream.Publish(pending.Select(item => item.Event.Id).ToArray());
                    logger.LogWarning(ex, "Destination {Destination} delivery failed", destination.Name);
                    OperationalEventLog.TryWrite(9002, $"Destination '{destination.Name}' delivery failed.\n{ex.Message}", logger);
                }
            }
            finally { destinationLock.Release(); }
            if (pending.Count == 0) await Task.Delay(250, cancellationToken);
        }
    }

    private async Task IgnoreCancellationAsync(Task task) { try { await task; } catch (OperationCanceledException ex) { logger.LogDebug(ex, "Output worker cancelled during shutdown"); } }
    private sealed record Worker(OutputDestination Destination, CancellationTokenSource Cancellation, Task Task);
}
