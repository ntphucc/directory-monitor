using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure;

internal static class OperationalEventLog
{
    public static void TryWrite(int eventId, string message, ILogger logger)
    {
        try
        {
            if (EventLog.SourceExists("Directory Monitor")) EventLog.WriteEntry("Directory Monitor", message, EventLogEntryType.Error, eventId);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Unable to write operational event {EventId} to Windows Event Log", eventId); }
    }
}
