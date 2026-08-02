using DirectoryMonitor.Application;
using DirectoryMonitor.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DirectoryMonitor.Presentation.Services;

public sealed class SqliteOptionsMonitorService(
    IOptionsMonitor<SqliteOptions> options,
    SqliteConfigurationStore store,
    IRestartRequirementState restartState,
    ILogger<SqliteOptionsMonitorService> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = options.OnChange(OnOptionsChanged);
        return Task.CompletedTask;
    }

    private void OnOptionsChanged(SqliteOptions changed)
    {
        try
        {
            var configuredPath = SqliteConfigurationStore.ResolveConfiguredDatabasePath(changed.ConnectionString);
            var configuredConnectionString = SqliteConfigurationStore.ResolveConfiguredConnectionString(changed.ConnectionString);
            if (string.Equals(configuredConnectionString, store.ActiveConnectionString, StringComparison.Ordinal)) return;

            const string message = "The SQLite configuration changed. Restart Directory Monitor to use the new database.";
            restartState.RequireDatabaseRestart(message);
            logger.LogWarning("SQLite database configuration changed from {CurrentPath} to {ConfiguredPath}; restart is required", store.DatabasePath, configuredPath);
        }
        catch (Exception ex)
        {
            const string message = "The SQLite configuration is invalid. Fix appsettings.json and restart Directory Monitor.";
            restartState.RequireDatabaseRestart(message);
            logger.LogWarning(ex, "Ignoring invalid reloaded SQLite configuration; the active database remains unchanged");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => _subscription?.Dispose();
}
