using System.Threading;
using System.Windows;
using System.Net.Http;
using System.IO;
using DirectoryMonitor.Application;
using DirectoryMonitor.Infrastructure.Monitoring;
using DirectoryMonitor.Infrastructure.Outputs;
using DirectoryMonitor.Infrastructure.Storage;
using DirectoryMonitor.Infrastructure.Updates;
using DirectoryMonitor.Infrastructure.Transfer;
using DirectoryMonitor.Presentation.Services;
using DirectoryMonitor.Presentation.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Forms = System.Windows.Forms;

namespace DirectoryMonitor.Presentation;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _mutex;
    private Forms.NotifyIcon? _tray;
    private bool _isExiting;
    private readonly SerilogLoggerManager _logManager;

    public App()
    {
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        var logsFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
        Serilog.ILogger bootstrapLogger;
        try
        {
            Directory.CreateDirectory(logsFolder);
            bootstrapLogger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Async(sink => sink.File(
                    Path.Combine(logsFolder, "directory-monitor-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"))
                .CreateLogger();
        }
        catch
        {
            bootstrapLogger = new LoggerConfiguration().MinimumLevel.Information().CreateLogger();
        }

        Log.Logger = bootstrapLogger;
        _logManager = new SerilogLoggerManager(bootstrapLogger);
        DispatcherUnhandledException += (_, args) => Log.Fatal(args.Exception, "Unhandled WPF dispatcher exception");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception; terminating={IsTerminating}", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error(args.Exception, "Unobserved task exception"); args.SetObserved(); };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Information("Directory Monitor {Version} is starting", typeof(App).Assembly.GetName().Version?.ToString(3));
        _mutex = new Mutex(true, @"Global\DirectoryMonitor.SingleInstance", out var created);
        if (!created) { Log.Warning("Startup stopped because another instance is running"); System.Windows.MessageBox.Show("Directory Monitor is already running.", "Directory Monitor", MessageBoxButton.OK, MessageBoxImage.Information); Shutdown(); return; }

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(settingsPath))
        {
            var message = $"Required configuration file was not found: {settingsPath}";
            Log.Fatal(message);
            System.Windows.MessageBox.Show(message, "Directory Monitor startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
            return;
        }

        _host = Host.CreateDefaultBuilder(e.Args)
        .ConfigureAppConfiguration((_, configuration) => configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true))
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureServices((context, services) =>
        {
            services.Configure<SqliteOptions>(context.Configuration.GetSection(SqliteOptions.SectionName));
            services.AddSingleton(_logManager);
            services.AddSingleton<ILoggerProvider, DynamicSerilogLoggerProvider>();
            services.AddHostedService<SerilogReloadService>();
            services.AddSingleton<IRestartRequirementState, RestartRequirementState>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IImportExportService, ImportExportService>();
            services.AddSingleton(provider => new SqliteConfigurationStore(provider.GetRequiredService<IOptionsMonitor<SqliteOptions>>()));
            services.AddSingleton<IConfigurationStore>(provider => provider.GetRequiredService<SqliteConfigurationStore>());
            services.AddSingleton<IApplicationDataBackupService>(provider => provider.GetRequiredService<SqliteConfigurationStore>());
            services.AddHostedService<SqliteOptionsMonitorService>();
            services.AddSingleton<IEventStream, EventStream>();
            services.AddSingleton<IDeliveryStatusStream, DeliveryStatusStream>();
            services.AddSingleton<IDirectoryHealthStream, DirectoryHealthStream>();
            services.AddSingleton<ISnapshotService, SnapshotService>();
            services.AddSingleton<IFileMonitorService, FileMonitorService>();
            services.AddSingleton<ILogDestination, TextLogDestination>();
            services.AddSingleton<ILogDestination, PostgreSqlDestination>();
            services.AddSingleton<ILogDestination, SqlServerDestination>();
            services.AddSingleton<ILogDestination, WindowsEventLogDestination>();
            services.AddSingleton<ILogDestinationFactory, LogDestinationFactory>();
            services.AddSingleton<ITextLogMaintenance, TextLogMaintenance>();
            services.AddHostedService<OutputDispatcherService>();
            services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseProxy = true }) { Timeout = TimeSpan.FromMinutes(30) });
            services.AddSingleton<IUpdateService, HttpsUpdateService>();
            services.AddHostedService<UpdateBackgroundService>();
            services.AddSingleton<DialogService>();
            services.AddSingleton<PathLauncher>();
            services.AddSingleton<UpdateInstallerLauncher>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }).Build();

        try
        {
            var store = _host.Services.GetRequiredService<IConfigurationStore>();
            await store.InitializeAsync();
            await _host.StartAsync();
            await _host.Services.GetRequiredService<IFileMonitorService>().StartAsync();
            if (!string.Equals(Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DISABLE_STARTUP"), "1", StringComparison.Ordinal)
                && await store.GetSettingAsync("StartupConfigured") is null)
            {
                StartupService.SetEnabled(true);
                await store.SetSettingAsync("StartupConfigured", "1");
            }
            LocalizationService.Apply(await store.GetSettingAsync("Language") ?? LocalizationService.DefaultLanguage);
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window; ConfigureTray(window); window.Show();
            if (e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase))) window.Hide();
            await _host.Services.GetRequiredService<MainViewModel>().LoadAsync();
            Log.Information("Directory Monitor started successfully using SQLite database {DatabasePath}", store.DatabasePath);
        }
        catch (Exception ex) { Log.Fatal(ex, "Directory Monitor startup failed"); System.Windows.MessageBox.Show(ex.ToString(), "Directory Monitor startup failed", MessageBoxButton.OK, MessageBoxImage.Error); await ExitAsync(); }
    }

    private void ConfigureTray(MainWindow window)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(TryFindResource("Open") as string ?? "Open", null, (_, _) => Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }));
        menu.Items.Add(TryFindResource("ExitApplication") as string ?? "Exit", null, async (_, _) => await Dispatcher.InvokeAsync(RequestExitAsync));
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/directory-monitor.ico"))?.Stream;
        _tray = new Forms.NotifyIcon { Text = "Directory Monitor", Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream), Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); });
        window.RequestCloseToTray += (_, _) => { if (!_isExiting) window.Hide(); };
    }

    public async Task RequestExitAsync()
    {
        Log.Information("User requested application exit");
        var message = TryFindResource("ExitConfirmation") as string ?? "Exit Directory Monitor? Monitoring will stop.";
        var answer = System.Windows.MessageBox.Show(message, "Directory Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ExitAsync();
    }

    public async Task ExitAsync()
    {
        if (_isExiting) return; _isExiting = true;
        Log.Information("Directory Monitor shutdown is starting");
        if (MainWindow is DirectoryMonitor.Presentation.MainWindow window) window.AllowClose = true;
        try { if (_host is not null) { await _host.Services.GetRequiredService<IFileMonitorService>().StopAsync(); await _host.StopAsync(TimeSpan.FromSeconds(5)); } }
        catch (Exception ex) { Log.Error(ex, "Error while shutting down hosted services"); }
        Log.Information("Directory Monitor shutdown completed");
        if (_host is not null) _host.Dispose(); else _logManager.Dispose();
        _tray?.Dispose(); _mutex?.ReleaseMutex(); _mutex?.Dispose(); Shutdown();
    }
}
