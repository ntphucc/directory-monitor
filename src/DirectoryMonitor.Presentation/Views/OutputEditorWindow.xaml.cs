using System.Windows;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Presentation.ViewModels;
using Microsoft.Win32;

namespace DirectoryMonitor.Presentation.Views;

public partial class OutputEditorWindow : Window
{
    private readonly OutputEditorViewModel _viewModel;
    public OutputEditorWindow(OutputEditorViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; }
    private void BrowseFile_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Select text log file", Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*", FileName = string.IsNullOrWhiteSpace(_viewModel.FilePath) ? "directory-monitor.log" : System.IO.Path.GetFileName(_viewModel.FilePath) }; if (dialog.ShowDialog(this) == true) _viewModel.FilePath = dialog.FileName; }
    private async void Test_Click(object sender, RoutedEventArgs e) => await _viewModel.TestAsync();
    private void UseSample_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.ConnectionString) && !string.Equals(_viewModel.ConnectionString, _viewModel.DatabaseConnectionExample, StringComparison.Ordinal))
        {
            var prompt = FindResource("ReplaceConnectionStringPrompt")?.ToString() ?? "Replace the current connection string with the selected sample?";
            if (System.Windows.MessageBox.Show(this, prompt, "Directory Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        _viewModel.ApplyDatabaseConnectionExample();
    }
    private async void Save_Click(object sender, RoutedEventArgs e) { if (!_viewModel.Validate()) return; if (_viewModel.Type is DestinationType.PostgreSql or DestinationType.SqlServer && !await _viewModel.TestAsync()) return; DialogResult = true; }
}
