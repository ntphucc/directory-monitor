using CommunityToolkit.Mvvm.ComponentModel;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Presentation.ViewModels;

public partial class SelectableDestinationViewModel(OutputDestination destination, bool isSelected) : ObservableObject
{
    public OutputDestination Destination { get; } = destination;
    public string Name => Destination.Name;
    public string Type => Destination.Type.ToString();
    [ObservableProperty] private bool _isSelected = isSelected;
}
