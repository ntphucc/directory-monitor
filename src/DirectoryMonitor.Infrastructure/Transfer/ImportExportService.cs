using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure.Transfer;

public sealed class ImportExportService(IConfigurationStore store, ILogger<ImportExportService> logger) : IImportExportService
{
    private static readonly byte[] Magic = "DIRMONDM"u8.ToArray();
    private const byte DmVersion = 1;
    private const int Pbkdf2Iterations = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task ExportAsync(string destinationPath, TransferFormat format, TransferSelection selection,
        IReadOnlyCollection<MonitoredDirectory> directories, IReadOnlyCollection<MonitorEvent> events,
        IReadOnlyCollection<OutputDestination> outputs, string? password, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!selection.Any) throw new InvalidOperationException("Select at least one category to export.");
        if (format == TransferFormat.Dm && string.IsNullOrEmpty(password)) throw new InvalidOperationException("A password is required for .dm export.");
        var temporaryPath = destinationPath + ".part";
        try
        {
            progress?.Report(0.05);
            var envelope = await BuildEnvelopeAsync(format, selection, directories, events, outputs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.45);
            var payload = format == TransferFormat.Xml ? SerializeXml(envelope) : JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            foreach (var output in envelope.Outputs) output.ConnectionString = null;
            byte[] bytes;
            if (format == TransferFormat.Dm)
            {
                try { bytes = EncryptDm(payload, password!); }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }
            else bytes = payload;
            var folder = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? throw new InvalidOperationException("The export folder is invalid.");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            progress?.Report(0.9);
            File.Move(temporaryPath, destinationPath, true);
            progress?.Report(1);
            logger.LogInformation("Exported Directory Monitor data to {ExportPath} in {Format} format", destinationPath, format);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception cleanupException) { logger.LogWarning(cleanupException, "Could not remove incomplete export file {TemporaryPath}", temporaryPath); }
            logger.LogError(ex, "Directory Monitor export failed for {ExportPath}", destinationPath);
            throw;
        }
    }

    public async Task<TransferEnvelopeV1> ReadAsync(string sourcePath, string? password, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            TransferEnvelopeV1? envelope;
            if (IsDm(bytes))
            {
                if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("This .dm file requires a password.");
                var json = DecryptDm(bytes, password);
                try { envelope = JsonSerializer.Deserialize<TransferEnvelopeV1>(json, JsonOptions); }
                finally { CryptographicOperations.ZeroMemory(json); }
            }
            else if (FirstNonWhitespace(bytes) == (byte)'<') envelope = DeserializeXml(bytes);
            else envelope = JsonSerializer.Deserialize<TransferEnvelopeV1>(bytes, JsonOptions);
            if (envelope is null || envelope.SchemaVersion != 1) throw new InvalidDataException("The transfer file schema is not supported.");
            return envelope;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Directory Monitor import file could not be read from {ImportPath}", sourcePath);
            throw;
        }
    }

    public async Task<ImportSummary> ImportAsync(TransferEnvelopeV1 envelope, TransferSelection selection, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var existingOutputs = await store.GetDestinationsAsync(cancellationToken);
        var outputIds = existingOutputs.Select(item => item.Id).ToHashSet();
        var hasWindowsEventLog = existingOutputs.Any(item => item.Type == DestinationType.WindowsEventLog);
        var outputAdded = 0; var outputSkipped = 0; var outputFailed = 0;
        if (selection.Outputs)
        {
            foreach (var item in envelope.Outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outputIds.Contains(item.Id) || item.Type == DestinationType.WindowsEventLog && hasWindowsEventLog) { outputSkipped++; continue; }
                try
                {
                    var configuration = item.ConfigurationJson;
                    if (item.Type is DestinationType.PostgreSql or DestinationType.SqlServer)
                    {
                        try
                        {
                            configuration = JsonSerializer.Serialize(new DatabaseDestinationOptions
                            {
                                ProtectedConnectionString = string.IsNullOrEmpty(item.ConnectionString) ? string.Empty : SecretProtector.Protect(item.ConnectionString)
                            }, JsonOptions);
                        }
                        finally { item.ConnectionString = null; }
                    }
                    await store.UpsertDestinationAsync(new OutputDestination
                    {
                        Id = item.Id, Name = item.Name, Type = item.Type, IsDisabled = true, ConfigurationJson = configuration,
                        Status = "Disabled", CreationTimeUtc = item.CreationTimeUtc, LastModificationUtc = DateTimeOffset.UtcNow
                    }, cancellationToken);
                    outputIds.Add(item.Id); if (item.Type == DestinationType.WindowsEventLog) hasWindowsEventLog = true; outputAdded++;
                }
                catch (Exception ex) { outputFailed++; logger.LogError(ex, "Failed to import output {OutputId}", item.Id); }
            }
        }
        progress?.Report(0.35);

        var existingDirectoryIds = (await store.GetDirectoriesAsync(true, cancellationToken)).Select(item => item.Id).ToHashSet();
        var directoryAdded = 0; var directorySkipped = 0; var directoryFailed = 0;
        if (selection.Directories)
        {
            foreach (var item in envelope.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingDirectoryIds.Contains(item.Id)) { directorySkipped++; continue; }
                try
                {
                    var validDestinations = item.DestinationIds.Where(outputIds.Contains).ToArray();
                    if (validDestinations.Length != item.DestinationIds.Length) warnings.Add($"Directory '{item.Path}' referenced output destinations that do not exist; those references were removed.");
                    await store.UpsertDirectoryAsync(new MonitoredDirectory
                    {
                        Id = item.Id, Path = item.Path, Description = item.Description, IsDisabled = true,
                        MonitorSubdirectories = item.MonitorSubdirectories, EnableDailyReconciliation = item.EnableDailyReconciliation,
                        EventKinds = item.EventKinds, EntryTypeFilter = item.EntryTypeFilter, IncludePatterns = item.IncludePatterns,
                        ExcludePatterns = item.ExcludePatterns, DestinationIds = validDestinations,
                        CreationTimeUtc = item.CreationTimeUtc, LastModificationUtc = DateTimeOffset.UtcNow
                    }, cancellationToken);
                    existingDirectoryIds.Add(item.Id); directoryAdded++;
                }
                catch (Exception ex) { directoryFailed++; logger.LogError(ex, "Failed to import directory {DirectoryId}", item.Id); }
            }
        }
        progress?.Report(0.7);

        var importedEvents = selection.Events ? envelope.Events.Select(ToDomain).ToArray() : [];
        var eventAdded = await store.ImportEventsAsync(importedEvents, cancellationToken);
        var eventSkipped = importedEvents.Length - eventAdded;
        progress?.Report(1);
        logger.LogInformation("Imported Directory Monitor data: {Directories} directories, {Events} events and {Outputs} outputs added", directoryAdded, eventAdded, outputAdded);
        return new ImportSummary
        {
            Directories = new(directoryAdded, directorySkipped, directoryFailed),
            Events = new(eventAdded, eventSkipped, 0),
            Outputs = new(outputAdded, outputSkipped, outputFailed),
            Warnings = warnings
        };
    }

    private async Task<TransferEnvelopeV1> BuildEnvelopeAsync(TransferFormat format, TransferSelection selection,
        IReadOnlyCollection<MonitoredDirectory> directories, IReadOnlyCollection<MonitorEvent> events,
        IReadOnlyCollection<OutputDestination> outputs, CancellationToken cancellationToken)
    {
        var envelope = new TransferEnvelopeV1
        {
            AppVersion = typeof(ImportExportService).Assembly.GetName().Version?.ToString(3) ?? "1.1.8",
            ExportedAtUtc = DateTimeOffset.UtcNow,
            SourceMachineId = await store.GetMachineIdAsync(cancellationToken),
            SourceMachineName = Environment.MachineName
        };
        if (selection.Directories) envelope.Directories = directories.Select(ToTransfer).ToList();
        if (selection.Events) envelope.Events = events.Select(ToTransfer).ToList();
        if (selection.Outputs)
        {
            foreach (var output in outputs)
            {
                var transferred = new TransferOutput
                {
                    Id = output.Id, Name = output.Name, Type = output.Type, IsDisabled = output.IsDisabled,
                    ConfigurationJson = output.ConfigurationJson, CreationTimeUtc = output.CreationTimeUtc,
                    LastModificationUtc = output.LastModificationUtc
                };
                if (output.Type is DestinationType.PostgreSql or DestinationType.SqlServer)
                {
                    transferred.RequiresCredentials = format != TransferFormat.Dm;
                    transferred.ConfigurationJson = "{}";
                    if (format == TransferFormat.Dm)
                    {
                        var options = JsonSerializer.Deserialize<DatabaseDestinationOptions>(output.ConfigurationJson, JsonOptions);
                        transferred.ConnectionString = string.IsNullOrWhiteSpace(options?.ProtectedConnectionString) ? null : SecretProtector.Unprotect(options.ProtectedConnectionString);
                    }
                }
                envelope.Outputs.Add(transferred);
            }
        }
        return envelope;
    }

    private static byte[] SerializeXml(TransferEnvelopeV1 envelope)
    {
        var serializer = new XmlSerializer(typeof(TransferEnvelopeV1));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, envelope);
        return stream.ToArray();
    }

    private static TransferEnvelopeV1? DeserializeXml(byte[] bytes)
    {
        var serializer = new XmlSerializer(typeof(TransferEnvelopeV1));
        using var stream = new MemoryStream(bytes);
        return serializer.Deserialize(stream) as TransferEnvelopeV1;
    }

    private static byte[] EncryptDm(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ciphertext = new byte[plaintext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic); }
        finally { CryptographicOperations.ZeroMemory(key); }
        using var result = new MemoryStream(); result.Write(Magic); result.WriteByte(DmVersion); result.Write(salt); result.Write(nonce); result.Write(tag); result.Write(ciphertext); return result.ToArray();
    }

    private static byte[] DecryptDm(byte[] payload, string password)
    {
        const int headerLength = 8 + 1 + 16 + 12 + 16;
        if (payload.Length < headerLength || !IsDm(payload) || payload[8] != DmVersion) throw new InvalidDataException("The .dm file header is invalid or unsupported.");
        var salt = payload.AsSpan(9, 16); var nonce = payload.AsSpan(25, 12); var tag = payload.AsSpan(37, 16); var ciphertext = payload.AsSpan(headerLength); var plaintext = new byte[ciphertext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic); return plaintext; }
        catch (CryptographicException ex) { throw new InvalidDataException("The password is incorrect or the .dm file has been modified.", ex); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static bool IsDm(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);
    private static byte FirstNonWhitespace(byte[] bytes)
    {
        var index = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        while (index < bytes.Length && char.IsWhiteSpace((char)bytes[index])) index++;
        return index < bytes.Length ? bytes[index] : (byte)0;
    }
    private static TransferDirectory ToTransfer(MonitoredDirectory value) => new() { Id = value.Id, Path = value.Path, Description = value.Description, IsDisabled = value.IsDisabled, MonitorSubdirectories = value.MonitorSubdirectories, EnableDailyReconciliation = value.EnableDailyReconciliation, EventKinds = value.EventKinds, EntryTypeFilter = value.EntryTypeFilter, IncludePatterns = value.IncludePatterns.ToArray(), ExcludePatterns = value.ExcludePatterns.ToArray(), DestinationIds = value.DestinationIds.ToArray(), CreationTimeUtc = value.CreationTimeUtc, LastModificationUtc = value.LastModificationUtc };
    private static TransferEvent ToTransfer(MonitorEvent value) => new() { Id = value.Id, SequenceNumber = value.SequenceNumber, EventTimeUtc = value.EventTimeUtc, EventType = value.EventType, EntryType = value.EntryType, DirectoryId = value.DirectoryId, RootPath = value.RootPath, Path = value.Path, OldPath = value.OldPath, FileSize = value.FileSize, UserName = value.UserName, MachineId = value.MachineId, MachineName = value.MachineName, IsRecovered = value.IsRecovered, CreationTimeUtc = value.CreationTimeUtc };
    private static MonitorEvent ToDomain(TransferEvent value) => new() { Id = value.Id, SequenceNumber = value.SequenceNumber, EventTimeUtc = value.EventTimeUtc, EventType = value.EventType, EntryType = value.EntryType, DirectoryId = value.DirectoryId, RootPath = value.RootPath, Path = value.Path, OldPath = value.OldPath, FileSize = value.FileSize, UserName = value.UserName, MachineId = value.MachineId, MachineName = value.MachineName, IsRecovered = value.IsRecovered, CreationTimeUtc = value.CreationTimeUtc };
}
