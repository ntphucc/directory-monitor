using System.Text;
using System.Text.Json;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryMonitor.Infrastructure.Outputs;

public sealed class TextLogDestination : ILogDestination
{
    private readonly ILogger<TextLogDestination> _logger;
    public TextLogDestination(ILogger<TextLogDestination>? logger = null) => _logger = logger ?? NullLogger<TextLogDestination>.Instance;
    public DestinationType Type => DestinationType.Text;

    public Task EnsureReadyAsync(OutputDestination destination, CancellationToken cancellationToken = default)
    {
        var options = Deserialize(destination); Validate(options); var folder = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(options.FilePath));
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Text destination must include a folder.");
        Directory.CreateDirectory(folder); return Task.CompletedTask;
    }

    public async Task DeliverAsync(OutputDestination destination, IReadOnlyList<MonitorEvent> events, CancellationToken cancellationToken = default)
    {
        var options = Deserialize(destination); Validate(options); var path = Environment.ExpandEnvironmentVariables(options.FilePath);
        var fileLock = TextFileLocks.For(path); await fileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); RotateIfRequired(path, options);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: false);
            foreach (var monitorEvent in events) await writer.WriteLineAsync(TextTemplateRenderer.Render(monitorEvent, options).AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken); stream.Flush(true); DeleteExpired(path, options.RetentionDays);
        }
        finally { fileLock.Release(); }
    }

    public static TextDestinationOptions Deserialize(OutputDestination destination)
        => JsonSerializer.Deserialize<TextDestinationOptions>(destination.ConfigurationJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new TextDestinationOptions();

    private static void Validate(TextDestinationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FilePath)) throw new InvalidOperationException("Text file path is required.");
        var invalid = TextTemplateRenderer.Validate(options.Template); if (invalid.Count > 0) throw new InvalidOperationException("Unknown template token(s): " + string.Join(", ", invalid));
    }

    private static void RotateIfRequired(string path, TextDestinationOptions options)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path); var rotateByDate = info.LastWriteTime.Date < DateTime.Today; var rotateBySize = info.Length >= Math.Max(1, options.RotateSizeMb) * 1024L * 1024L;
        if (!rotateByDate && !rotateBySize) return;
        var folder = info.DirectoryName!; var baseName = Path.GetFileNameWithoutExtension(path); var extension = info.Extension; var index = 0; string rotated;
        do { rotated = Path.Combine(folder, $"{baseName}.{DateTime.Now:yyyyMMdd-HHmmss}.{index++}{extension}"); } while (File.Exists(rotated));
        File.Move(path, rotated);
    }

    private void DeleteExpired(string path, int days)
    {
        if (days <= 0) return; var folder = Path.GetDirectoryName(path)!; var prefix = Path.GetFileNameWithoutExtension(path) + "."; var cutoff = DateTime.UtcNow.AddDays(-days);
        foreach (var file in Directory.EnumerateFiles(folder, prefix + "*")) try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch (Exception ex) { _logger.LogWarning(ex, "Unable to delete expired text log {LogPath}", file); }
    }
}
