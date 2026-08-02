using System.Text.Json;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using DirectoryMonitor.Infrastructure.Security;
using DirectoryMonitor.Presentation.ViewModels;

namespace DirectoryMonitor.Tests;

public sealed class OutputEditorViewModelTests
{
    [Fact]
    public void Database_examples_follow_type_and_authentication_without_overwriting_user_input()
    {
        var viewModel = Create();
        viewModel.ConnectionString = "custom connection";

        viewModel.Type = DestinationType.PostgreSql;
        Assert.True(viewModel.IsPostgreSql);
        Assert.Equal(OutputEditorViewModel.PostgreSqlConnectionStringExample, viewModel.DatabaseConnectionExample);
        Assert.Equal("custom connection", viewModel.ConnectionString);

        viewModel.Type = DestinationType.SqlServer;
        Assert.True(viewModel.IsSqlServer);
        Assert.Equal(OutputEditorViewModel.SqlServerWindowsConnectionStringExample, viewModel.DatabaseConnectionExample);
        Assert.Equal("custom connection", viewModel.ConnectionString);

        viewModel.SelectedSqlServerSampleAuthentication = SqlServerSampleAuthentication.SqlLogin;
        Assert.Equal(OutputEditorViewModel.SqlServerLoginConnectionStringExample, viewModel.DatabaseConnectionExample);
        Assert.Equal("custom connection", viewModel.ConnectionString);
    }

    [Fact]
    public void Use_sample_copies_only_the_current_example()
    {
        var viewModel = Create();
        viewModel.Type = DestinationType.SqlServer;
        viewModel.SelectedSqlServerSampleAuthentication = SqlServerSampleAuthentication.SqlLogin;

        viewModel.ApplyDatabaseConnectionExample();

        Assert.Equal(OutputEditorViewModel.SqlServerLoginConnectionStringExample, viewModel.ConnectionString);
    }

    [Fact]
    public void Existing_sql_login_destination_selects_the_matching_example_without_changing_its_secret()
    {
        const string connectionString = "Server=sql01;Database=DirectoryMonitor;User ID=app;Password=secret;Encrypt=True;TrustServerCertificate=False;";
        var source = new OutputDestination
        {
            Name = "SQL Server",
            Type = DestinationType.SqlServer,
            ConfigurationJson = JsonSerializer.Serialize(new DatabaseDestinationOptions { ProtectedConnectionString = SecretProtector.Protect(connectionString) }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

        var viewModel = new OutputEditorViewModel(source, new CountingFactory());

        Assert.Equal(SqlServerSampleAuthentication.SqlLogin, viewModel.SelectedSqlServerSampleAuthentication);
        Assert.Equal(connectionString, viewModel.ConnectionString);
    }

    [Fact]
    public void PostgreSql_sample_must_have_every_placeholder_replaced()
    {
        var viewModel = ValidDatabase(DestinationType.PostgreSql);
        viewModel.ApplyDatabaseConnectionExample();

        Assert.False(viewModel.Validate());
        Assert.Contains("SERVER_NAME", viewModel.TestResult);
        Assert.Contains("DATABASE_NAME", viewModel.TestResult);
        Assert.Contains("USER_NAME", viewModel.TestResult);
        Assert.Contains("PASSWORD", viewModel.TestResult);

        viewModel.ConnectionString = viewModel.ConnectionString
            .Replace("SERVER_NAME", "db.company.test")
            .Replace("DATABASE_NAME", "directory_monitor")
            .Replace("USER_NAME", "directory_monitor_app")
            .Replace("PASSWORD", "secret");
        Assert.True(viewModel.Validate());
    }

    [Fact]
    public void SqlServer_samples_validate_only_placeholders_actually_present()
    {
        var viewModel = ValidDatabase(DestinationType.SqlServer);
        viewModel.ApplyDatabaseConnectionExample();

        Assert.False(viewModel.Validate());
        Assert.Contains("SERVER_NAME", viewModel.TestResult);
        Assert.Contains("DATABASE_NAME", viewModel.TestResult);
        Assert.DoesNotContain("USER_NAME", viewModel.TestResult);
        Assert.DoesNotContain("PASSWORD", viewModel.TestResult);

        viewModel.SelectedSqlServerSampleAuthentication = SqlServerSampleAuthentication.SqlLogin;
        viewModel.ApplyDatabaseConnectionExample();
        Assert.False(viewModel.Validate());
        Assert.Contains("USER_NAME", viewModel.TestResult);
        Assert.Contains("PASSWORD", viewModel.TestResult);
    }

    [Fact]
    public async Task Placeholder_validation_stops_test_before_resolving_database_provider()
    {
        var factory = new CountingFactory();
        var viewModel = new OutputEditorViewModel(null, factory) { Name = "PostgreSQL", Type = DestinationType.PostgreSql };
        viewModel.ApplyDatabaseConnectionExample();

        Assert.False(await viewModel.TestAsync());
        Assert.Equal(0, factory.GetCalls);
    }

    [Fact]
    public void Custom_connection_string_without_sample_tokens_remains_valid()
    {
        var viewModel = ValidDatabase(DestinationType.SqlServer);
        viewModel.ConnectionString = "Server=sql01;Database=DirectoryMonitor;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;";

        Assert.True(viewModel.Validate());
    }

    private static OutputEditorViewModel Create() => new(null, new CountingFactory());

    private static OutputEditorViewModel ValidDatabase(DestinationType type)
        => new(null, new CountingFactory()) { Name = "Database output", Type = type };

    private sealed class CountingFactory : ILogDestinationFactory
    {
        public int GetCalls { get; private set; }
        public ILogDestination Get(DestinationType type)
        {
            GetCalls++;
            throw new InvalidOperationException("The provider should not be resolved during placeholder validation.");
        }
    }
}
