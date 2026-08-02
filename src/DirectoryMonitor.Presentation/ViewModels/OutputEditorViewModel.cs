using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Outputs;
using DirectoryMonitor.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryMonitor.Presentation.ViewModels;

public enum SqlServerSampleAuthentication { WindowsAuthentication = 1, SqlLogin = 2 }

public partial class OutputEditorViewModel : ObservableObject
{
    public const string PostgreSqlConnectionStringExample = "Host=SERVER_NAME;Port=5432;Database=DATABASE_NAME;Username=USER_NAME;Password=PASSWORD;SSL Mode=VerifyFull;Timeout=15;Command Timeout=60;";
    public const string SqlServerWindowsConnectionStringExample = "Server=SERVER_NAME;Database=DATABASE_NAME;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;Connect Timeout=15;";
    public const string SqlServerLoginConnectionStringExample = "Server=SERVER_NAME;Database=DATABASE_NAME;User ID=USER_NAME;Password=PASSWORD;Encrypt=True;TrustServerCertificate=False;Connect Timeout=15;";
    private static readonly string[] ConnectionStringPlaceholders = ["SERVER_NAME", "DATABASE_NAME", "USER_NAME", "PASSWORD"];
    private readonly OutputDestination? _source;
    private readonly ILogDestinationFactory _factory;
    private readonly ILogger<OutputEditorViewModel> _logger;
    public Array DestinationTypes { get; } = Enum.GetValues<DestinationType>();
    public Array TextFormats { get; } = Enum.GetValues<TextOutputFormat>();
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private DestinationType _type = DestinationType.Text;
    [ObservableProperty] private bool _isDisabled;
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private TextOutputFormat _format = TextOutputFormat.CustomTemplate;
    [ObservableProperty] private string _template = new TextDestinationOptions().Template;
    [ObservableProperty] private int _rotateSizeMb = 100;
    [ObservableProperty] private int _retentionDays = 30;
    [ObservableProperty] private string _connectionString = string.Empty;
    [ObservableProperty] private string _testResult = string.Empty;
    [ObservableProperty] private SqlServerSampleAuthentication _selectedSqlServerSampleAuthentication = SqlServerSampleAuthentication.WindowsAuthentication;
    public bool IsText => Type == DestinationType.Text;
    public bool IsDatabase => Type is DestinationType.PostgreSql or DestinationType.SqlServer;
    public bool IsPostgreSql => Type == DestinationType.PostgreSql;
    public bool IsSqlServer => Type == DestinationType.SqlServer;
    public string DatabaseConnectionExample => Type switch
    {
        DestinationType.PostgreSql => PostgreSqlConnectionStringExample,
        DestinationType.SqlServer when SelectedSqlServerSampleAuthentication == SqlServerSampleAuthentication.SqlLogin => SqlServerLoginConnectionStringExample,
        DestinationType.SqlServer => SqlServerWindowsConnectionStringExample,
        _ => string.Empty
    };
    public string Preview
    {
        get
        {
            if (!IsText) return string.Empty;
            var sample = new MonitorEvent { SequenceNumber = 42, EventTimeUtc = DateTimeOffset.Now, EventType = MonitorEventType.Change, EntryType = EntryType.File, Path = @"C:\Example\Report.xlsx", OldPath = @"C:\Example\Report-old.xlsx", FileSize = 12_345, UserName = Environment.UserName, MachineName = Environment.MachineName };
            return TextTemplateRenderer.Render(sample, new TextDestinationOptions { Format = Format, Template = Template });
        }
    }

    public OutputEditorViewModel(OutputDestination? source, ILogDestinationFactory factory, ILogger<OutputEditorViewModel>? logger = null)
    {
        _source = source; _factory = factory; _logger = logger ?? NullLogger<OutputEditorViewModel>.Instance; if (source is null) return; Name = source.Name; Type = source.Type; IsDisabled = source.IsDisabled;
        try
        {
            if (source.Type == DestinationType.Text) { var options = TextLogDestination.Deserialize(source); FilePath = options.FilePath; Format = options.Format; Template = options.Template; RotateSizeMb = options.RotateSizeMb; RetentionDays = options.RetentionDays; }
            else if (IsDatabase)
            {
                var options = JsonSerializer.Deserialize<DatabaseDestinationOptions>(source.ConfigurationJson, JsonOptions);
                if (options is not null)
                {
                    ConnectionString = string.IsNullOrWhiteSpace(options.ProtectedConnectionString) ? string.Empty : SecretProtector.Unprotect(options.ProtectedConnectionString);
                    if (source.Type == DestinationType.SqlServer && UsesSqlLogin(ConnectionString)) SelectedSqlServerSampleAuthentication = SqlServerSampleAuthentication.SqlLogin;
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Stored configuration for output {OutputId} could not be opened", source.Id); TestResult = "Stored secret can only be opened by the Windows account that created it."; }
    }

    partial void OnTypeChanged(DestinationType value) { OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsDatabase)); OnPropertyChanged(nameof(IsPostgreSql)); OnPropertyChanged(nameof(IsSqlServer)); OnPropertyChanged(nameof(DatabaseConnectionExample)); OnPropertyChanged(nameof(Preview)); }
    partial void OnSelectedSqlServerSampleAuthenticationChanged(SqlServerSampleAuthentication value) => OnPropertyChanged(nameof(DatabaseConnectionExample));
    partial void OnFormatChanged(TextOutputFormat value) => OnPropertyChanged(nameof(Preview));
    partial void OnTemplateChanged(string value) => OnPropertyChanged(nameof(Preview));
    public void ApplyDatabaseConnectionExample() => ConnectionString = DatabaseConnectionExample;
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) { TestResult = "Name is required."; return false; }
        if (IsText && string.IsNullOrWhiteSpace(FilePath)) { TestResult = "File path is required."; return false; }
        if (IsText && TextTemplateRenderer.Validate(Template).Count > 0) { TestResult = "Template contains unknown tokens."; return false; }
        if (IsText && RotateSizeMb is < 1 or > 10_240) { TestResult = "Rotation size must be between 1 and 10,240 MB."; return false; }
        if (IsText && RetentionDays is < 1 or > 3_650) { TestResult = "Retention must be between 1 and 3,650 days."; return false; }
        if (IsDatabase && string.IsNullOrWhiteSpace(ConnectionString)) { TestResult = "Connection string is required."; return false; }
        if (IsDatabase)
        {
            var configuredValues = ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.IndexOf('=') is var separator && separator >= 0 ? part[(separator + 1)..].Trim() : string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = ConnectionStringPlaceholders.Where(configuredValues.Contains).ToArray();
            if (remaining.Length > 0)
            {
                TestResult = string.Format(Localized("ConnectionPlaceholdersRemaining", "Replace these sample values before testing or saving: {0}"), string.Join(", ", remaining));
                return false;
            }
        }
        return true;
    }
    public async Task<bool> TestAsync()
    {
        if (!Validate()) return false;
        try
        {
            var destination = Build(); var output = _factory.Get(destination.Type); await output.EnsureReadyAsync(destination);
            if (destination.Type == DestinationType.Text)
            {
                await output.DeliverAsync(destination, [new MonitorEvent { SequenceNumber = 1, EventTimeUtc = DateTimeOffset.UtcNow, EventType = MonitorEventType.Change, EntryType = EntryType.File, Path = @"C:\Example\Report.xlsx", OldPath = @"C:\Example\Report-old.xlsx", UserName = Environment.UserName, MachineName = Environment.MachineName }]);
                TestResult = "Test event was written successfully.";
            }
            else TestResult = destination.Type == DestinationType.WindowsEventLog ? "Event Log source is ready." : "Connection and schema are ready.";
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Output connection test failed for destination type {DestinationType}", Type); TestResult = ex.Message; return false; }
    }
    public OutputDestination Build()
    {
        var json = Type switch { DestinationType.Text => JsonSerializer.Serialize(new TextDestinationOptions { FilePath = FilePath.Trim(), Format = Format, Template = Template, RotateSizeMb = RotateSizeMb, RetentionDays = RetentionDays }, JsonOptions), DestinationType.PostgreSql or DestinationType.SqlServer => JsonSerializer.Serialize(new DatabaseDestinationOptions { ProtectedConnectionString = SecretProtector.Protect(ConnectionString) }, JsonOptions), _ => "{}" };
        return new OutputDestination { Id = _source?.Id ?? Guid.NewGuid(), Name = Name.Trim(), Type = Type, IsDisabled = IsDisabled, ConfigurationJson = json, CreationTimeUtc = _source?.CreationTimeUtc ?? DateTimeOffset.UtcNow, LastModificationUtc = DateTimeOffset.UtcNow };
    }
    private static bool UsesSqlLogin(string connectionString) => connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.IndexOf('=') is var separator && separator >= 0 ? part[..separator].Trim() : string.Empty)
        .Any(key => key.Equals("User ID", StringComparison.OrdinalIgnoreCase) || key.Equals("UID", StringComparison.OrdinalIgnoreCase));
    private static string Localized(string key, string fallback) => System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
