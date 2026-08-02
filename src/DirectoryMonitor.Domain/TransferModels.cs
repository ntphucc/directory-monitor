namespace DirectoryMonitor.Domain;

public sealed class TransferSelection
{
    public bool Directories { get; set; }
    public bool Events { get; set; }
    public bool Outputs { get; set; }
    public bool Any => Directories || Events || Outputs;
}

public sealed class TransferEnvelopeV1
{
    public int SchemaVersion { get; set; } = 1;
    public string AppVersion { get; set; } = string.Empty;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public Guid SourceMachineId { get; set; }
    public string SourceMachineName { get; set; } = string.Empty;
    public List<TransferDirectory> Directories { get; set; } = [];
    public List<TransferEvent> Events { get; set; } = [];
    public List<TransferOutput> Outputs { get; set; } = [];
}

public sealed class TransferDirectory
{
    public Guid Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsDisabled { get; set; }
    public bool MonitorSubdirectories { get; set; }
    public bool EnableDailyReconciliation { get; set; }
    public MonitoredEventKinds EventKinds { get; set; }
    public EntryTypeFilter EntryTypeFilter { get; set; }
    public string[] IncludePatterns { get; set; } = [];
    public string[] ExcludePatterns { get; set; } = [];
    public Guid[] DestinationIds { get; set; } = [];
    public DateTimeOffset CreationTimeUtc { get; set; }
    public DateTimeOffset LastModificationUtc { get; set; }
}

public sealed class TransferEvent
{
    public Guid Id { get; set; }
    public long SequenceNumber { get; set; }
    public DateTimeOffset EventTimeUtc { get; set; }
    public MonitorEventType EventType { get; set; }
    public EntryType EntryType { get; set; }
    public Guid DirectoryId { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? OldPath { get; set; }
    public long? FileSize { get; set; }
    public string UserName { get; set; } = string.Empty;
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public bool IsRecovered { get; set; }
    public DateTimeOffset CreationTimeUtc { get; set; }
}

public sealed class TransferOutput
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DestinationType Type { get; set; }
    public bool IsDisabled { get; set; }
    public string ConfigurationJson { get; set; } = "{}";
    public bool RequiresCredentials { get; set; }
    public string? ConnectionString { get; set; }
    public DateTimeOffset CreationTimeUtc { get; set; }
    public DateTimeOffset LastModificationUtc { get; set; }
}

public sealed record ImportCategoryResult(int Added, int Skipped, int Failed);

public sealed class ImportSummary
{
    public ImportCategoryResult Directories { get; init; } = new(0, 0, 0);
    public ImportCategoryResult Events { get; init; } = new(0, 0, 0);
    public ImportCategoryResult Outputs { get; init; } = new(0, 0, 0);
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
