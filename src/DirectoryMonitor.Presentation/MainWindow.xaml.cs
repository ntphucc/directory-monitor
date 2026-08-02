using System.ComponentModel;
using System.Collections.Specialized;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DirectoryMonitor.Presentation.ViewModels;

namespace DirectoryMonitor.Presentation;

public partial class MainWindow : Window
{
    public event EventHandler? RequestCloseToTray;
    public bool AllowClose { get; set; }
    private readonly MainViewModel _viewModel;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); _viewModel = viewModel; DataContext = viewModel;
        _viewModel.Events.CollectionChanged += Events_CollectionChanged;
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); Title = admin ? "Directory Monitor (Administrator)" : "Directory Monitor";
    }
    protected override void OnClosed(EventArgs e) { _viewModel.Events.CollectionChanged -= Events_CollectionChanged; base.OnClosed(e); }
    protected override void OnClosing(CancelEventArgs e) { if (!AllowClose) { e.Cancel = true; RequestCloseToTray?.Invoke(this, EventArgs.Empty); } base.OnClosing(e); }
    private void Events_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.Events.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToLatestVisibleEvent));
    }
    private void ScrollToLatestVisibleEvent()
    {
        if (EventGrid.Items.Count == 0) return;
        EventGrid.ScrollIntoView(EventGrid.Items[EventGrid.Items.Count - 1]);
    }
    private void ClearFilter_Click(object sender, RoutedEventArgs e) { _viewModel.SelectedEventFilter = null; _viewModel.SelectedEntryFilter = null; _viewModel.SearchText = string.Empty; }
    private EventRowViewModel? Selected() => EventGrid.SelectedItem as EventRowViewModel;
    private void OpenSelected_Click(object sender, RoutedEventArgs e) { var row = Selected(); if (row is not null) _viewModel.OpenPathCommand.Execute(row.Path); }
    private void OpenContainingSelected_Click(object sender, RoutedEventArgs e) { var row = Selected(); if (row is not null) _viewModel.OpenContainingFolderCommand.Execute(row.Path); }
    private void CopySelectedPath_Click(object sender, RoutedEventArgs e) { var row = Selected(); if (row is not null) _viewModel.CopyPathCommand.Execute(row.Path); }
    private void CopySelectedEvent_Click(object sender, RoutedEventArgs e) { var row = Selected(); if (row is not null) _viewModel.CopyEventCommand.Execute(row); }
    private static DataGridRow? FindRow(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        return current as DataGridRow;
    }
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && FindRow(e.OriginalSource) is { } row)
        {
            row.IsSelected = true;
            grid.SelectedItem = row.Item;
            row.Focus();
        }
    }
    private void DirectoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && FindRow(e.OriginalSource) is not null && _viewModel.EditDirectoryCommand.CanExecute(null)) _viewModel.EditDirectoryCommand.Execute(null);
    }
    private void OutputGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && FindRow(e.OriginalSource) is not null && _viewModel.EditOutputCommand.CanExecute(null)) _viewModel.EditOutputCommand.Execute(null);
    }
    private async void ExitApplication_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) await app.RequestExitAsync();
    }
}
