using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure.Updates;

public sealed class HttpsUpdateService : IUpdateService
{
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxInstallerBytes = 1024L * 1024 * 1024;
    private static readonly Regex StableVersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(@"^[A-Fa-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IConfigurationStore _store;
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpsUpdateService> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly string _downloadRoot;

    public HttpsUpdateService(IConfigurationStore store, HttpClient httpClient, ILogger<HttpsUpdateService> logger)
        : this(store, httpClient, logger, null)
    {
    }

    public HttpsUpdateService(IConfigurationStore store, HttpClient httpClient, ILogger<HttpsUpdateService> logger, Version? currentVersion)
    {
        _store = store;
        _httpClient = httpClient;
        _logger = logger;
        CurrentVersion = currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(HttpsUpdateService).Assembly.GetName().Version ?? new Version(1, 1, 0);
        _downloadRoot = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_UPDATE_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Directory Monitor", "Updates");
    }

    public event EventHandler<UpdateState>? StateChanged;
    public UpdateState Current { get; private set; } = new() { Status = UpdateStatus.Idle };
    public Version CurrentVersion { get; }

    public async Task<UpdateState> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!await _operationLock.WaitAsync(0, cancellationToken)) return Current;
        try
        {
            var configuredUrl = (await _store.GetSettingAsync(UpdateSettingNames.ManifestUrl, cancellationToken))?.Trim();
            if (string.IsNullOrWhiteSpace(configuredUrl)) return Publish(new UpdateState { Status = UpdateStatus.NotConfigured });
            var now = DateTimeOffset.UtcNow;
            await _store.SetSettingAsync(UpdateSettingNames.LastAttemptUtc, now.ToString("O"), cancellationToken);
            var manifestUri = RequireHttpsUri(configuredUrl, "Update manifest URL");
            Publish(new UpdateState { Status = UpdateStatus.Checking });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            RequireHttpsUri(response.RequestMessage?.RequestUri, "Final update manifest URL");
            if (response.Content.Headers.ContentLength is > MaxManifestBytes) throw new InvalidDataException("The update manifest exceeds 1 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var payload = await ReadLimitedAsync(stream, MaxManifestBytes, timeout.Token);
            var release = ParseManifest(payload);
            await _store.SetSettingAsync(UpdateSettingNames.LastSuccessfulCheckUtc, now.ToString("O"), cancellationToken);

            if (release.Version <= CurrentVersion)
                return Publish(new UpdateState { Status = UpdateStatus.UpToDate });

            var skipped = await _store.GetSettingAsync(UpdateSettingNames.SkippedVersion, cancellationToken);
            if (Version.TryParse(skipped, out var skippedVersion) && skippedVersion == release.Version)
                return Publish(new UpdateState { Status = UpdateStatus.UpToDate });

            return Publish(new UpdateState { Status = UpdateStatus.Available, Release = release });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Publish(new UpdateState { Status = UpdateStatus.Error, ErrorMessage = "The update check timed out." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return Publish(new UpdateState { Status = UpdateStatus.Error, ErrorMessage = ex.Message });
        }
        finally { _operationLock.Release(); }
    }

    public async Task<string> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var release = Current.Release ?? throw new InvalidOperationException("No update is available to download.");
        var versionFolder = Path.Combine(_downloadRoot, release.Version.ToString(3));
        var finalPath = Path.Combine(versionFolder, $"DirectoryMonitor-Setup-{release.Version.ToString(3)}-x64.exe");
        var partialPath = finalPath + ".part";
        try
        {
            Directory.CreateDirectory(versionFolder);
            if (File.Exists(partialPath)) File.Delete(partialPath);
            Publish(new UpdateState { Status = UpdateStatus.Downloading, Release = release });
            using var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            RequireHttpsUri(response.RequestMessage?.RequestUri, "Final installer URL");
            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != release.InstallerSizeBytes)
                throw new InvalidDataException("The installer size does not match the update manifest.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            var lastPublishedPercent = -1;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > release.InstallerSizeBytes || total > MaxInstallerBytes) throw new InvalidDataException("The installer is larger than declared.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                var value = Math.Clamp((double)total / release.InstallerSizeBytes, 0, 1);
                progress?.Report(value);
                var percent = (int)(value * 100);
                if (percent != lastPublishedPercent) { lastPublishedPercent = percent; Publish(new UpdateState { Status = UpdateStatus.Downloading, Release = release, Progress = value }); }
            }
            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            if (total != release.InstallerSizeBytes) throw new InvalidDataException("The installer size does not match the update manifest.");
            var expectedHash = Convert.FromHexString(release.InstallerSha256);
            var actualHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) throw new InvalidDataException("The installer SHA-256 does not match the update manifest.");
            File.Move(partialPath, finalPath, true);
            Publish(new UpdateState { Status = UpdateStatus.Available, Release = release, Progress = 1 });
            CleanupOldDownloads(versionFolder);
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            Publish(new UpdateState { Status = UpdateStatus.Available, Release = release });
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download failed");
            Publish(new UpdateState { Status = UpdateStatus.Error, Release = release, ErrorMessage = ex.Message });
            throw;
        }
        finally
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch (Exception ex) { _logger.LogWarning(ex, "Unable to remove partial update file {PartialPath}", partialPath); }
            _operationLock.Release();
        }
    }

    public async Task SkipVersionAsync(Version version, CancellationToken cancellationToken = default)
    {
        await _store.SetSettingAsync(UpdateSettingNames.SkippedVersion, version.ToString(3), cancellationToken);
        Publish(new UpdateState { Status = UpdateStatus.UpToDate });
    }

    public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default)
        => _store.SetSettingAsync(UpdateSettingNames.SkippedVersion, string.Empty, cancellationToken);

    public void MarkInstalling()
        => Publish(new UpdateState { Status = UpdateStatus.Installing, Release = Current.Release, Progress = 1 });

    private UpdateState Publish(UpdateState state)
    {
        Current = state with { ChangedAtUtc = DateTimeOffset.UtcNow };
        StateChanged?.Invoke(this, Current);
        return Current;
    }

    private static UpdateRelease ParseManifest(byte[] payload)
    {
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF) payload = payload[3..];
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported update manifest schema.");
        var versionText = root.GetProperty("version").GetString();
        if (versionText is null || !StableVersionPattern.IsMatch(versionText) || !Version.TryParse(versionText, out var version)) throw new InvalidDataException("The update version must use major.minor.patch.");
        if (!root.GetProperty("publishedAtUtc").TryGetDateTimeOffset(out var publishedAt)) throw new InvalidDataException("The update publish time is invalid.");
        var notes = root.GetProperty("releaseNotes");
        var installer = root.GetProperty("installer");
        var sha256 = installer.GetProperty("sha256").GetString();
        if (sha256 is null || !Sha256Pattern.IsMatch(sha256)) throw new InvalidDataException("The installer SHA-256 is invalid.");
        var size = installer.GetProperty("sizeBytes").GetInt64();
        if (size <= 0 || size > MaxInstallerBytes) throw new InvalidDataException("The installer size is invalid.");
        var installerUri = RequireHttpsUri(installer.GetProperty("url").GetString(), "Installer URL");
        return new UpdateRelease
        {
            Version = version,
            PublishedAtUtc = publishedAt.ToUniversalTime(),
            ReleaseNotesEnglish = ReadNotes(notes, "en"),
            ReleaseNotesVietnamese = ReadNotes(notes, "vi"),
            InstallerUri = installerUri,
            InstallerSha256 = sha256.ToUpperInvariant(),
            InstallerSizeBytes = size
        };
    }

    private static IReadOnlyList<string> ReadNotes(JsonElement notes, string language)
    {
        if (!notes.TryGetProperty(language, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
    }

    private static Uri RequireHttpsUri(string? value, string field)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? RequireHttpsUri(uri, field) : throw new InvalidDataException($"{field} is invalid.");

    private static Uri RequireHttpsUri(Uri? uri, string field)
        => uri is not null && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? uri : throw new InvalidDataException($"{field} must use HTTPS.");

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) throw new InvalidDataException("The update manifest exceeds 1 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private void CleanupOldDownloads(string currentFolder)
    {
        try
        {
            if (!Directory.Exists(_downloadRoot)) return;
            foreach (var folder in Directory.GetDirectories(_downloadRoot).Where(path => !path.Equals(currentFolder, StringComparison.OrdinalIgnoreCase)).OrderByDescending(Directory.GetCreationTimeUtc).Skip(1))
                Directory.Delete(folder, true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to clean old update downloads under {DownloadRoot}", _downloadRoot); }
    }
}
