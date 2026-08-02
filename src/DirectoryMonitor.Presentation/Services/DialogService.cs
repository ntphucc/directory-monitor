using System.Windows;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Presentation.ViewModels;
using DirectoryMonitor.Presentation.Views;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Presentation.Services;

public sealed class DialogService(ILogDestinationFactory factory, IImportExportService transferService, ILoggerFactory loggerFactory)
{
    public MonitoredDirectory? EditDirectory(MonitoredDirectory? source, IReadOnlyList<OutputDestination> destinations)
    {
        var vm = new DirectoryEditorViewModel(source, destinations, loggerFactory.CreateLogger<DirectoryEditorViewModel>()); var window = new DirectoryEditorWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? vm.Build() : null;
    }
    public OutputDestination? EditOutput(OutputDestination? source)
    {
        var vm = new OutputEditorViewModel(source, factory, loggerFactory.CreateLogger<OutputEditorViewModel>()); var window = new OutputEditorWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? vm.Build() : null;
    }
    public UpdateDialogResult ShowUpdate(UpdateRelease release, Version currentVersion)
    {
        var window = new UpdateDialogWindow(release, currentVersion) { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
        return window.Result;
    }
    public string? DownloadUpdate(IUpdateService updateService, UpdateRelease release)
    {
        var window = new UpdateProgressWindow(updateService, release) { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.DownloadedInstallerPath : null;
    }
    public ExportDialogResult? ShowExport()
    {
        var window = new ExportDialogWindow { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }
    public ImportDialogResult? ShowImport()
    {
        var window = new ImportDialogWindow(transferService) { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }
    public bool Confirm(string message) => System.Windows.MessageBox.Show(message, "Directory Monitor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public void Info(string message) => System.Windows.MessageBox.Show(message, "Directory Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string message) => System.Windows.MessageBox.Show(message, "Directory Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
}
