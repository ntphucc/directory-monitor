using System.Windows;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Presentation.Services;

namespace DirectoryMonitor.Presentation.Views;

public enum UpdateDialogResult { Later, UpdateNow, SkipVersion }

public partial class UpdateDialogWindow : Window
{
    public UpdateDialogResult Result { get; private set; } = UpdateDialogResult.Later;

    public UpdateDialogWindow(UpdateRelease release, Version currentVersion)
    {
        InitializeComponent();
        var preferred = LocalizationService.IsVietnamese ? release.ReleaseNotesVietnamese : release.ReleaseNotesEnglish;
        var fallback = LocalizationService.IsVietnamese ? release.ReleaseNotesEnglish : release.ReleaseNotesVietnamese;
        var notes = preferred.Count > 0 ? preferred : fallback.Count > 0 ? fallback : [FindResource("NoReleaseNotes")?.ToString() ?? "No release notes provided."];
        DataContext = new { CurrentVersion = currentVersion.ToString(3), NewVersion = release.Version.ToString(3), PublishedAt = release.PublishedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), ReleaseNotes = notes };
    }

    private void Update_Click(object sender, RoutedEventArgs e) { Result = UpdateDialogResult.UpdateNow; DialogResult = true; }
    private void Skip_Click(object sender, RoutedEventArgs e) { Result = UpdateDialogResult.SkipVersion; DialogResult = true; }
    private void Later_Click(object sender, RoutedEventArgs e) { Result = UpdateDialogResult.Later; DialogResult = false; }
}
