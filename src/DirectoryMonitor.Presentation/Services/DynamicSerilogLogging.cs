using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Serilog;
using Serilog.Events;

namespace DirectoryMonitor.Presentation.Services;

public sealed class SerilogLoggerManager : IDisposable
{
    private Serilog.ILogger _current;

    public SerilogLoggerManager(Serilog.ILogger initial) => _current = initial;
    public Serilog.ILogger Current => Volatile.Read(ref _current);

    public void Swap(Serilog.ILogger replacement)
    {
        var previous = Interlocked.Exchange(ref _current, replacement);
        Log.Logger = replacement;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (previous is IDisposable disposable) disposable.Dispose();
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _current, new LoggerConfiguration().CreateLogger()) is IDisposable disposable) disposable.Dispose();
        Log.CloseAndFlush();
    }
}

public sealed class DynamicSerilogLoggerProvider(SerilogLoggerManager manager) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new DynamicLogger(categoryName, manager, () => _scopeProvider);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;
    public void Dispose() { }

    private sealed class DynamicLogger(string category, SerilogLoggerManager manager, Func<IExternalScopeProvider> scopes) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes().Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && manager.Current.IsEnabled(ToSerilogLevel(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var logger = manager.Current.ForContext("SourceContext", category).ForContext("EventId", eventId.Id);
            var scopeValues = new List<string>();
            scopes().ForEachScope((scope, list) => list.Add(scope?.ToString() ?? string.Empty), scopeValues);
            if (scopeValues.Count > 0) logger = logger.ForContext("Scope", scopeValues);
            logger.Write(ToSerilogLevel(logLevel), exception, "{Message:l}", formatter(state, exception));
        }

        private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }
}

public sealed class SerilogReloadService(
    IConfiguration configuration,
    SerilogLoggerManager manager,
    ILogger<SerilogReloadService> logger) : IHostedService, IDisposable
{
    private IDisposable? _reloadSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ReloadLogger(initial: true);
        _reloadSubscription = ChangeToken.OnChange(configuration.GetReloadToken, () => ReloadLogger(initial: false));
        return Task.CompletedTask;
    }

    private void ReloadLogger(bool initial)
    {
        try
        {
            if (!configuration.GetSection("Serilog").Exists())
                throw new InvalidOperationException("The Serilog section is missing from appsettings.json.");

            var replacement = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
            manager.Swap(replacement);
            manager.Current.Information(initial ? "Serilog configuration loaded" : "Serilog configuration reloaded");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Serilog configuration reload failed; the existing logger remains active");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => _reloadSubscription?.Dispose();
}
