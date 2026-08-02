using Microsoft.Win32;

namespace DirectoryMonitor.Presentation.Services;

public static class StartupService
{
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue("Directory Monitor") is string value && !string.IsNullOrWhiteSpace(value);
    }

    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue("Directory Monitor", $"\"{Environment.ProcessPath}\" --minimized"); else key.DeleteValue("Directory Monitor", false);
    }
}
