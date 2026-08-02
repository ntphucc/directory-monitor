using CommunityToolkit.Mvvm.ComponentModel;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Presentation.ViewModels;

public sealed partial class EventRowViewModel : ObservableObject
{
    private readonly MonitorEvent value;
    public EventRowViewModel(MonitorEvent value) { this.value = value; Value = value; }
    public MonitorEvent Value { get; }
    public string EventTime => value.EventTimeUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss.fff");
    public MonitorEventType EventType => value.EventType;
    public string EventTypeDisplay => value.EventType.DisplayName();
    public string EventIcon => value.EventType switch { MonitorEventType.New => "\u002B", MonitorEventType.Change => "\u21BB", MonitorEventType.ModifyProperties => "\u2699", MonitorEventType.Delete => "\u00D7", _ => string.Empty };
    public string EntryType => value.EntryType.ToString();
    public string Path => value.Path;
    public string OldPath => value.OldPath ?? string.Empty;
    public string FileSize => value.FileSize?.ToString("N0") ?? string.Empty;
    public string UserName => value.UserName;
    public string MachineName => value.MachineName;
    [ObservableProperty] private string _deliveryStatus = "Queued";
}
