using System.Windows;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;

namespace DirectoryMonitor.Presentation.Views;

public partial class UpdateProgressWindow : Window
{
    private readonly IUpdateService _updates;
    private readonly CancellationTokenSource _cancellation = new();
    public string? DownloadedInstallerPath { get; private set; }

    public UpdateProgressWindow(IUpdateService updates, UpdateRelease release)
    {
        InitializeComponent();
        _updates = updates;
        Loaded += async (_, _) =>
        {
            try
            {
                var progress = new Progress<double>(value => DownloadProgress.Value = value * 100);
                DownloadedInstallerPath = await _updates.DownloadInstallerAsync(progress, _cancellation.Token);
                if (IsVisible) DialogResult = true;
            }
            catch (OperationCanceledException) { if (IsVisible) DialogResult = false; }
            catch (Exception ex)
            {
                if (IsVisible)
                {
                    System.Windows.MessageBox.Show(this, ex.Message, "Directory Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
                    DialogResult = false;
                }
            }
            finally { _cancellation.Dispose(); }
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation.Cancel();
    protected override void OnClosed(EventArgs e) { _cancellation.Cancel(); base.OnClosed(e); }
}
