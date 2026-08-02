using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Presentation.Services;

public sealed class PathLauncher(ILogger<PathLauncher> logger)
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi" };
    public void Open(string? path, bool containingFolder = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path)) { Start("explorer.exe", $"\"{path}\""); return; }
            if (File.Exists(path))
            {
                if (containingFolder) { Start("explorer.exe", $"/select,\"{path}\""); return; }
                if (DangerousExtensions.Contains(Path.GetExtension(path)) && System.Windows.MessageBox.Show($"Run this file?\n\n{path}", "Security confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return;
            }
            var parent = Path.GetDirectoryName(path); while (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
            if (!string.IsNullOrWhiteSpace(parent)) Start("explorer.exe", $"\"{parent}\""); else throw new DirectoryNotFoundException(path);
        }
        catch (Exception ex) { logger.LogError(ex, "Unable to open path {Path}", path); System.Windows.MessageBox.Show(ex.Message, "Cannot open path", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    public void OpenDirectoryStrict(string path)
    {
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(string.Format(System.Windows.Application.Current?.TryFindResource("DirectoryMissingMessage") as string ?? "The directory no longer exists or is unavailable: {0}", path));
            Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to open monitored directory {Path}", path);
            System.Windows.MessageBox.Show(ex.Message, "Cannot open directory", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private static void Start(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
}
