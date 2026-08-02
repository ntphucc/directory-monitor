using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Presentation.Collections;
using DirectoryMonitor.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Presentation.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationStore _store;
    private readonly IFileMonitorService _monitor;
    private readonly DialogService _dialogs;
    private readonly PathLauncher _launcher;
    private readonly IEventStream _stream;
    private readonly IDeliveryStatusStream _deliveryStatuses;
    private readonly IDirectoryHealthStream _directoryHealth;
    private readonly IUpdateService _updates;
    private readonly IApplicationDataBackupService _dataBackup;
    private readonly UpdateInstallerLauncher _installerLauncher;
    private readonly IThemeService _themeService;
    private readonly IRestartRequirementState _restartState;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IImportExportService _transferService;
    private CancellationTokenSource? _transferCancellation;
    private readonly object _uiQueueGate = new();
    private ConcurrentQueue<MonitorEvent> _incoming = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingStatusRefresh = new();
    private readonly Dictionary<Guid, EventRowViewModel> _eventRows = [];
    private bool _isRefreshingDeliveryStatuses;
    private bool _isRefreshingStatus;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _filterTimer;

    public ObservableCollection<DirectoryRowViewModel> Directories { get; } = [];
    public ObservableCollection<OutputDestination> Destinations { get; } = [];
    public BulkObservableCollection<EventRowViewModel> Events { get; } = [];
    public ICollectionView EventsView { get; }
    public Array EventTypes { get; } = Enum.GetValues<MonitorEventType>();
    public Array EntryTypes { get; } = Enum.GetValues<EntryType>();
    public IReadOnlyList<string> Languages { get; } = ["English", "Tiếng Việt"];
    public Array UpdateSchedules { get; } = Enum.GetValues<UpdateCheckSchedule>();
    public Array ThemeModes { get; } = Enum.GetValues<AppThemeMode>();
    public string CurrentVersion => _updates.CurrentVersion.ToString(3);
    public string DatabasePath => _store.DatabasePath;

    [ObservableProperty] private DirectoryRowViewModel? _selectedDirectory;
    [ObservableProperty] private OutputDestination? _selectedDestination;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private MonitorEventType? _selectedEventFilter;
    [ObservableProperty] private EntryType? _selectedEntryFilter;
    [ObservableProperty] private string _statusText = "Starting...";
    [ObservableProperty] private long _pendingCount;
    [ObservableProperty] private int _uiBufferLimit = 10_000;
    [ObservableProperty] private bool _isStartupEnabled;
    [ObservableProperty] private AppThemeMode _selectedThemeMode = AppThemeMode.System;
    [ObservableProperty] private bool _isDatabaseRestartRequired;
    [ObservableProperty] private string _databaseRestartMessage = string.Empty;
    [ObservableProperty] private string _selectedLanguage = "English";
    [ObservableProperty] private string _updateManifestUrl = string.Empty;
    [ObservableProperty] private UpdateCheckSchedule _selectedUpdateSchedule = UpdateCheckSchedule.Daily;
    [ObservableProperty] private string _lastUpdateCheck = "Never";
    [ObservableProperty] private string _skippedUpdateVersion = string.Empty;
    [ObservableProperty] private string _updateStatusDisplay = UpdateStatus.NotConfigured.ToString();
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _availableUpdateVersion = string.Empty;
    [ObservableProperty] private bool _isTransferBusy;
    [ObservableProperty] private double _transferProgress;
    [ObservableProperty] private string _transferStatus = string.Empty;

    public MainViewModel(IConfigurationStore store, IFileMonitorService monitor, DialogService dialogs, PathLauncher launcher, IEventStream stream, IDeliveryStatusStream deliveryStatuses, IDirectoryHealthStream directoryHealth, IUpdateService updates, IApplicationDataBackupService dataBackup, UpdateInstallerLauncher installerLauncher, IThemeService themeService, IRestartRequirementState restartState, ILogger<MainViewModel> logger, IImportExportService transferService)
    {
        (_store, _monitor, _dialogs, _launcher, _stream, _deliveryStatuses, _directoryHealth, _updates, _dataBackup, _installerLauncher, _themeService, _restartState, _logger, _transferService) = (store, monitor, dialogs, launcher, stream, deliveryStatuses, directoryHealth, updates, dataBackup, installerLauncher, themeService, restartState, logger, transferService);
        EventsView = CollectionViewSource.GetDefaultView(Events); EventsView.Filter = FilterEvent;
        IsStartupEnabled = StartupService.IsEnabled();
        _stream.EventReceived += OnEventReceived;
        _deliveryStatuses.StatusChanged += OnDeliveryStatusChanged;
        _directoryHealth.HealthChanged += OnDirectoryHealthChanged;
        _updates.StateChanged += OnUpdateStateChanged;
        _restartState.Changed += OnRestartRequirementChanged;
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushEvents(), Dispatcher.CurrentDispatcher); _uiTimer.Start();
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await RefreshStatusAsync(), Dispatcher.CurrentDispatcher); _statusTimer.Start();
        _filterTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); EventsView.Refresh(); };
    }

    public async Task LoadAsync()
    {
        var currentHealth = _directoryHealth.Current;
        Directories.Clear(); foreach (var item in await _store.GetDirectoriesAsync()) Directories.Add(new DirectoryRowViewModel(item, currentHealth.GetValueOrDefault(item.Id)));
        Destinations.Clear(); foreach (var item in await _store.GetDestinationsAsync()) Destinations.Add(item);
        if (int.TryParse(await _store.GetSettingAsync("UiBufferLimit"), out var limit)) UiBufferLimit = Math.Clamp(limit, 1_000, 50_000);
        var savedTheme = await _store.GetSettingAsync("ThemeMode");
        if (!Enum.TryParse<AppThemeMode>(savedTheme, true, out var themeMode))
        {
            var legacyTheme = await _store.GetSettingAsync("DarkTheme");
            themeMode = legacyTheme switch { "1" => AppThemeMode.Dark, "0" => AppThemeMode.Light, _ => AppThemeMode.System };
            await _store.SetSettingAsync("ThemeMode", themeMode.ToString());
        }
        SelectedThemeMode = themeMode;
        _themeService.Apply(themeMode);
        OnRestartRequirementChanged(this, EventArgs.Empty);
        SelectedLanguage = string.Equals(await _store.GetSettingAsync("Language") ?? LocalizationService.DefaultLanguage, "vi", StringComparison.OrdinalIgnoreCase) ? "Tiếng Việt" : "English";
        UpdateManifestUrl = await _store.GetSettingAsync(UpdateSettingNames.ManifestUrl) ?? string.Empty;
        var schedule = await _store.GetSettingAsync(UpdateSettingNames.Schedule);
        SelectedUpdateSchedule = Enum.TryParse<UpdateCheckSchedule>(schedule, true, out var parsedSchedule) ? parsedSchedule : UpdateCheckSchedule.Daily;
        await RefreshUpdateMetadataAsync();
        ApplyUpdateState(string.IsNullOrWhiteSpace(UpdateManifestUrl) ? new UpdateState { Status = UpdateStatus.NotConfigured } : _updates.Current);
        await RefreshStatusAsync();
    }

    partial void OnSearchTextChanged(string value) { _filterTimer.Stop(); _filterTimer.Start(); }
    partial void OnSelectedEventFilterChanged(MonitorEventType? value) => EventsView.Refresh();
    partial void OnSelectedEntryFilterChanged(EntryType? value) => EventsView.Refresh();
    partial void OnUiBufferLimitChanged(int value)
    {
        var normalized = Math.Clamp(value, 1_000, 50_000);
        if (normalized != value) { UiBufferLimit = normalized; return; }
        _ = _store.SetSettingAsync("UiBufferLimit", normalized.ToString());
    }
    partial void OnIsStartupEnabledChanged(bool value)
    {
        StartupService.SetEnabled(value);
        _ = _store.SetSettingAsync("StartupConfigured", "1");
    }
    partial void OnSelectedThemeModeChanged(AppThemeMode value)
    {
        _themeService.Apply(value);
        _ = _store.SetSettingAsync("ThemeMode", value.ToString());
    }
    partial void OnSelectedLanguageChanged(string value)
    {
        var code = value == "Tiếng Việt" ? "vi" : "en";
        LocalizationService.Apply(code);
        _ = _store.SetSettingAsync("Language", code);
        OnRestartRequirementChanged(this, EventArgs.Empty);
        ApplyUpdateState(string.IsNullOrWhiteSpace(UpdateManifestUrl) ? new UpdateState { Status = UpdateStatus.NotConfigured } : _updates.Current);
        _ = RefreshUpdateMetadataAsync();
    }
    private bool FilterEvent(object item) => item is EventRowViewModel row && (SelectedEventFilter is null || row.EventType == SelectedEventFilter) && (SelectedEntryFilter is null || row.Value.EntryType == SelectedEntryFilter) && (string.IsNullOrWhiteSpace(SearchText) || row.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || row.OldPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task AddDirectoryAsync()
    {
        var value = _dialogs.EditDirectory(null, Destinations.ToArray()); if (value is null) return;
        if (!ConfirmOverlappingRoot(value)) return;
        await _store.UpsertDirectoryAsync(value); await _monitor.ReloadAsync(); await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private async Task EditDirectoryAsync()
    {
        var value = _dialogs.EditDirectory(SelectedDirectory?.Value, Destinations.ToArray()); if (value is null) return;
        if (!ConfirmOverlappingRoot(value)) return;
        await _store.UpsertDirectoryAsync(value); await _monitor.ReloadAsync(); await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private async Task DeleteDirectoryAsync()
    {
        if (!_dialogs.Confirm($"Delete monitored directory?\n\n{SelectedDirectory!.Path}")) return;
        await _store.SoftDeleteDirectoryAsync(SelectedDirectory.Id); await _monitor.ReloadAsync(); await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private async Task ToggleDirectoryAsync()
    {
        var value = SelectedDirectory!.Value with { IsDisabled = !SelectedDirectory.IsDisabled, LastModificationUtc = DateTimeOffset.UtcNow };
        await _store.UpsertDirectoryAsync(value); await _monitor.ReloadAsync(); await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private async Task DuplicateDirectoryAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var duplicate = SelectedDirectory!.Value with
        {
            Id = Guid.NewGuid(), IsDisabled = true, IsDeleted = false,
            CreationTimeUtc = now, LastModificationUtc = now
        };
        await _store.UpsertDirectoryAsync(duplicate);
        await _monitor.ReloadAsync();
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private void OpenSelectedDirectory() => _launcher.OpenDirectoryStrict(SelectedDirectory!.Path);

    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private void CopySelectedDirectoryPath() => System.Windows.Clipboard.SetText(SelectedDirectory!.Path);

    [RelayCommand]
    private async Task AddOutputAsync()
    {
        var value = _dialogs.EditOutput(null); if (value is null || !ValidateEventLogSingleton(value)) return; await _store.UpsertDestinationAsync(value); await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDestination))]
    private async Task EditOutputAsync()
    {
        var value = _dialogs.EditOutput(SelectedDestination); if (value is null || !ValidateEventLogSingleton(value)) return; await _store.UpsertDestinationAsync(value); await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDestination))]
    private async Task DeleteOutputAsync()
    {
        if (!_dialogs.Confirm($"Delete output '{SelectedDestination!.Name}'?")) return;
        var pending = await _store.GetPendingCountAsync(SelectedDestination.Id);
        if (pending > 0 && !_dialogs.Confirm($"This output still has {pending:N0} pending event(s). Discard those deliveries and delete it?")) return;
        try { await _store.DeleteDestinationAsync(SelectedDestination.Id, pending > 0); await LoadAsync(); } catch (Exception ex) { _logger.LogError(ex, "Failed to delete output {DestinationId}", SelectedDestination.Id); _dialogs.Error(ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanDuplicateDestination))]
    private async Task DuplicateOutputAsync()
    {
        var source = SelectedDestination!;
        var baseName = $"{source.Name} (Copy)";
        var name = baseName;
        for (var suffix = 2; Destinations.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++) name = $"{baseName} {suffix}";
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertDestinationAsync(source with
        {
            Id = Guid.NewGuid(), Name = name, IsDisabled = true, Status = "Disabled", PendingCount = 0, LastError = null,
            CreationTimeUtc = now, LastModificationUtc = now
        });
        await LoadAsync();
    }

    [RelayCommand]
    private void ClearAllEvents()
    {
        if (!_dialogs.Confirm(Localized("ClearEventsConfirmation", "Clear all events from the screen? Output data and pending deliveries will not be changed."))) return;
        lock (_uiQueueGate)
        {
            _incoming = new ConcurrentQueue<MonitorEvent>();
            Events.Clear();
            _eventRows.Clear();
            _pendingStatusRefresh.Clear();
        }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (IsTransferBusy) return;
        var request = _dialogs.ShowExport();
        if (request is null) return;
        var directories = Directories.Select(item => item.Value).ToArray();
        var outputs = Destinations.ToArray();
        var events = EventsView.Cast<EventRowViewModel>().Select(item => item.Value).ToArray();
        await RunTransferAsync(Localized("ExportingData", "Exporting data..."), async (progress, token) =>
        {
            await Task.Run(() => _transferService.ExportAsync(request.FilePath, request.Format, request.Selection, directories, events, outputs, request.Password, progress, token), token);
            _dialogs.Info(Localized("ExportCompleted", "Export completed successfully."));
        });
    }

    [RelayCommand]
    private async Task ImportDataAsync()
    {
        if (IsTransferBusy) return;
        var request = _dialogs.ShowImport();
        if (request is null) return;
        try
        {
            await RunTransferAsync(Localized("ImportingData", "Importing data..."), async (progress, token) =>
            {
                var summary = await Task.Run(() => _transferService.ImportAsync(request.Envelope, request.Selection, progress, token), token);
                await _monitor.ReloadAsync(token);
                await LoadAsync();
                if (request.Selection.Events) DisplayImportedEvents(request.Envelope.Events);
                var message = $"Directories: {summary.Directories.Added} added, {summary.Directories.Skipped} skipped, {summary.Directories.Failed} failed\n" +
                              $"Events: {summary.Events.Added} added, {summary.Events.Skipped} skipped, {summary.Events.Failed} failed\n" +
                              $"Outputs: {summary.Outputs.Added} added, {summary.Outputs.Skipped} skipped, {summary.Outputs.Failed} failed";
                if (summary.Warnings.Count > 0) message += "\n\n" + string.Join("\n", summary.Warnings);
                _dialogs.Info(message);
            });
        }
        finally
        {
            foreach (var output in request.Envelope.Outputs) output.ConnectionString = null;
        }
    }

    [RelayCommand]
    private void CancelTransfer() => _transferCancellation?.Cancel();

    private async Task RunTransferAsync(string status, Func<IProgress<double>, CancellationToken, Task> action)
    {
        _transferCancellation = new CancellationTokenSource();
        IsTransferBusy = true; TransferProgress = 0; TransferStatus = status;
        var progress = new Progress<double>(value => TransferProgress = Math.Clamp(value, 0, 1) * 100);
        try { await action(progress, _transferCancellation.Token); }
        catch (OperationCanceledException) { _dialogs.Info(Localized("TransferCancelled", "The operation was cancelled.")); }
        catch (Exception ex) { _logger.LogError(ex, "Import/export operation failed"); _dialogs.Error(ex.Message); }
        finally { _transferCancellation.Dispose(); _transferCancellation = null; IsTransferBusy = false; TransferStatus = string.Empty; }
    }

    private void DisplayImportedEvents(IEnumerable<TransferEvent> imported)
    {
        var rows = Events.ToList();
        var ids = rows.Select(item => item.Value.Id).ToHashSet();
        foreach (var item in imported)
        {
            if (!ids.Add(item.Id)) continue;
            rows.Add(new EventRowViewModel(new MonitorEvent
            {
                Id = item.Id, SequenceNumber = item.SequenceNumber, EventTimeUtc = item.EventTimeUtc, EventType = item.EventType,
                EntryType = item.EntryType, DirectoryId = item.DirectoryId, RootPath = item.RootPath, Path = item.Path,
                OldPath = item.OldPath, FileSize = item.FileSize, UserName = item.UserName, MachineId = item.MachineId,
                MachineName = item.MachineName, IsRecovered = item.IsRecovered, CreationTimeUtc = item.CreationTimeUtc
            }) { DeliveryStatus = Localized("Imported", "Imported") });
        }
        var ordered = rows.OrderBy(item => item.Value.EventTimeUtc).ThenBy(item => item.Value.MachineId).ThenBy(item => item.Value.SequenceNumber).TakeLast(UiBufferLimit).ToArray();
        Events.ReplaceAll(ordered);
        _eventRows.Clear();
        foreach (var row in ordered) _eventRows[row.Value.Id] = row;
        EventsView.Refresh();
    }

    [RelayCommand]
    private async Task SaveUpdateSettingsAsync() => await SaveUpdateSettingsCoreAsync();

    private async Task<bool> SaveUpdateSettingsCoreAsync()
    {
        var url = UpdateManifestUrl.Trim();
        if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Error(Localized("UpdateUrlHttpsError", "The update URL must be a valid HTTPS address."));
            return false;
        }
        UpdateManifestUrl = url;
        await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, url);
        await _store.SetSettingAsync(UpdateSettingNames.Schedule, SelectedUpdateSchedule.ToString());
        ApplyUpdateState(string.IsNullOrWhiteSpace(url) ? new UpdateState { Status = UpdateStatus.NotConfigured } : new UpdateState { Status = UpdateStatus.Idle });
        return true;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!await SaveUpdateSettingsCoreAsync()) return;
        if (string.IsNullOrWhiteSpace(UpdateManifestUrl))
        {
            _dialogs.Error(Localized("UpdateUrlRequired", "Enter an HTTPS manifest URL first."));
            return;
        }
        var state = await _updates.CheckAsync(true);
        await RefreshUpdateMetadataAsync();
        if (state.Status == UpdateStatus.Error) _dialogs.Error(state.ErrorMessage ?? Localized("UpdateCheckFailed", "Update check failed."));
        else if (state.Status == UpdateStatus.Available) await ShowUpdateAsync();
        else _dialogs.Info(Localized("LatestVersionMessage", "You are using the latest version."));
    }

    [RelayCommand]
    private async Task ShowUpdateAsync()
    {
        var release = _updates.Current.Release;
        if (release is null) return;
        var result = _dialogs.ShowUpdate(release, _updates.CurrentVersion);
        if (result == DirectoryMonitor.Presentation.Views.UpdateDialogResult.Later) return;
        if (result == DirectoryMonitor.Presentation.Views.UpdateDialogResult.SkipVersion)
        {
            await _updates.SkipVersionAsync(release.Version);
            await RefreshUpdateMetadataAsync();
            return;
        }

        var installerPath = _dialogs.DownloadUpdate(_updates, release);
        if (string.IsNullOrWhiteSpace(installerPath)) return;
        try
        {
            var backupPath = Path.Combine(Path.GetDirectoryName(installerPath)!, "directory-monitor-data-backup.db");
            await _dataBackup.CreateBackupAsync(backupPath);
            if (!_installerLauncher.Launch(installerPath, backupPath)) throw new InvalidOperationException(Localized("InstallerLaunchError", "The update installer could not be started."));
            _updates.MarkInstalling();
            if (System.Windows.Application.Current is DirectoryMonitor.Presentation.App app) await app.ExitAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.LogWarning(ex, "Update elevation was cancelled by the user");
            _dialogs.Info(Localized("UacCancelledMessage", "Administrator approval was cancelled. The application will keep running."));
        }
        catch (Exception ex) { _logger.LogError(ex, "Update installation could not be started"); _dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private async Task ClearSkippedUpdateAsync()
    {
        await _updates.ClearSkippedVersionAsync();
        await RefreshUpdateMetadataAsync();
    }

    [RelayCommand] private void OpenPath(string? path) => _launcher.Open(path);
    [RelayCommand] private void OpenContainingFolder(string? path) => _launcher.Open(path, true);
    [RelayCommand] private void OpenDataFolder() => _launcher.Open(Path.GetDirectoryName(DatabasePath));
    [RelayCommand] private void CopyPath(string? path) { if (!string.IsNullOrWhiteSpace(path)) System.Windows.Clipboard.SetText(path); }
    [RelayCommand] private void CopyEvent(EventRowViewModel? row) { if (row is not null) System.Windows.Clipboard.SetText($"{row.EventTypeDisplay}\t{row.EventTime}\t{row.EntryType}\t{row.Path}\t{row.OldPath}\t{row.FileSize}\t{row.DeliveryStatus}"); }
    [RelayCommand(CanExecute = nameof(HasSelectedDestinationError))]
    private void CopyLastError() => System.Windows.Clipboard.SetText(SelectedDestination!.LastError!);

    private bool HasSelectedDirectory() => SelectedDirectory is not null;
    private bool HasSelectedDestination() => SelectedDestination is not null;
    private bool CanDuplicateDestination() => SelectedDestination is not null && SelectedDestination.Type != DestinationType.WindowsEventLog;
    private bool HasSelectedDestinationError() => !string.IsNullOrWhiteSpace(SelectedDestination?.LastError);
    private bool ValidateEventLogSingleton(OutputDestination value)
    {
        if (value.Type != DestinationType.WindowsEventLog || !Destinations.Any(item => item.Type == DestinationType.WindowsEventLog && item.Id != value.Id)) return true;
        _dialogs.Error("Only one Windows Event Log destination is allowed."); return false;
    }
    private bool ConfirmOverlappingRoot(MonitoredDirectory value)
    {
        var other = Directories.FirstOrDefault(item => item.Id != value.Id && (IsBelow(value.Path, item.Path) || IsBelow(item.Path, value.Path)));
        return other is null || _dialogs.Confirm($"This path overlaps another monitored root and may produce duplicate events.\n\n{other.Path}\n\nContinue anyway?");
    }
    private static bool IsBelow(string candidate, string root)
    {
        var normalizedCandidate = System.IO.Path.GetFullPath(candidate).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var normalizedRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
    partial void OnSelectedDirectoryChanged(DirectoryRowViewModel? value) { EditDirectoryCommand.NotifyCanExecuteChanged(); DeleteDirectoryCommand.NotifyCanExecuteChanged(); ToggleDirectoryCommand.NotifyCanExecuteChanged(); DuplicateDirectoryCommand.NotifyCanExecuteChanged(); OpenSelectedDirectoryCommand.NotifyCanExecuteChanged(); CopySelectedDirectoryPathCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedDestinationChanged(OutputDestination? value) { EditOutputCommand.NotifyCanExecuteChanged(); DeleteOutputCommand.NotifyCanExecuteChanged(); DuplicateOutputCommand.NotifyCanExecuteChanged(); CopyLastErrorCommand.NotifyCanExecuteChanged(); }
    private void OnEventReceived(object? sender, MonitorEvent e) { lock (_uiQueueGate) _incoming.Enqueue(e); }
    private void OnRestartRequirementChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsDatabaseRestartRequired = _restartState.IsDatabaseRestartRequired;
            DatabaseRestartMessage = IsDatabaseRestartRequired ? Localized("DatabaseRestartRequired", _restartState.DatabaseRestartMessage ?? string.Empty) : string.Empty;
        });
    }
    private void OnDeliveryStatusChanged(object? sender, IReadOnlyCollection<Guid> eventIds) { foreach (var id in eventIds) _pendingStatusRefresh[id] = 0; }
    private void OnDirectoryHealthChanged(object? sender, DirectoryHealth health)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Directories.FirstOrDefault(item => item.Id == health.DirectoryId)?.Apply(health));
    }
    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () => { ApplyUpdateState(state); await RefreshUpdateMetadataAsync(); });
    }
    private void ApplyUpdateState(UpdateState state)
    {
        UpdateStatusDisplay = state.ErrorMessage ?? Localized($"UpdateStatus{state.Status}", state.Status.ToString());
        IsUpdateAvailable = state.Status == UpdateStatus.Available && state.Release is not null;
        AvailableUpdateVersion = state.Release?.Version.ToString(3) ?? string.Empty;
    }
    private async Task RefreshUpdateMetadataAsync()
    {
        var checkedValue = await _store.GetSettingAsync(UpdateSettingNames.LastSuccessfulCheckUtc);
        LastUpdateCheck = DateTimeOffset.TryParse(checkedValue, out var checkedAt) ? checkedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : Localized("Never", "Never");
        SkippedUpdateVersion = await _store.GetSettingAsync(UpdateSettingNames.SkippedVersion) ?? string.Empty;
    }
    private static string Localized(string key, string fallback) => System.Windows.Application.Current.TryFindResource(key) as string ?? fallback;
    private void FlushEvents()
    {
        var added = new List<EventRowViewModel>(100);
        for (var i = 0; i < 100 && _incoming.TryDequeue(out var e); i++) { var row = new EventRowViewModel(e); added.Add(row); _eventRows[e.Id] = row; _pendingStatusRefresh[e.Id] = 0; }
        var removed = Events.AppendRangeAndTrimStart(added, UiBufferLimit);
        foreach (var row in removed) _eventRows.Remove(row.Value.Id);
        if (!_isRefreshingDeliveryStatuses && !_pendingStatusRefresh.IsEmpty) _ = RefreshDeliveryStatusesAsync();
    }
    private async Task RefreshDeliveryStatusesAsync()
    {
        _isRefreshingDeliveryStatuses = true;
        try
        {
            var ids = _pendingStatusRefresh.Keys.Take(200).ToArray();
            foreach (var id in ids) _pendingStatusRefresh.TryRemove(id, out _);
            var statuses = await _store.GetDeliveryStatusesAsync(ids);
            foreach (var pair in statuses) if (_eventRows.TryGetValue(pair.Key, out var row)) row.DeliveryStatus = pair.Value;
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to refresh event delivery statuses"); }
        finally { _isRefreshingDeliveryStatuses = false; }
    }
    private async Task RefreshStatusAsync()
    {
        if (_isRefreshingStatus) return;
        _isRefreshingStatus = true;
        try
        {
            PendingCount = await _store.GetPendingCountAsync();
            var active = Directories.Count(item => item.Status == DirectoryMonitorStatus.Monitoring);
            StatusText = LocalizationService.IsVietnamese ? $"Đang theo dõi {active} đường dẫn • {PendingCount:N0} đang chờ" : $"Monitoring {active} path(s) • {PendingCount:N0} pending";
            var refreshed = await _store.GetDestinationsAsync(); var selectedId = SelectedDestination?.Id;
            for (var index = 0; index < refreshed.Count && index < Destinations.Count; index++)
                if (Destinations[index].Status != refreshed[index].Status || Destinations[index].PendingCount != refreshed[index].PendingCount || Destinations[index].LastError != refreshed[index].LastError) Destinations[index] = refreshed[index];
            if (selectedId.HasValue) SelectedDestination = Destinations.FirstOrDefault(item => item.Id == selectedId.Value);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to refresh main-window status"); }
        finally { _isRefreshingStatus = false; }
    }
    public void Dispose() { _uiTimer.Stop(); _statusTimer.Stop(); _filterTimer.Stop(); _stream.EventReceived -= OnEventReceived; _deliveryStatuses.StatusChanged -= OnDeliveryStatusChanged; _directoryHealth.HealthChanged -= OnDirectoryHealthChanged; _updates.StateChanged -= OnUpdateStateChanged; _restartState.Changed -= OnRestartRequirementChanged; }
}
