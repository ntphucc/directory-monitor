using System.Windows;
using System.Windows.Controls;
using DirectoryMonitor.Domain;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace DirectoryMonitor.Presentation.Views;

public sealed record ExportDialogResult(string FilePath, TransferFormat Format, TransferSelection Selection, string? Password);

public partial class ExportDialogWindow : Window
{
    public ExportDialogResult? Result { get; private set; }
    public ExportDialogWindow() => InitializeComponent();
    protected override void OnClosed(EventArgs e) { PasswordBox.Clear(); ConfirmPasswordBox.Clear(); base.OnClosed(e); }

    private TransferFormat SelectedFormat => Enum.Parse<TransferFormat>(((ComboBoxItem)FormatBox.SelectedItem).Tag!.ToString()!);
    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PasswordPanel is not null) PasswordPanel.Visibility = SelectedFormat == TransferFormat.Dm ? Visibility.Visible : Visibility.Collapsed; }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var extension = SelectedFormat switch { TransferFormat.Json => ".json", TransferFormat.Xml => ".xml", _ => ".dm" };
        var filter = SelectedFormat switch { TransferFormat.Json => "JSON (*.json)|*.json", TransferFormat.Xml => "XML (*.xml)|*.xml", _ => "Directory Monitor (*.dm)|*.dm" };
        var dialog = new SaveFileDialog { AddExtension = true, DefaultExt = extension, Filter = filter, FileName = $"directory-monitor-export-{DateTime.Now:yyyyMMdd-HHmmss}{extension}" };
        if (dialog.ShowDialog(this) == true) FilePathBox.Text = dialog.FileName;
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var selection = new TransferSelection { Directories = DirectoriesBox.IsChecked == true, Events = EventsBox.IsChecked == true, Outputs = OutputsBox.IsChecked == true };
        if (!selection.Any) { MessageBox.Show((string)FindResource("SelectCategoryMessage"), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (string.IsNullOrWhiteSpace(FilePathBox.Text)) { MessageBox.Show((string)FindResource("ChooseExportFileMessage"), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string? password = null;
        if (SelectedFormat == TransferFormat.Dm)
        {
            if (string.IsNullOrEmpty(PasswordBox.Password) || PasswordBox.Password != ConfirmPasswordBox.Password) { MessageBox.Show((string)FindResource("PasswordsDoNotMatch"), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            password = PasswordBox.Password;
        }
        Result = new ExportDialogResult(FilePathBox.Text, SelectedFormat, selection, password); DialogResult = true;
    }
}
