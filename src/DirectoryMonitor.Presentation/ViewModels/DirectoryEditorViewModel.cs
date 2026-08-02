using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryMonitor.Presentation.ViewModels;

public partial class DirectoryEditorViewModel : ObservableObject
{
    private readonly MonitoredDirectory? _source;
    private readonly ILogger<DirectoryEditorViewModel> _logger;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _monitorNew = true;
    [ObservableProperty] private bool _monitorChange = true;
    [ObservableProperty] private bool _monitorProperties = true;
    [ObservableProperty] private bool _monitorDelete = true;
    [ObservableProperty] private bool _monitorSubdirectories = true;
    [ObservableProperty] private bool _dailyReconciliation = true;
    [ObservableProperty] private EntryTypeFilter _entryTypeFilter;
    [ObservableProperty] private string _includePatterns = string.Empty;
    [ObservableProperty] private string _excludePatterns = string.Empty;
    [ObservableProperty] private string _validationMessage = string.Empty;
    public ObservableCollection<SelectableDestinationViewModel> Outputs { get; } = [];

    public DirectoryEditorViewModel(MonitoredDirectory? source, IReadOnlyList<OutputDestination> destinations, ILogger<DirectoryEditorViewModel>? logger = null)
    {
        _source = source; _logger = logger ?? NullLogger<DirectoryEditorViewModel>.Instance;
        if (source is not null) { Path = source.Path; Description = source.Description; MonitorNew = source.EventKinds.HasFlag(MonitoredEventKinds.New); MonitorChange = source.EventKinds.HasFlag(MonitoredEventKinds.Change); MonitorProperties = source.EventKinds.HasFlag(MonitoredEventKinds.ModifyProperties); MonitorDelete = source.EventKinds.HasFlag(MonitoredEventKinds.Delete); MonitorSubdirectories = source.MonitorSubdirectories; DailyReconciliation = source.EnableDailyReconciliation; EntryTypeFilter = source.EntryTypeFilter; IncludePatterns = string.Join(Environment.NewLine, source.IncludePatterns); ExcludePatterns = string.Join(Environment.NewLine, source.ExcludePatterns); }
        foreach (var destination in destinations) Outputs.Add(new SelectableDestinationViewModel(destination, source?.DestinationIds.Contains(destination.Id) == true));
    }

    public bool Validate()
    {
        var errors = new List<string>(); if (string.IsNullOrWhiteSpace(Path)) errors.Add("Directory path is required.");
        else try { if (!System.IO.Path.IsPathFullyQualified(Path.Trim())) errors.Add("Directory path must be an absolute local or UNC path."); else _ = System.IO.Path.GetFullPath(Path.Trim()); } catch (Exception ex) { _logger.LogDebug(ex, "Directory editor rejected an invalid path"); errors.Add("Directory path is invalid."); }
        if (!MonitorNew && !MonitorChange && !MonitorProperties && !MonitorDelete) errors.Add("Select at least one event type.");
        var invalid = Lines(IncludePatterns).Concat(Lines(ExcludePatterns)).Where(item => !GlobMatcher.IsValid(item)).ToArray(); if (invalid.Length > 0) errors.Add("Invalid glob pattern: " + string.Join(", ", invalid));
        ValidationMessage = string.Join(Environment.NewLine, errors); return errors.Count == 0;
    }

    public MonitoredDirectory Build()
    {
        var events = MonitoredEventKinds.None; if (MonitorNew) events |= MonitoredEventKinds.New; if (MonitorChange) events |= MonitoredEventKinds.Change; if (MonitorProperties) events |= MonitoredEventKinds.ModifyProperties; if (MonitorDelete) events |= MonitoredEventKinds.Delete;
        return new MonitoredDirectory { Id = _source?.Id ?? Guid.NewGuid(), Path = System.IO.Path.GetFullPath(Path.Trim()), Description = Description.Trim(), IsDisabled = _source?.IsDisabled ?? false, IsDeleted = false, MonitorSubdirectories = MonitorSubdirectories, EnableDailyReconciliation = DailyReconciliation, EventKinds = events, EntryTypeFilter = EntryTypeFilter, IncludePatterns = Lines(IncludePatterns), ExcludePatterns = Lines(ExcludePatterns), DestinationIds = Outputs.Where(item => item.IsSelected).Select(item => item.Destination.Id).ToArray(), CreationTimeUtc = _source?.CreationTimeUtc ?? DateTimeOffset.UtcNow, LastModificationUtc = DateTimeOffset.UtcNow };
    }
    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
