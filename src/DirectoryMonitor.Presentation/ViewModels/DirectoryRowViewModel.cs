using CommunityToolkit.Mvvm.ComponentModel;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Presentation.ViewModels;

public partial class DirectoryRowViewModel : ObservableObject
{
    public DirectoryRowViewModel(MonitoredDirectory value, DirectoryHealth? health)
    {
        Value = value;
        _status = health?.Status ?? (value.IsDisabled ? DirectoryMonitorStatus.Disabled : DirectoryMonitorStatus.Reconnecting);
        _statusMessage = health?.Message;
    }

    public MonitoredDirectory Value { get; }
    public Guid Id => Value.Id;
    public string Path => Value.Path;
    public string Description => Value.Description;
    public bool IsDisabled => Value.IsDisabled;
    public MonitoredEventKinds EventKinds => Value.EventKinds;
    public EntryTypeFilter EntryTypeFilter => Value.EntryTypeFilter;

    [ObservableProperty] private DirectoryMonitorStatus _status;
    [ObservableProperty] private string? _statusMessage;

    public void Apply(DirectoryHealth health)
    {
        Status = health.Status;
        StatusMessage = health.Message;
    }
}
