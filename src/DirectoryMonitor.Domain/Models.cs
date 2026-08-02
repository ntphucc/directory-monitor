using System.Text.Json.Serialization;

namespace DirectoryMonitor.Domain;

public enum MonitorEventType
{
    New = 1,
    Change = 2,
    ModifyProperties = 3,
    Delete = 4
}

public enum EntryType { File = 1, Directory = 2 }
public enum EntryTypeFilter { FilesAndDirectories = 0, FilesOnly = 1, DirectoriesOnly = 2 }
public enum DestinationType { Text = 1, PostgreSql = 2, SqlServer = 3, WindowsEventLog = 4 }
public enum TextOutputFormat { PlainText = 1, JsonLines = 2, Csv = 3, CustomTemplate = 4 }
public enum DeliveryState { Pending = 0, Delivered = 1, Failed = 2, CancelledByUser = 3 }
public enum DirectoryMonitorStatus { Monitoring = 1, Disabled = 2, Unavailable = 3, Reconnecting = 4, Error = 5 }
public enum UpdateCheckSchedule { EveryStartup = 1, Daily = 2, Monthly = 3, Never = 4 }
public enum UpdateStatus { NotConfigured = 0, Idle = 1, Checking = 2, UpToDate = 3, Available = 4, Downloading = 5, Installing = 6, Error = 7 }
public enum AppThemeMode { System = 0, Light = 1, Dark = 2 }
public enum TransferFormat { Json = 1, Xml = 2, Dm = 3 }

[Flags]
public enum MonitoredEventKinds
{
    None = 0,
    New = 1,
    Change = 2,
    ModifyProperties = 4,
    Delete = 8,
    All = New | Change | ModifyProperties | Delete
}

public sealed record MonitoredDirectory
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Path { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsDisabled { get; init; }
    public bool IsDeleted { get; init; }
    public bool MonitorSubdirectories { get; init; } = true;
    public bool EnableDailyReconciliation { get; init; } = true;
    public MonitoredEventKinds EventKinds { get; init; } = MonitoredEventKinds.All;
    public EntryTypeFilter EntryTypeFilter { get; init; } = EntryTypeFilter.FilesAndDirectories;
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    public IReadOnlyList<Guid> DestinationIds { get; init; } = [];
    public DateTimeOffset CreationTimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModificationUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record OutputDestination
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public DestinationType Type { get; init; }
    public bool IsDisabled { get; init; }
    public string ConfigurationJson { get; init; } = "{}";
    public string Status { get; init; } = "Ready";
    public long PendingCount { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreationTimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModificationUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MonitorEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long SequenceNumber { get; init; }
    public DateTimeOffset EventTimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public MonitorEventType EventType { get; init; }
    public EntryType EntryType { get; init; }
    public Guid DirectoryId { get; init; }
    public string RootPath { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? OldPath { get; init; }
    public long? FileSize { get; init; }
    public string UserName { get; init; } = string.Empty;
    public Guid MachineId { get; init; }
    public string MachineName { get; init; } = string.Empty;
    public bool IsRecovered { get; init; }
    public DateTimeOffset CreationTimeUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TextDestinationOptions
{
    public string FilePath { get; init; } = string.Empty;
    public TextOutputFormat Format { get; init; } = TextOutputFormat.CustomTemplate;
    public string Template { get; init; } = "[{EventTime:yyyy-MM-dd HH:mm:ss.fff}] [{EventType}] [{EntryType}] Path=\"{Path}\" OldPath=\"{OldPath}\" User=\"{UserName}\" Machine=\"{MachineName}\"";
    public int RotateSizeMb { get; init; } = 100;
    public int RetentionDays { get; init; } = 30;
}

public sealed record DatabaseDestinationOptions
{
    public string ProtectedConnectionString { get; init; } = string.Empty;
}

public sealed record DestinationHealth(
    Guid DestinationId,
    string Name,
    DestinationType Type,
    string State,
    long PendingCount,
    string? LastError,
    DateTimeOffset CheckedAtUtc);

public sealed record DirectoryHealth(
    Guid DirectoryId,
    DirectoryMonitorStatus Status,
    string? Message,
    DateTimeOffset ChangedAtUtc);

public sealed record UpdateRelease
{
    public required Version Version { get; init; }
    public required DateTimeOffset PublishedAtUtc { get; init; }
    public IReadOnlyList<string> ReleaseNotesEnglish { get; init; } = [];
    public IReadOnlyList<string> ReleaseNotesVietnamese { get; init; } = [];
    public required Uri InstallerUri { get; init; }
    public required string InstallerSha256 { get; init; }
    public required long InstallerSizeBytes { get; init; }
}

public sealed record UpdateState
{
    public UpdateStatus Status { get; init; } = UpdateStatus.Idle;
    public UpdateRelease? Release { get; init; }
    public double Progress { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset ChangedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class UpdateSchedulePolicy
{
    public static bool IsDue(UpdateCheckSchedule schedule, DateTimeOffset? lastSuccessfulUtc, DateTimeOffset? lastAttemptUtc, DateTimeOffset nowUtc, bool checkedThisStartup)
    {
        if (schedule == UpdateCheckSchedule.Never) return false;
        if (lastAttemptUtc.HasValue && (!lastSuccessfulUtc.HasValue || lastAttemptUtc > lastSuccessfulUtc))
            return nowUtc - lastAttemptUtc.Value >= TimeSpan.FromHours(6);
        if (schedule == UpdateCheckSchedule.EveryStartup) return !checkedThisStartup;
        if (!lastSuccessfulUtc.HasValue) return true;
        var interval = schedule == UpdateCheckSchedule.Monthly ? TimeSpan.FromDays(30) : TimeSpan.FromDays(1);
        return nowUtc - lastSuccessfulUtc.Value >= interval;
    }
}

public sealed record SnapshotEntry(
    Guid DirectoryId,
    string Path,
    EntryType EntryType,
    long? Size,
    DateTimeOffset? LastWriteTimeUtc,
    FileAttributes Attributes);

public sealed record SnapshotScanResult(
    IReadOnlyDictionary<string, SnapshotEntry> Entries,
    bool IsComplete);

public sealed record PendingDelivery(OutputDestination Destination, MonitorEvent Event, int Attempts);

public static class EventTypeExtensions
{
    public static string DisplayName(this MonitorEventType type) => type switch
    {
        MonitorEventType.ModifyProperties => "Modify Properties",
        _ => type.ToString()
    };

    public static MonitoredEventKinds ToFlag(this MonitorEventType type) => type switch
    {
        MonitorEventType.New => MonitoredEventKinds.New,
        MonitorEventType.Change => MonitoredEventKinds.Change,
        MonitorEventType.ModifyProperties => MonitoredEventKinds.ModifyProperties,
        MonitorEventType.Delete => MonitoredEventKinds.Delete,
        _ => MonitoredEventKinds.None
    };
}
