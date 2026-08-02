using System.Windows;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DirectoryMonitor.Presentation.Views;

public sealed record ImportDialogResult(TransferEnvelopeV1 Envelope, TransferSelection Selection);

public partial class ImportDialogWindow : Window
{
    private readonly IImportExportService _service;
    private TransferEnvelopeV1? _envelope;
    public ImportDialogResult? Result { get; private set; }
    public ImportDialogWindow(IImportExportService service) { _service = service; InitializeComponent(); }
    protected override void OnClosed(EventArgs e)
    {
        PasswordBox.Clear();
        if (Result is null && _envelope is not null) foreach (var output in _envelope.Outputs) output.ConnectionString = null;
        base.OnClosed(e);
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Directory Monitor data (*.json;*.xml;*.dm)|*.json;*.xml;*.dm|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) { FilePathBox.Text = dialog.FileName; _envelope = null; ImportButton.IsEnabled = false; StatusText.Text = string.Empty; }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FilePathBox.Text)) return;
        try
        {
            IsEnabled = false;
            _envelope = await _service.ReadAsync(FilePathBox.Text, PasswordBox.Password);
            DirectoriesBox.Content = $"{FindResource("Directories")} ({_envelope.Directories.Count:N0})"; DirectoriesBox.IsEnabled = _envelope.Directories.Count > 0; DirectoriesBox.IsChecked = DirectoriesBox.IsEnabled;
            EventsBox.Content = $"{FindResource("Events")} ({_envelope.Events.Count:N0})"; EventsBox.IsEnabled = _envelope.Events.Count > 0; EventsBox.IsChecked = EventsBox.IsEnabled;
            OutputsBox.Content = $"{FindResource("Outputs")} ({_envelope.Outputs.Count:N0})"; OutputsBox.IsEnabled = _envelope.Outputs.Count > 0; OutputsBox.IsChecked = OutputsBox.IsEnabled;
            StatusText.Text = $"Schema {_envelope.SchemaVersion} • {_envelope.AppVersion} • {_envelope.SourceMachineName} • {_envelope.ExportedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
            ImportButton.IsEnabled = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_envelope is null) return;
        var selection = new TransferSelection { Directories = DirectoriesBox.IsChecked == true, Events = EventsBox.IsChecked == true, Outputs = OutputsBox.IsChecked == true };
        if (!selection.Any) { MessageBox.Show((string)FindResource("SelectCategoryMessage"), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Result = new ImportDialogResult(_envelope, selection); DialogResult = true;
    }
}
