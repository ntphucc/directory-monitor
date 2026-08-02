namespace DirectoryMonitor.Infrastructure.Storage;

public sealed class SqliteOptions
{
    public const string SectionName = "DirectoryMonitor:Sqlite";
    public string ConnectionString { get; set; } = string.Empty;
}
