using System.Globalization;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DirectoryMonitor.Infrastructure.Updates;

public sealed class UpdateBackgroundService(IConfigurationStore store, IUpdateService updates, ILogger<UpdateBackgroundService> logger) : BackgroundService
{
    private bool _checkedThisStartup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckIfDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Scheduled update check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        var url = await store.GetSettingAsync(UpdateSettingNames.ManifestUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(url)) return;
        var scheduleText = await store.GetSettingAsync(UpdateSettingNames.Schedule, cancellationToken);
        var schedule = Enum.TryParse<UpdateCheckSchedule>(scheduleText, true, out var parsed) ? parsed : UpdateCheckSchedule.Daily;
        var lastSuccess = Parse(await store.GetSettingAsync(UpdateSettingNames.LastSuccessfulCheckUtc, cancellationToken));
        var lastAttempt = Parse(await store.GetSettingAsync(UpdateSettingNames.LastAttemptUtc, cancellationToken));
        if (!UpdateSchedulePolicy.IsDue(schedule, lastSuccess, lastAttempt, DateTimeOffset.UtcNow, _checkedThisStartup)) return;
        _checkedThisStartup = true;
        await updates.CheckAsync(false, cancellationToken);
    }

    private static DateTimeOffset? Parse(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;
}
