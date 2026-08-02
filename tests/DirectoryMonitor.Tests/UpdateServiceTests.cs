using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Storage;
using DirectoryMonitor.Infrastructure.Updates;
using DirectoryMonitor.Presentation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryMonitor.Tests;

public sealed class UpdateServiceTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DirectoryMonitor.UpdateTests", Guid.NewGuid().ToString("N"));
    private SqliteConfigurationStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _store = new SqliteConfigurationStore(Path.Combine(_folder, "settings.db"));
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_folder, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void Schedule_policy_honors_intervals_startup_and_six_hour_error_retry()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Never, null, null, now, false));
        Assert.True(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.EveryStartup, now.AddDays(-1), now.AddDays(-1), now, false));
        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.EveryStartup, now.AddDays(-1), now.AddDays(-1), now, true));
        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Daily, now.AddHours(-23), now.AddHours(-23), now, false));
        Assert.True(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Daily, now.AddHours(-24), now.AddHours(-24), now, false));
        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Monthly, now.AddDays(-29), now.AddDays(-29), now, false));
        Assert.True(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Monthly, now.AddDays(-30), now.AddDays(-30), now, false));
        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Daily, now.AddDays(-2), now.AddHours(-5), now, false));
        Assert.True(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.Daily, now.AddHours(-7), now.AddHours(-6), now, false));
        Assert.True(UpdateSchedulePolicy.IsDue(UpdateCheckSchedule.EveryStartup, now.AddHours(-7), now.AddHours(-6), now, true));
    }

    [Fact]
    public async Task Blank_url_is_not_configured_and_http_url_is_rejected()
    {
        var service = CreateService(_ => throw new InvalidOperationException("HTTP should not be called."));
        Assert.Equal(UpdateStatus.NotConfigured, (await service.CheckAsync(true)).Status);

        await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, "http://updates.test/manifest.json");
        var result = await service.CheckAsync(true);

        Assert.Equal(UpdateStatus.Error, result.Status);
        Assert.Contains("HTTPS", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await _store.GetSettingAsync(UpdateSettingNames.LastAttemptUtc));
    }

    [Fact]
    public async Task Valid_manifest_is_available_and_skip_applies_only_to_that_version()
    {
        var version = "9.0.0";
        await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, "https://updates.test/manifest.json");
        await _store.SetSettingAsync(UpdateSettingNames.Schedule, UpdateCheckSchedule.Never.ToString());
        var service = CreateService(request => JsonResponse(request, Manifest(version, [1, 2, 3])));

        var available = await service.CheckAsync(true);
        Assert.Equal(UpdateStatus.Available, available.Status);
        Assert.Equal(new Version(9, 0, 0), available.Release!.Version);
        Assert.Equal("Added secure updates.", Assert.Single(available.Release.ReleaseNotesEnglish));
        Assert.NotNull(await _store.GetSettingAsync(UpdateSettingNames.LastSuccessfulCheckUtc));

        await service.SkipVersionAsync(available.Release.Version);
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync(true)).Status);

        version = "9.1.0";
        var newer = await service.CheckAsync(true);
        Assert.Equal(UpdateStatus.Available, newer.Status);
        Assert.Equal(new Version(9, 1, 0), newer.Release!.Version);
    }

    [Theory]
    [InlineData("1.1.5", UpdateStatus.UpToDate)]
    [InlineData("1.1.4", UpdateStatus.UpToDate)]
    [InlineData("1.1.1-beta", UpdateStatus.Error)]
    public async Task Version_comparison_accepts_only_stable_newer_releases(string version, UpdateStatus expected)
    {
        await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, "https://updates.test/manifest.json");
        var service = CreateService(request => JsonResponse(request, Manifest(version, [1])));

        Assert.Equal(expected, (await service.CheckAsync(true)).Status);
    }

    [Fact]
    public async Task Malformed_oversized_and_https_to_http_manifest_responses_are_rejected()
    {
        await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, "https://updates.test/manifest.json");
        var json = Manifest("9.0.0", [1]);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var bomPayload = Encoding.UTF8.GetPreamble().Concat(jsonBytes).ToArray();
        var bom = CreateService(request => BytesResponse(request, bomPayload));
        Assert.Equal(UpdateStatus.Available, (await bom.CheckAsync(true)).Status);

        var malformed = CreateService(request => JsonResponse(request, "{not-json"));
        Assert.Equal(UpdateStatus.Error, (await malformed.CheckAsync(true)).Status);

        var oversized = CreateService(request =>
        {
            var response = TextResponse(request, new string('x', 1024 * 1024 + 1));
            response.Content.Headers.ContentLength = 1024 * 1024 + 1;
            return response;
        });
        Assert.Equal(UpdateStatus.Error, (await oversized.CheckAsync(true)).Status);

        var downgrade = CreateService(request =>
        {
            var response = JsonResponse(request, Manifest("9.0.0", [1]));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://updates.test/redirected.json");
            return response;
        });
        Assert.Equal(UpdateStatus.Error, (await downgrade.CheckAsync(true)).Status);
    }

    [Fact]
    public async Task Installer_download_verifies_size_and_sha_and_removes_invalid_partial_file()
    {
        var original = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_UPDATE_DATA");
        var updatesFolder = Path.Combine(_folder, "downloads");
        Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_UPDATE_DATA", updatesFolder);
        try
        {
            var installerBytes = Encoding.UTF8.GetBytes("verified installer payload");
            var manifest = Manifest("9.0.0", installerBytes);
            await _store.SetSettingAsync(UpdateSettingNames.ManifestUrl, "https://updates.test/manifest.json");
            var service = CreateService(request => request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? JsonResponse(request, manifest)
                : BytesResponse(request, installerBytes));

            Assert.Equal(UpdateStatus.Available, (await service.CheckAsync(true)).Status);
            var installerPath = await service.DownloadInstallerAsync();
            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(installerPath));
            Assert.Empty(Directory.GetFiles(updatesFolder, "*.part", SearchOption.AllDirectories));

            var badManifest = Manifest("9.1.0", installerBytes, new string('0', 64));
            var invalid = CreateService(request => request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? JsonResponse(request, badManifest)
                : BytesResponse(request, installerBytes));
            Assert.Equal(UpdateStatus.Available, (await invalid.CheckAsync(true)).Status);
            await Assert.ThrowsAsync<InvalidDataException>(() => invalid.DownloadInstallerAsync());
            Assert.Empty(Directory.GetFiles(updatesFolder, "*.part", SearchOption.AllDirectories));

            var wrongSizeManifest = Manifest("9.2.0", installerBytes, declaredSize: installerBytes.Length + 1);
            var wrongSize = CreateService(request => request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? JsonResponse(request, wrongSizeManifest)
                : BytesResponse(request, installerBytes));
            Assert.Equal(UpdateStatus.Available, (await wrongSize.CheckAsync(true)).Status);
            await Assert.ThrowsAsync<InvalidDataException>(() => wrongSize.DownloadInstallerAsync());

            var largeInstaller = new byte[256 * 1024];
            RandomNumberGenerator.Fill(largeInstaller);
            var cancellable = CreateService(request => request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? JsonResponse(request, Manifest("9.3.0", largeInstaller))
                : BytesResponse(request, largeInstaller));
            Assert.Equal(UpdateStatus.Available, (await cancellable.CheckAsync(true)).Status);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellable.DownloadInstallerAsync(new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
            Assert.Empty(Directory.GetFiles(updatesFolder, "*.part", SearchOption.AllDirectories));
        }
        finally { Environment.SetEnvironmentVariable("DIRECTORY_MONITOR_UPDATE_DATA", original); }
    }

    [Fact]
    public void Installer_arguments_enable_silent_relaunch_and_database_import()
    {
        var backup = Path.Combine(_folder, "portable backup.db");
        var arguments = UpdateInstallerLauncher.BuildArguments(backup);

        Assert.Contains("/SILENT", arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", arguments);
        Assert.Contains("/CLOSEAPPLICATIONS", arguments);
        Assert.Contains("/RELAUNCH=1", arguments);
        Assert.Contains($"/IMPORTDB={Path.GetFullPath(backup)}", arguments);
    }

    private HttpsUpdateService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(_store, new HttpClient(new DelegateHandler(responder)), NullLogger<HttpsUpdateService>.Instance, new Version(1, 1, 5));

    private static string Manifest(string version, byte[] installerBytes, string? sha256 = null, long? declaredSize = null)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version,
            publishedAtUtc = "2026-08-02T12:00:00Z",
            releaseNotes = new { en = new[] { "Added secure updates." }, vi = new[] { "Bá»• sung cáº­p nháº­t an toÃ n." } },
            installer = new
            {
                url = "https://updates.test/DirectoryMonitor-Setup.exe",
                sha256 = sha256 ?? Convert.ToHexString(SHA256.HashData(installerBytes)),
                sizeBytes = declaredSize ?? installerBytes.LongLength
            }
        });

    private static HttpResponseMessage JsonResponse(HttpRequestMessage request, string content)
        => TextResponse(request, content, "application/json");

    private static HttpResponseMessage TextResponse(HttpRequestMessage request, string content, string mediaType = "text/plain")
        => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(content, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage BytesResponse(HttpRequestMessage request, byte[] content)
        => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
