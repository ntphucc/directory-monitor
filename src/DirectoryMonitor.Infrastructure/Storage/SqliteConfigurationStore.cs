using System.Globalization;
using System.Text.Json;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DirectoryMonitor.Infrastructure.Storage;

public sealed class SqliteConfigurationStore : IConfigurationStore, IApplicationDataBackupService
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly string? _legacyDatabasePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _sequenceLock = new(1, 1);
    private long _nextReservedSequence = 1;
    private long _reservedSequenceEnd;
    private const int SequenceReservationSize = 1_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteConfigurationStore(IOptionsMonitor<SqliteOptions> options)
        : this(ResolveConfiguredSettings(options.CurrentValue.ConnectionString))
    {
    }

    public SqliteConfigurationStore(string? databasePath = null, string? legacyDatabasePath = null)
        : this(CreatePathSettings(databasePath, legacyDatabasePath))
    {
    }

    private SqliteConfigurationStore(ResolvedSqliteSettings settings)
    {
        _databasePath = settings.DatabasePath;
        _legacyDatabasePath = settings.LegacyDatabasePath;
        _connectionString = settings.ConnectionString;
    }

    public string DatabasePath => _databasePath;

    public string ActiveConnectionString => _connectionString;

    public static string ResolveConfiguredDatabasePath(string? configuredConnectionString)
    {
        var overrideFolder = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA");
        if (!string.IsNullOrWhiteSpace(overrideFolder))
            return Path.GetFullPath(Path.Combine(overrideFolder, "directory-monitor.db"));

        if (string.IsNullOrWhiteSpace(configuredConnectionString))
            throw new InvalidOperationException($"Missing required configuration '{SqliteOptions.SectionName}:ConnectionString'.");

        SqliteConnectionStringBuilder builder;
        try { builder = new SqliteConnectionStringBuilder(configuredConnectionString); }
        catch (Exception ex) { throw new InvalidOperationException("The configured SQLite connection string is invalid.", ex); }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            throw new InvalidOperationException("The configured SQLite connection string must contain Data Source.");

        return Path.GetFullPath(Path.IsPathRooted(builder.DataSource)
            ? builder.DataSource
            : Path.Combine(AppContext.BaseDirectory, builder.DataSource));
    }

    public static string ResolveConfiguredConnectionString(string? configuredConnectionString)
    {
        var overrideFolder = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA");
        if (!string.IsNullOrWhiteSpace(overrideFolder)) return CreatePathSettings(Path.Combine(overrideFolder, "directory-monitor.db"), null).ConnectionString;
        var databasePath = ResolveConfiguredDatabasePath(configuredConnectionString);
        return new SqliteConnectionStringBuilder(configuredConnectionString) { DataSource = databasePath }.ToString();
    }

    private static string? ResolveLegacyPath(string? configuredConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA"))) return null;
        var configuredPath = ResolveConfiguredDatabasePath(configuredConnectionString);
        var defaultPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data", "directory-monitor.db"));
        return string.Equals(configuredPath, defaultPath, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Directory Monitor", "directory-monitor.db")
            : null;
    }

    private static ResolvedSqliteSettings ResolveConfiguredSettings(string? configuredConnectionString)
    {
        var overrideFolder = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA");
        if (!string.IsNullOrWhiteSpace(overrideFolder))
            return CreatePathSettings(Path.Combine(overrideFolder, "directory-monitor.db"), null);

        var databasePath = ResolveConfiguredDatabasePath(configuredConnectionString);
        return new(databasePath, ResolveConfiguredConnectionString(configuredConnectionString), ResolveLegacyPath(configuredConnectionString));
    }

    private static ResolvedSqliteSettings CreatePathSettings(string? databasePath, string? legacyDatabasePath)
    {
        var overrideFolder = Environment.GetEnvironmentVariable("DIRECTORY_MONITOR_DATA");
        if (databasePath is null)
        {
            databasePath = Path.Combine(overrideFolder ?? Path.Combine(AppContext.BaseDirectory, "Data"), "directory-monitor.db");
            if (overrideFolder is null)
                legacyDatabasePath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Directory Monitor", "directory-monitor.db");
        }

        var fullPath = Path.GetFullPath(databasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        return new(fullPath, connectionString, legacyDatabasePath is null ? null : Path.GetFullPath(legacyDatabasePath));
    }

    private sealed record ResolvedSqliteSettings(string DatabasePath, string ConnectionString, string? LegacyDatabasePath);

    public async Task<string> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The update backup folder is invalid.");
        Directory.CreateDirectory(folder);
        var temporaryPath = fullPath + ".part";
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            var targetConnectionString = new SqliteConnectionStringBuilder { DataSource = temporaryPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
            await using (var source = await OpenAsync(cancellationToken))
            await using (var target = new SqliteConnection(targetConnectionString))
            {
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
            }
            File.Move(temporaryPath, fullPath, true);
            return fullPath;
        }
        catch (Exception backupException)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception cleanupException) { throw new AggregateException("SQLite backup failed and its temporary file could not be removed.", backupException, cleanupException); }
            throw;
        }
        finally { _writeLock.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDataFolderIsWritable();
        await MigrateLegacyDatabaseAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync(connection, SchemaSql, cancellationToken);
        await MigrateEventSequenceIdentityAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO AppState(Key, Value) VALUES ('MachineId', $value), ('Sequence', '0');";
        command.Parameters.AddWithValue("$value", Guid.NewGuid().ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void EnsureDataFolderIsWritable()
    {
        var folder = Path.GetDirectoryName(_databasePath) ?? throw new InvalidOperationException("The SQLite data folder is invalid.");
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".directory-monitor-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"Directory Monitor cannot write to its data folder: {folder}", ex);
        }
    }

    private async Task MigrateLegacyDatabaseAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_databasePath) || string.IsNullOrWhiteSpace(_legacyDatabasePath) || !File.Exists(_legacyDatabasePath)) return;
        var migrationPath = _databasePath + ".migrating";
        try
        {
            if (File.Exists(migrationPath)) File.Delete(migrationPath);
            var sourceConnectionString = new SqliteConnectionStringBuilder { DataSource = _legacyDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            var targetConnectionString = new SqliteConnectionStringBuilder { DataSource = migrationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
            await using (var source = new SqliteConnection(sourceConnectionString))
            await using (var target = new SqliteConnection(targetConnectionString))
            {
                await source.OpenAsync(cancellationToken); await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
            }
            File.Move(migrationPath, _databasePath);
        }
        catch (Exception migrationException)
        {
            try { if (File.Exists(migrationPath)) File.Delete(migrationPath); }
            catch (Exception cleanupException) { throw new AggregateException("Legacy SQLite migration failed and its temporary file could not be removed.", migrationException, cleanupException); }
            throw;
        }
    }

    public async Task<Guid> GetMachineIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppState WHERE Key='MachineId';";
        return Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppState WHERE Key=$key;";
        Add(command, "$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync("INSERT INTO AppState(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;", [("$key", key), ("$value", value)], cancellationToken);

    public async Task<long> NextSequenceAsync(CancellationToken cancellationToken = default)
    {
        await _sequenceLock.WaitAsync(cancellationToken);
        try
        {
            if (_nextReservedSequence <= _reservedSequenceEnd) return _nextReservedSequence++;
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE AppState SET Value=CAST(Value AS INTEGER)+$size WHERE Key='Sequence' RETURNING Value;";
                Add(update, "$size", SequenceReservationSize);
                _reservedSequenceEnd = Convert.ToInt64(await update.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                _nextReservedSequence = _reservedSequenceEnd - SequenceReservationSize + 1;
                await transaction.CommitAsync(cancellationToken);
                return _nextReservedSequence++;
            }
            finally { _writeLock.Release(); }
        }
        finally { _sequenceLock.Release(); }
    }

    public async Task<long> GetLatestEventSequenceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(MAX(SequenceNumber),0) FROM Events;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<MonitoredDirectory>> GetDirectoriesAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var result = new List<MonitoredDirectory>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id,Path,Description,IsDisabled,IsDeleted,MonitorSubdirectories,DailyReconciliation,EventKinds,EntryTypeFilter,IncludePatterns,ExcludePatterns,DestinationIds,CreationTime,LastModification FROM Directories {(includeDeleted ? string.Empty : "WHERE IsDeleted=0")} ORDER BY Path;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MonitoredDirectory
            {
                Id = Guid.Parse(reader.GetString(0)), Path = reader.GetString(1), Description = reader.GetString(2),
                IsDisabled = reader.GetBoolean(3), IsDeleted = reader.GetBoolean(4), MonitorSubdirectories = reader.GetBoolean(5),
                EnableDailyReconciliation = reader.GetBoolean(6), EventKinds = (MonitoredEventKinds)reader.GetInt32(7),
                EntryTypeFilter = (EntryTypeFilter)reader.GetInt32(8),
                IncludePatterns = Deserialize<string[]>(reader.GetString(9)) ?? [], ExcludePatterns = Deserialize<string[]>(reader.GetString(10)) ?? [],
                DestinationIds = Deserialize<Guid[]>(reader.GetString(11)) ?? [], CreationTimeUtc = ParseDate(reader.GetString(12)),
                LastModificationUtc = ParseDate(reader.GetString(13))
            });
        }
        return result;
    }

    public async Task UpsertDirectoryAsync(MonitoredDirectory directory, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Directories(Id,Path,Description,IsDisabled,IsDeleted,MonitorSubdirectories,DailyReconciliation,EventKinds,EntryTypeFilter,IncludePatterns,ExcludePatterns,DestinationIds,CreationTime,LastModification)
                VALUES($id,$path,$description,$disabled,$deleted,$sub,$daily,$events,$entry,$include,$exclude,$destinations,$created,$modified)
                ON CONFLICT(Id) DO UPDATE SET Path=excluded.Path,Description=excluded.Description,IsDisabled=excluded.IsDisabled,IsDeleted=excluded.IsDeleted,
                MonitorSubdirectories=excluded.MonitorSubdirectories,DailyReconciliation=excluded.DailyReconciliation,EventKinds=excluded.EventKinds,
                EntryTypeFilter=excluded.EntryTypeFilter,IncludePatterns=excluded.IncludePatterns,ExcludePatterns=excluded.ExcludePatterns,
                DestinationIds=excluded.DestinationIds,LastModification=excluded.LastModification;
                """;
            Add(command, "$id", directory.Id); Add(command, "$path", directory.Path); Add(command, "$description", directory.Description);
            Add(command, "$disabled", directory.IsDisabled); Add(command, "$deleted", directory.IsDeleted); Add(command, "$sub", directory.MonitorSubdirectories);
            Add(command, "$daily", directory.EnableDailyReconciliation); Add(command, "$events", (int)directory.EventKinds); Add(command, "$entry", (int)directory.EntryTypeFilter);
            Add(command, "$include", Serialize(directory.IncludePatterns)); Add(command, "$exclude", Serialize(directory.ExcludePatterns));
            Add(command, "$destinations", Serialize(directory.DestinationIds)); Add(command, "$created", FormatDate(directory.CreationTimeUtc));
            Add(command, "$modified", FormatDate(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    public async Task SoftDeleteDirectoryAsync(Guid id, CancellationToken cancellationToken = default)
        => await ExecuteWriteAsync("UPDATE Directories SET IsDeleted=1,IsDisabled=1,LastModification=$now WHERE Id=$id;", [("$id", id), ("$now", FormatDate(DateTimeOffset.UtcNow))], cancellationToken);

    public async Task<IReadOnlyList<OutputDestination>> GetDestinationsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<OutputDestination>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.Id,d.Name,d.Type,d.IsDisabled,d.ConfigurationJson,d.CreationTime,d.LastModification,
                   COALESCE(SUM(CASE WHEN dl.State IN (0,2) THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN dl.State=2 THEN 1 ELSE 0 END),0),
                   MAX(CASE WHEN dl.State=2 THEN dl.LastError ELSE NULL END)
            FROM Destinations d LEFT JOIN Deliveries dl ON dl.DestinationId=d.Id AND dl.State IN (0,2)
            GROUP BY d.Id,d.Name,d.Type,d.IsDisabled,d.ConfigurationJson,d.CreationTime,d.LastModification
            ORDER BY d.Name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var disabled = reader.GetBoolean(3); var pending = reader.GetInt64(7); var failures = reader.GetInt64(8);
            result.Add(new OutputDestination { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), Type = (DestinationType)reader.GetInt32(2), IsDisabled = disabled, ConfigurationJson = reader.GetString(4), Status = disabled ? "Disabled" : failures > 0 ? "Retrying" : pending > 0 ? "Delivering" : "Ready", PendingCount = pending, LastError = reader.IsDBNull(9) ? null : reader.GetString(9), CreationTimeUtc = ParseDate(reader.GetString(5)), LastModificationUtc = ParseDate(reader.GetString(6)) });
        }
        return result;
    }

    public async Task UpsertDestinationAsync(OutputDestination destination, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync("""
            INSERT INTO Destinations(Id,Name,Type,IsDisabled,ConfigurationJson,CreationTime,LastModification) VALUES($id,$name,$type,$disabled,$json,$created,$modified)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Type=excluded.Type,IsDisabled=excluded.IsDisabled,ConfigurationJson=excluded.ConfigurationJson,LastModification=excluded.LastModification;
            """, [("$id", destination.Id), ("$name", destination.Name), ("$type", (int)destination.Type), ("$disabled", destination.IsDisabled), ("$json", destination.ConfigurationJson), ("$created", FormatDate(destination.CreationTimeUtc)), ("$modified", FormatDate(DateTimeOffset.UtcNow))], cancellationToken);
    }

    public async Task DeleteDestinationAsync(Guid id, bool discardPending = false, CancellationToken cancellationToken = default)
    {
        if (!discardPending && await GetPendingCountAsync(id, cancellationToken) > 0) throw new InvalidOperationException("Destination still has pending events.");
        var affectedDirectories = (await GetDirectoriesAsync(true, cancellationToken)).Where(item => item.DestinationIds.Contains(id)).ToArray();
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var deliveries = connection.CreateCommand(); deliveries.Transaction = transaction;
            deliveries.CommandText = "DELETE FROM Deliveries WHERE DestinationId=$id;"; Add(deliveries, "$id", id);
            await deliveries.ExecuteNonQueryAsync(cancellationToken);
            await using var destination = connection.CreateCommand(); destination.Transaction = transaction;
            destination.CommandText = "DELETE FROM Destinations WHERE Id=$id;"; Add(destination, "$id", id);
            await destination.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
        foreach (var directory in affectedDirectories)
            await UpsertDirectoryAsync(directory with { DestinationIds = directory.DestinationIds.Where(destinationId => destinationId != id).ToArray(), LastModificationUtc = DateTimeOffset.UtcNow }, cancellationToken);
    }

    public async Task EnqueueEventAsync(MonitorEvent monitorEvent, IReadOnlyCollection<Guid> destinationIds, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO Events(Id,SequenceNumber,EventTime,EventType,EntryType,DirectoryId,RootPath,Path,OldPath,FileSize,UserName,MachineId,MachineName,IsRecovered,CreationTime)
                VALUES($id,$sequence,$time,$type,$entry,$directory,$root,$path,$old,$size,$user,$machine,$machineName,$recovered,$created);
                """;
            AddEventParameters(command, monitorEvent);
            await command.ExecuteNonQueryAsync(cancellationToken);
            foreach (var destinationId in destinationIds)
            {
                await using var delivery = connection.CreateCommand();
                delivery.Transaction = transaction;
                delivery.CommandText = "INSERT OR IGNORE INTO Deliveries(EventId,DestinationId,State,Attempts,NextAttempt) SELECT $event,$destination,0,0,$now WHERE EXISTS(SELECT 1 FROM Destinations WHERE Id=$destination);";
                Add(delivery, "$event", monitorEvent.Id); Add(delivery, "$destination", destinationId); Add(delivery, "$now", FormatDate(DateTimeOffset.UtcNow));
                await delivery.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> ImportEventsAsync(IReadOnlyCollection<MonitorEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return 0;
        await _sequenceLock.WaitAsync(cancellationToken);
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var machineCommand = connection.CreateCommand();
                machineCommand.Transaction = transaction;
                machineCommand.CommandText = "SELECT Value FROM AppState WHERE Key='MachineId';";
                var currentMachineId = Guid.Parse((string)(await machineCommand.ExecuteScalarAsync(cancellationToken))!);
                var added = 0;
                foreach (var monitorEvent in events)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT OR IGNORE INTO Events(Id,SequenceNumber,EventTime,EventType,EntryType,DirectoryId,RootPath,Path,OldPath,FileSize,UserName,MachineId,MachineName,IsRecovered,CreationTime)
                        VALUES($id,$sequence,$time,$type,$entry,$directory,$root,$path,$old,$size,$user,$machine,$machineName,$recovered,$created);
                        """;
                    AddEventParameters(command, monitorEvent);
                    added += await command.ExecuteNonQueryAsync(cancellationToken);
                }

                var ownMaximum = events.Where(item => item.MachineId == currentMachineId).Select(item => item.SequenceNumber).DefaultIfEmpty(0).Max();
                if (ownMaximum > 0)
                {
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE AppState SET Value=CAST(MAX(CAST(Value AS INTEGER),$sequence) AS TEXT) WHERE Key='Sequence';";
                    Add(update, "$sequence", Math.Max(ownMaximum, _reservedSequenceEnd));
                    await update.ExecuteNonQueryAsync(cancellationToken);
                    _nextReservedSequence = 1;
                    _reservedSequenceEnd = 0;
                }
                await transaction.CommitAsync(cancellationToken);
                return added;
            }
            finally { _writeLock.Release(); }
        }
        finally { _sequenceLock.Release(); }
    }

    public async Task<IReadOnlyList<PendingDelivery>> GetPendingDeliveriesAsync(Guid destinationId, int take, CancellationToken cancellationToken = default)
    {
        var result = new List<PendingDelivery>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.Id,d.Name,d.Type,d.IsDisabled,d.ConfigurationJson,d.CreationTime,d.LastModification,
                   e.Id,e.SequenceNumber,e.EventTime,e.EventType,e.EntryType,e.DirectoryId,e.RootPath,e.Path,e.OldPath,e.FileSize,e.UserName,e.MachineId,e.MachineName,e.IsRecovered,e.CreationTime,dl.Attempts
            FROM Deliveries dl JOIN Events e ON e.Id=dl.EventId JOIN Destinations d ON d.Id=dl.DestinationId
            WHERE dl.DestinationId=$destination AND dl.State IN (0,2) AND dl.NextAttempt <= $now ORDER BY e.SequenceNumber LIMIT $take;
            """;
        Add(command, "$destination", destinationId); Add(command, "$now", FormatDate(DateTimeOffset.UtcNow)); Add(command, "$take", take);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var destination = new OutputDestination { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), Type = (DestinationType)reader.GetInt32(2), IsDisabled = reader.GetBoolean(3), ConfigurationJson = reader.GetString(4), CreationTimeUtc = ParseDate(reader.GetString(5)), LastModificationUtc = ParseDate(reader.GetString(6)) };
            var evt = new MonitorEvent { Id = Guid.Parse(reader.GetString(7)), SequenceNumber = reader.GetInt64(8), EventTimeUtc = ParseDate(reader.GetString(9)), EventType = (MonitorEventType)reader.GetInt32(10), EntryType = (EntryType)reader.GetInt32(11), DirectoryId = Guid.Parse(reader.GetString(12)), RootPath = reader.GetString(13), Path = reader.GetString(14), OldPath = reader.IsDBNull(15) ? null : reader.GetString(15), FileSize = reader.IsDBNull(16) ? null : reader.GetInt64(16), UserName = reader.GetString(17), MachineId = Guid.Parse(reader.GetString(18)), MachineName = reader.GetString(19), IsRecovered = reader.GetBoolean(20), CreationTimeUtc = ParseDate(reader.GetString(21)) };
            result.Add(new PendingDelivery(destination, evt, reader.GetInt32(22)));
        }
        return result;
    }

    public Task MarkDeliveredAsync(Guid destinationId, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
        => UpdateDeliveriesAsync(destinationId, eventIds, "UPDATE Deliveries SET State=1,LastError=NULL WHERE DestinationId=$destination AND EventId=$event;", null, null, cancellationToken);

    public Task MarkFailedAsync(Guid destinationId, IReadOnlyCollection<Guid> eventIds, string error, DateTimeOffset nextAttemptUtc, CancellationToken cancellationToken = default)
        => UpdateDeliveriesAsync(destinationId, eventIds, "UPDATE Deliveries SET State=2,Attempts=Attempts+1,LastError=$error,NextAttempt=$next WHERE DestinationId=$destination AND EventId=$event;", error, nextAttemptUtc, cancellationToken);

    public async Task<long> GetPendingCountAsync(Guid? destinationId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Deliveries WHERE State IN (0,2)" + (destinationId.HasValue ? " AND DestinationId=$destination" : string.Empty) + ";";
        if (destinationId.HasValue) Add(command, "$destination", destinationId.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetDeliveryStatusesAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        var result = eventIds.Distinct().ToDictionary(id => id, _ => "No outputs");
        if (result.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var names = result.Keys.Select((id, index) => { var name = "$event" + index; Add(command, name, id); return name; }).ToArray();
        command.CommandText = $"SELECT EventId,COUNT(*),SUM(CASE WHEN State=1 THEN 1 ELSE 0 END),SUM(CASE WHEN State=2 THEN 1 ELSE 0 END) FROM Deliveries WHERE EventId IN ({string.Join(',', names)}) GROUP BY EventId;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0)); var total = reader.GetInt64(1); var delivered = reader.GetInt64(2); var failed = reader.GetInt64(3);
            result[id] = delivered == total ? "Delivered" : failed > 0 ? "Retrying" : $"{delivered}/{total}";
        }
        return result;
    }

    public async Task CancelPendingAsync(IReadOnlyCollection<Guid> destinationIds, long throughSequence, CancellationToken cancellationToken = default)
    {
        foreach (var id in destinationIds)
            await ExecuteWriteAsync("UPDATE Deliveries SET State=3,LastError='Cancelled by Clear All' WHERE DestinationId=$destination AND EventId IN (SELECT Id FROM Events WHERE SequenceNumber <= $sequence) AND State IN (0,2);", [("$destination", id), ("$sequence", throughSequence)], cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, SnapshotEntry>> LoadSnapshotAsync(Guid directoryId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Path,EntryType,Size,LastWriteTime,Attributes FROM Snapshots WHERE DirectoryId=$directory;";
        Add(command, "$directory", directoryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = new SnapshotEntry(directoryId, reader.GetString(0), (EntryType)reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)), (FileAttributes)reader.GetInt32(4));
            result[entry.Path] = entry;
        }
        return result;
    }

    public async Task ReplaceSnapshotAsync(Guid directoryId, IReadOnlyCollection<SnapshotEntry> entries, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var delete = connection.CreateCommand()) { delete.Transaction = transaction; delete.CommandText = "DELETE FROM Snapshots WHERE DirectoryId=$directory;"; Add(delete, "$directory", directoryId); await delete.ExecuteNonQueryAsync(cancellationToken); }
            foreach (var entry in entries)
            {
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO Snapshots(DirectoryId,Path,EntryType,Size,LastWriteTime,Attributes) VALUES($directory,$path,$entry,$size,$write,$attributes);";
                Add(insert, "$directory", directoryId); Add(insert, "$path", entry.Path); Add(insert, "$entry", (int)entry.EntryType);
                Add(insert, "$size", entry.Size); Add(insert, "$write", entry.LastWriteTimeUtc is null ? null : FormatDate(entry.LastWriteTimeUtc.Value)); Add(insert, "$attributes", (int)entry.Attributes);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task UpdateDeliveriesAsync(Guid destinationId, IReadOnlyCollection<Guid> eventIds, string sql, string? error, DateTimeOffset? next, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var eventId in eventIds)
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
                Add(command, "$destination", destinationId); Add(command, "$event", eventId);
                if (error is not null) Add(command, "$error", error); if (next.HasValue) Add(command, "$next", FormatDate(next.Value));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ExecuteWriteAsync(string sql, IReadOnlyCollection<(string, object?)> parameters, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try { await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var item in parameters) Add(command, item.Item1, item.Item2); await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { _writeLock.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(cancellationToken); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid guid => guid.ToString("D"), bool boolean => boolean ? 1 : 0, _ => value });
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions);
    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddEventParameters(SqliteCommand command, MonitorEvent e)
    {
        Add(command, "$id", e.Id); Add(command, "$sequence", e.SequenceNumber); Add(command, "$time", FormatDate(e.EventTimeUtc)); Add(command, "$type", (int)e.EventType);
        Add(command, "$entry", (int)e.EntryType); Add(command, "$directory", e.DirectoryId); Add(command, "$root", e.RootPath); Add(command, "$path", e.Path); Add(command, "$old", e.OldPath);
        Add(command, "$size", e.FileSize); Add(command, "$user", e.UserName); Add(command, "$machine", e.MachineId); Add(command, "$machineName", e.MachineName);
        Add(command, "$recovered", e.IsRecovered); Add(command, "$created", FormatDate(e.CreationTimeUtc));
    }

    private static async Task MigrateEventSequenceIdentityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasCompositeIdentity = false;
        var hasLegacyIdentity = false;
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "PRAGMA index_list('Events');";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            var uniqueIndexes = new List<string>();
            while (await reader.ReadAsync(cancellationToken)) if (reader.GetInt32(2) == 1) uniqueIndexes.Add(reader.GetString(1));
            await reader.DisposeAsync();
            foreach (var index in uniqueIndexes)
            {
                await using var info = connection.CreateCommand();
                info.CommandText = $"PRAGMA index_info('{index.Replace("'", "''")}');";
                await using var columnsReader = await info.ExecuteReaderAsync(cancellationToken);
                var columns = new List<string>();
                while (await columnsReader.ReadAsync(cancellationToken)) columns.Add(columnsReader.GetString(2));
                hasCompositeIdentity |= columns.SequenceEqual(["MachineId", "SequenceNumber"], StringComparer.OrdinalIgnoreCase);
                hasLegacyIdentity |= columns.SequenceEqual(["SequenceNumber"], StringComparer.OrdinalIgnoreCase);
            }
        }
        if (hasCompositeIdentity || !hasLegacyIdentity) return;

        await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF;", cancellationToken);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE Events_v2(Id TEXT PRIMARY KEY,SequenceNumber INTEGER NOT NULL,EventTime TEXT NOT NULL,EventType INTEGER NOT NULL,EntryType INTEGER NOT NULL,DirectoryId TEXT NOT NULL,RootPath TEXT NOT NULL,Path TEXT NOT NULL,OldPath TEXT NULL,FileSize INTEGER NULL,UserName TEXT NOT NULL,MachineId TEXT NOT NULL,MachineName TEXT NOT NULL,IsRecovered INTEGER NOT NULL,CreationTime TEXT NOT NULL,UNIQUE(MachineId,SequenceNumber));
                INSERT INTO Events_v2 SELECT * FROM Events;
                DROP TABLE Events;
                ALTER TABLE Events_v2 RENAME TO Events;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken); }
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS AppState(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Directories(Id TEXT PRIMARY KEY,Path TEXT NOT NULL,Description TEXT NOT NULL,IsDisabled INTEGER NOT NULL,IsDeleted INTEGER NOT NULL,MonitorSubdirectories INTEGER NOT NULL,DailyReconciliation INTEGER NOT NULL,EventKinds INTEGER NOT NULL,EntryTypeFilter INTEGER NOT NULL,IncludePatterns TEXT NOT NULL,ExcludePatterns TEXT NOT NULL,DestinationIds TEXT NOT NULL,CreationTime TEXT NOT NULL,LastModification TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Destinations(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Type INTEGER NOT NULL,IsDisabled INTEGER NOT NULL,ConfigurationJson TEXT NOT NULL,CreationTime TEXT NOT NULL,LastModification TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Events(Id TEXT PRIMARY KEY,SequenceNumber INTEGER NOT NULL,EventTime TEXT NOT NULL,EventType INTEGER NOT NULL,EntryType INTEGER NOT NULL,DirectoryId TEXT NOT NULL,RootPath TEXT NOT NULL,Path TEXT NOT NULL,OldPath TEXT NULL,FileSize INTEGER NULL,UserName TEXT NOT NULL,MachineId TEXT NOT NULL,MachineName TEXT NOT NULL,IsRecovered INTEGER NOT NULL,CreationTime TEXT NOT NULL,UNIQUE(MachineId,SequenceNumber));
        CREATE TABLE IF NOT EXISTS Deliveries(EventId TEXT NOT NULL,DestinationId TEXT NOT NULL,State INTEGER NOT NULL,Attempts INTEGER NOT NULL,NextAttempt TEXT NOT NULL,LastError TEXT NULL,PRIMARY KEY(EventId,DestinationId),FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE CASCADE,FOREIGN KEY(DestinationId) REFERENCES Destinations(Id));
        CREATE INDEX IF NOT EXISTS IX_Deliveries_Pending ON Deliveries(DestinationId,State,NextAttempt);
        CREATE TABLE IF NOT EXISTS Snapshots(DirectoryId TEXT NOT NULL,Path TEXT NOT NULL,EntryType INTEGER NOT NULL,Size INTEGER NULL,LastWriteTime TEXT NULL,Attributes INTEGER NOT NULL,PRIMARY KEY(DirectoryId,Path));
        """;
}
