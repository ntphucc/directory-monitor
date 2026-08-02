using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Application;

public static class UpdateSettingNames
{
    public const string ManifestUrl = "UpdateManifestUrl";
    public const string Schedule = "UpdateSchedule";
    public const string LastAttemptUtc = "LastUpdateAttemptUtc";
    public const string LastSuccessfulCheckUtc = "LastSuccessfulUpdateCheckUtc";
    public const string SkippedVersion = "SkippedUpdateVersion";
}

public interface IConfigurationStore
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Guid> GetMachineIdAsync(CancellationToken cancellationToken = default);
    Task<long> NextSequenceAsync(CancellationToken cancellationToken = default);
    Task<long> GetLatestEventSequenceAsync(CancellationToken cancellationToken = default);
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonitoredDirectory>> GetDirectoriesAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task UpsertDirectoryAsync(MonitoredDirectory directory, CancellationToken cancellationToken = default);
    Task SoftDeleteDirectoryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutputDestination>> GetDestinationsAsync(CancellationToken cancellationToken = default);
    Task UpsertDestinationAsync(OutputDestination destination, CancellationToken cancellationToken = default);
    Task DeleteDestinationAsync(Guid id, bool discardPending = false, CancellationToken cancellationToken = default);
    Task EnqueueEventAsync(MonitorEvent monitorEvent, IReadOnlyCollection<Guid> destinationIds, CancellationToken cancellationToken = default);
    Task<int> ImportEventsAsync(IReadOnlyCollection<MonitorEvent> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingDelivery>> GetPendingDeliveriesAsync(Guid destinationId, int take, CancellationToken cancellationToken = default);
    Task MarkDeliveredAsync(Guid destinationId, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid destinationId, IReadOnlyCollection<Guid> eventIds, string error, DateTimeOffset nextAttemptUtc, CancellationToken cancellationToken = default);
    Task<long> GetPendingCountAsync(Guid? destinationId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, string>> GetDeliveryStatusesAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);
    Task CancelPendingAsync(IReadOnlyCollection<Guid> destinationIds, long throughSequence, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, SnapshotEntry>> LoadSnapshotAsync(Guid directoryId, CancellationToken cancellationToken = default);
    Task ReplaceSnapshotAsync(Guid directoryId, IReadOnlyCollection<SnapshotEntry> entries, CancellationToken cancellationToken = default);
}

public interface IRestartRequirementState
{
    bool IsDatabaseRestartRequired { get; }
    string? DatabaseRestartMessage { get; }
    event EventHandler? Changed;
    void RequireDatabaseRestart(string message);
}

public interface IThemeService : IDisposable
{
    AppThemeMode Mode { get; }
    AppThemeMode EffectiveMode { get; }
    event EventHandler? ThemeChanged;
    void Apply(AppThemeMode mode);
}

public interface IImportExportService
{
    Task ExportAsync(string destinationPath, TransferFormat format, TransferSelection selection,
        IReadOnlyCollection<MonitoredDirectory> directories, IReadOnlyCollection<MonitorEvent> events,
        IReadOnlyCollection<OutputDestination> outputs, string? password, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
    Task<TransferEnvelopeV1> ReadAsync(string sourcePath, string? password, CancellationToken cancellationToken = default);
    Task<ImportSummary> ImportAsync(TransferEnvelopeV1 envelope, TransferSelection selection,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public interface IFileMonitorService
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ISnapshotService
{
    Task<SnapshotScanResult> ScanAsync(MonitoredDirectory directory, CancellationToken cancellationToken = default);
}

public interface ILogDestination
{
    DestinationType Type { get; }
    Task EnsureReadyAsync(OutputDestination destination, CancellationToken cancellationToken = default);
    Task DeliverAsync(OutputDestination destination, IReadOnlyList<MonitorEvent> events, CancellationToken cancellationToken = default);
}

public interface ILogDestinationFactory
{
    ILogDestination Get(DestinationType type);
}

public interface IEventStream
{
    event EventHandler<MonitorEvent>? EventReceived;
    void Publish(MonitorEvent monitorEvent);
}

public interface IDeliveryStatusStream
{
    event EventHandler<IReadOnlyCollection<Guid>>? StatusChanged;
    void Publish(IReadOnlyCollection<Guid> eventIds);
}

public interface IDirectoryHealthStream
{
    event EventHandler<DirectoryHealth>? HealthChanged;
    IReadOnlyDictionary<Guid, DirectoryHealth> Current { get; }
    void Publish(DirectoryHealth health);
}

public interface ITextLogMaintenance
{
    Task ClearActiveLogsAsync(long throughSequence, CancellationToken cancellationToken = default);
}

public interface IUpdateService
{
    event EventHandler<UpdateState>? StateChanged;
    UpdateState Current { get; }
    Version CurrentVersion { get; }
    Task<UpdateState> CheckAsync(bool manual, CancellationToken cancellationToken = default);
    Task<string> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task SkipVersionAsync(Version version, CancellationToken cancellationToken = default);
    Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default);
    void MarkInstalling();
}

public interface IApplicationDataBackupService
{
    Task<string> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public sealed class EventStream : IEventStream
{
    public event EventHandler<MonitorEvent>? EventReceived;
    public void Publish(MonitorEvent monitorEvent) => EventReceived?.Invoke(this, monitorEvent);
}

public sealed class DeliveryStatusStream : IDeliveryStatusStream
{
    public event EventHandler<IReadOnlyCollection<Guid>>? StatusChanged;
    public void Publish(IReadOnlyCollection<Guid> eventIds) => StatusChanged?.Invoke(this, eventIds);
}

public sealed class DirectoryHealthStream : IDirectoryHealthStream
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DirectoryHealth> _current = [];
    public event EventHandler<DirectoryHealth>? HealthChanged;
    public IReadOnlyDictionary<Guid, DirectoryHealth> Current => _current.ToDictionary();

    public void Publish(DirectoryHealth health)
    {
        if (_current.TryGetValue(health.DirectoryId, out var existing) && existing.Status == health.Status && existing.Message == health.Message) return;
        _current[health.DirectoryId] = health;
        HealthChanged?.Invoke(this, health);
    }
}
