using System.Windows;
using System.IO;
using DirectoryMonitor.Presentation.ViewModels;
using Forms = System.Windows.Forms;

namespace DirectoryMonitor.Presentation.Views;

public partial class DirectoryEditorWindow : Window
{
    private readonly DirectoryEditorViewModel _viewModel;
    public DirectoryEditorWindow(DirectoryEditorViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; }
    private void Browse_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.FolderBrowserDialog { Description = "Select a directory to monitor", UseDescriptionForTitle = true, ShowNewFolderButton = true, InitialDirectory = Directory.Exists(_viewModel.Path) ? _viewModel.Path : string.Empty }; if (dialog.ShowDialog() == Forms.DialogResult.OK) _viewModel.Path = dialog.SelectedPath; }
    private void Save_Click(object sender, RoutedEventArgs e) { if (!_viewModel.Validate()) return; DialogResult = true; }
}
