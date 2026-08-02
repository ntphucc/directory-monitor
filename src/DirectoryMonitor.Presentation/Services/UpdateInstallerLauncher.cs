using System.Diagnostics;
using System.IO;

namespace DirectoryMonitor.Presentation.Services;

public sealed class UpdateInstallerLauncher
{
    public bool Launch(string installerPath, string backupPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in BuildArguments(backupPath)) info.ArgumentList.Add(argument);
        return Process.Start(info) is not null;
    }

    public static IReadOnlyList<string> BuildArguments(string backupPath) =>
    [
        "/SILENT",
        "/SUPPRESSMSGBOXES",
        "/CLOSEAPPLICATIONS",
        "/RELAUNCH=1",
        $"/IMPORTDB={Path.GetFullPath(backupPath)}"
    ];
}
