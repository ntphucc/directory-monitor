using System.Diagnostics;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class WindowsEventLogDestination : ILogDestination
{
    public DestinationType Type => DestinationType.WindowsEventLog;
    public Task EnsureReadyAsync(OutputDestination destination, CancellationToken cancellationToken = default)
    {
        if (!EventLog.SourceExists("Directory Monitor")) throw new InvalidOperationException("Windows Event Log source 'Directory Monitor' is not registered. Run the installer as Administrator.");
        return Task.CompletedTask;
    }
    public async Task DeliverAsync(OutputDestination destination, IReadOnlyList<MonitorEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(destination, cancellationToken);
        await Task.Run(() => { foreach (var e in events) { cancellationToken.ThrowIfCancellationRequested(); var id = 1000 + (int)e.EventType; EventLog.WriteEntry("Directory Monitor", $"{e.EventType.DisplayName()} {e.EntryType}: {e.Path}\nOldPath: {e.OldPath}\nUser: {e.UserName}\nMachine: {e.MachineName}\nEventId: {e.Id}", EventLogEntryType.Information, id); } }, cancellationToken);
    }
}
