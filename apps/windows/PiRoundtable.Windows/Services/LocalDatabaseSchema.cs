using Microsoft.Data.Sqlite;

namespace PiRoundtable.Windows.Services;

/// <summary>
/// Owns the additive, transactional schema history for the Windows-local data
/// plane. Stores own queries; none of them may independently reinterpret the
/// database version or create an unversioned table.
/// </summary>
internal static class LocalDatabaseSchema
{
    internal const int CurrentVersion = 2;
    private const int MaximumBusyRetries = 3;
    private const int SchemaLockAttemptTimeoutSeconds = 1;
    private const int SchemaLockAttemptTimeoutMilliseconds = 1_000;

    private static readonly TableDefinition[] Version1Tables =
    [
        new("schema_info",
        [
            new("singleton", "INTEGER", IsRequired: false, PrimaryKeyOrder: 1),
            new("schema_version", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
        new("meeting_events",
        [
            new("meeting_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 1),
            new("event_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("sequence", "INTEGER", IsRequired: true, PrimaryKeyOrder: 2),
            new("runtime_generation", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("event_kind", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("occurred_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("visibility", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("protected_event", "BLOB", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
        new("command_journal",
        [
            new("meeting_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 1),
            new("command_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 2),
            new("fingerprint", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("status", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("sequence", "INTEGER", IsRequired: false, PrimaryKeyOrder: 0),
            new("protected_receipt", "BLOB", IsRequired: false, PrimaryKeyOrder: 0),
            new("updated_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
        new("runtime_checkpoints",
        [
            new("meeting_id", "TEXT", IsRequired: false, PrimaryKeyOrder: 1),
            new("last_sequence", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("runtime_generation", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("clean_shutdown", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("is_closed", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("updated_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
        new("subagent_runs",
        [
            new("meeting_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 1),
            new("subagent_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 2),
            new("parent_role_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("status", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("result_delivered", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("protected_state", "BLOB", IsRequired: true, PrimaryKeyOrder: 0),
            new("updated_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
        new("legacy_projections",
        [
            new("meeting_id", "TEXT", IsRequired: false, PrimaryKeyOrder: 1),
            new("protected_projection", "BLOB", IsRequired: true, PrimaryKeyOrder: 0),
            new("imported_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
    ];

    private static readonly TableDefinition[] Version2Tables =
    [
        .. Version1Tables,
        new("role_memories",
        [
            new("workspace_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 1),
            new("role_profile_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 2),
            new("memory_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 3),
            new("memory_kind", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("current_revision", "INTEGER", IsRequired: true, PrimaryKeyOrder: 0),
            new("created_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("updated_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("superseded_at", "TEXT", IsRequired: false, PrimaryKeyOrder: 0),
        ]),
        new("role_memory_revisions",
        [
            new("workspace_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 1),
            new("role_profile_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 2),
            new("memory_id", "TEXT", IsRequired: true, PrimaryKeyOrder: 3),
            new("revision", "INTEGER", IsRequired: true, PrimaryKeyOrder: 4),
            new("protected_content", "BLOB", IsRequired: true, PrimaryKeyOrder: 0),
            new("write_authority", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
            new("source_meeting_id", "TEXT", IsRequired: false, PrimaryKeyOrder: 0),
            new("source_event_id", "TEXT", IsRequired: false, PrimaryKeyOrder: 0),
            new("confidence", "REAL", IsRequired: false, PrimaryKeyOrder: 0),
            new("created_at", "TEXT", IsRequired: true, PrimaryKeyOrder: 0),
        ]),
    ];

    private static readonly IndexDefinition[] Version1Indexes =
    [
        new("meeting_events", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("meeting_id", IsDescending: false, "BINARY"),
            new("sequence", IsDescending: false, "BINARY"),
        ]),
        new("meeting_events", null, IsUnique: true, "u", IsPartial: false,
        [
            new("event_id", IsDescending: false, "BINARY"),
        ]),
        new("command_journal", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("meeting_id", IsDescending: false, "BINARY"),
            new("command_id", IsDescending: false, "BINARY"),
        ]),
        new("runtime_checkpoints", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("meeting_id", IsDescending: false, "BINARY"),
        ]),
        new("subagent_runs", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("meeting_id", IsDescending: false, "BINARY"),
            new("subagent_id", IsDescending: false, "BINARY"),
        ]),
        new("legacy_projections", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("meeting_id", IsDescending: false, "BINARY"),
        ]),
    ];

    private static readonly IndexDefinition[] Version2Indexes =
    [
        .. Version1Indexes,
        new("role_memories", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("workspace_id", IsDescending: false, "BINARY"),
            new("role_profile_id", IsDescending: false, "BINARY"),
            new("memory_id", IsDescending: false, "BINARY"),
        ]),
        new("role_memories", "ix_role_memories_active", IsUnique: false, "c", IsPartial: false,
        [
            new("workspace_id", IsDescending: false, "BINARY"),
            new("role_profile_id", IsDescending: false, "BINARY"),
            new("superseded_at", IsDescending: false, "BINARY"),
            new("updated_at", IsDescending: true, "BINARY"),
        ]),
        new("role_memory_revisions", null, IsUnique: true, "pk", IsPartial: false,
        [
            new("workspace_id", IsDescending: false, "BINARY"),
            new("role_profile_id", IsDescending: false, "BINARY"),
            new("memory_id", IsDescending: false, "BINARY"),
            new("revision", IsDescending: false, "BINARY"),
        ]),
    ];

    public static async Task InitializeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("Local database schema initialization requires an open connection.");
        }

        var previousDefaultTimeout = connection.DefaultTimeout;
        var previousBusyTimeout = await ReadBusyTimeoutAsync(connection, cancellationToken);
        connection.DefaultTimeout = SchemaLockAttemptTimeoutSeconds;
        try
        {
            await SetBusyTimeoutAsync(
                connection,
                Math.Min(previousBusyTimeout, SchemaLockAttemptTimeoutMilliseconds),
                cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await InitializeOnceAsync(connection, cancellationToken);
                    return;
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (SqliteException exception) when (IsBusy(exception) && attempt < MaximumBusyRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)), cancellationToken);
                }
                catch (SqliteException exception) when (IsBusy(exception))
                {
                    throw new TimeoutException("等待本地数据库架构写入锁超时。", exception);
                }
                catch (SqliteException exception) when (IsStructuralFailure(exception))
                {
                    throw new InvalidDataException("本地数据库架构无效或迁移失败。", exception);
                }
            }
        }
        finally
        {
            try
            {
                await SetBusyTimeoutAsync(connection, previousBusyTimeout, CancellationToken.None);
            }
            finally
            {
                connection.DefaultTimeout = previousDefaultTimeout;
            }
        }
    }

    private static async Task<int> ReadBusyTimeoutAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not long timeout || timeout is < 0 or > int.MaxValue)
        {
            throw new InvalidDataException("本地数据库 busy timeout 元数据无效。");
        }
        return (int)timeout;
    }

    private static async Task SetBusyTimeoutAsync(
        SqliteConnection connection,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={timeoutMilliseconds}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InitializeOnceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // An immediate transaction claims the write reservation before reading
        // schema state, avoiding the deferred read-to-write upgrade race when
        // two processes initialize the same new database.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var tables = await ReadUserTableNamesAsync(connection, transaction, cancellationToken);
        int version;
        if (!tables.Contains("schema_info"))
        {
            if (tables.Count != 0)
            {
                throw new InvalidDataException(
                    "本地数据库缺少版本元数据，但已经包含业务表；拒绝推断或覆盖其架构。");
            }
            await CreateVersion1Async(connection, transaction, cancellationToken);
            version = 1;
        }
        else
        {
            version = await ReadVersionAsync(connection, transaction, cancellationToken);
            EnsureSupportedVersion(version);
            await ValidateSchemaAsync(connection, transaction, version, cancellationToken);
        }

        if (version == 1)
        {
            await MigrateVersion1To2Async(connection, transaction, cancellationToken);
            version = 2;
        }
        EnsureSupportedVersion(version);
        await ValidateSchemaAsync(connection, transaction, version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CreateVersion1Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE schema_info (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                schema_version INTEGER NOT NULL
            );
            INSERT INTO schema_info(singleton, schema_version) VALUES (1, 1);

            CREATE TABLE meeting_events (
                meeting_id TEXT NOT NULL,
                event_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                runtime_generation INTEGER NOT NULL,
                event_kind TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                visibility TEXT NOT NULL,
                protected_event BLOB NOT NULL,
                PRIMARY KEY (meeting_id, sequence),
                UNIQUE (event_id)
            );

            CREATE TABLE command_journal (
                meeting_id TEXT NOT NULL,
                command_id TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                status TEXT NOT NULL,
                sequence INTEGER,
                protected_receipt BLOB,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, command_id)
            );

            CREATE TABLE runtime_checkpoints (
                meeting_id TEXT PRIMARY KEY,
                last_sequence INTEGER NOT NULL,
                runtime_generation INTEGER NOT NULL,
                clean_shutdown INTEGER NOT NULL,
                is_closed INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE subagent_runs (
                meeting_id TEXT NOT NULL,
                subagent_id TEXT NOT NULL,
                parent_role_id TEXT NOT NULL,
                status TEXT NOT NULL,
                result_delivered INTEGER NOT NULL,
                protected_state BLOB NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, subagent_id)
            );

            CREATE TABLE legacy_projections (
                meeting_id TEXT PRIMARY KEY,
                protected_projection BLOB NOT NULL,
                imported_at TEXT NOT NULL
            );
            """, cancellationToken);
    }

    private static async Task MigrateVersion1To2Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE role_memories (
                workspace_id TEXT NOT NULL,
                role_profile_id TEXT NOT NULL,
                memory_id TEXT NOT NULL,
                memory_kind TEXT NOT NULL,
                current_revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                superseded_at TEXT,
                PRIMARY KEY (workspace_id, role_profile_id, memory_id)
            );

            CREATE TABLE role_memory_revisions (
                workspace_id TEXT NOT NULL,
                role_profile_id TEXT NOT NULL,
                memory_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                protected_content BLOB NOT NULL,
                write_authority TEXT NOT NULL,
                source_meeting_id TEXT,
                source_event_id TEXT,
                confidence REAL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (workspace_id, role_profile_id, memory_id, revision),
                FOREIGN KEY (workspace_id, role_profile_id, memory_id)
                    REFERENCES role_memories(workspace_id, role_profile_id, memory_id)
                    ON DELETE RESTRICT
            );

            CREATE INDEX ix_role_memories_active
            ON role_memories(workspace_id, role_profile_id, superseded_at, updated_at DESC);

            UPDATE schema_info SET schema_version = 2 WHERE singleton = 1;
            """, cancellationToken);
    }

    private static void EnsureSupportedVersion(int version)
    {
        if (version is < 1 or > CurrentVersion)
        {
            throw new InvalidDataException($"不支持的本地数据库版本：{version}。");
        }
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT singleton, schema_version FROM schema_info";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetValue(0) is not long singleton || singleton != 1 ||
            reader.GetValue(1) is not long rawVersion ||
            rawVersion is < int.MinValue or > int.MaxValue ||
            await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("本地数据库版本元数据损坏。");
        }
        return (int)rawVersion;
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        var expectedTables = version switch
        {
            1 => Version1Tables,
            2 => Version2Tables,
            _ => throw new InvalidDataException($"不支持的本地数据库版本：{version}。"),
        };
        var actualTableNames = await ReadUserTableNamesAsync(connection, transaction, cancellationToken);
        var expectedTableNames = expectedTables
            .Select(table => table.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualTableNames.SetEquals(expectedTableNames))
        {
            throw new InvalidDataException("本地数据库表集合与其版本声明不一致。");
        }

        foreach (var table in expectedTables)
        {
            await ValidateTableAsync(connection, transaction, table, cancellationToken);
        }
        await RejectViewsAndTriggersAsync(connection, transaction, cancellationToken);
        await ValidateIndexesAsync(
            connection,
            transaction,
            expectedTables,
            version == 1 ? Version1Indexes : Version2Indexes,
            cancellationToken);
        if (version >= 2)
        {
            await ValidateRoleMemoryForeignKeyAsync(connection, transaction, cancellationToken);
        }
    }

    private static async Task ValidateTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableDefinition expected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(expected.Name)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actual = new List<ColumnDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ReadInt32(reader, 6, "hidden column flag") != 0)
            {
                throw new InvalidDataException($"本地数据库表 {expected.Name} 包含未评审的隐藏列。");
            }
            actual.Add(new ColumnDefinition(
                ReadRequiredString(reader, 1, "column name"),
                ReadRequiredString(reader, 2, "column type").ToUpperInvariant(),
                ReadInt32(reader, 3, "not-null flag") != 0,
                ReadInt32(reader, 5, "primary-key order")));
        }
        if (!actual.SequenceEqual(expected.Columns))
        {
            throw new InvalidDataException($"本地数据库表 {expected.Name} 的列定义无效。");
        }
    }

    private static async Task ValidateIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<IndexDefinition> expected,
        CancellationToken cancellationToken)
    {
        var actual = new List<IndexDefinition>();
        foreach (var table in tables)
        {
            var rawIndexes = new List<RawIndex>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA index_list({QuoteIdentifier(table.Name)})";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rawIndexes.Add(new RawIndex(
                        ReadRequiredString(reader, 1, "index name"),
                        ReadInt32(reader, 2, "unique flag") != 0,
                        ReadRequiredString(reader, 3, "index origin"),
                        ReadInt32(reader, 4, "partial-index flag") != 0));
                }
            }
            foreach (var index in rawIndexes)
            {
                actual.Add(new IndexDefinition(
                    table.Name,
                    index.Origin == "c" ? index.Name : null,
                    index.IsUnique,
                    index.Origin,
                    index.IsPartial,
                    await ReadIndexColumnsAsync(connection, transaction, index.Name, cancellationToken)));
            }
        }
        var actualFingerprints = actual.Select(IndexFingerprint).Order(StringComparer.Ordinal).ToArray();
        var expectedFingerprints = expected.Select(IndexFingerprint).Order(StringComparer.Ordinal).ToArray();
        if (!actualFingerprints.SequenceEqual(expectedFingerprints, StringComparer.Ordinal))
        {
            throw new InvalidDataException("本地数据库索引集合与其版本声明不一致。");
        }
    }

    private static async Task<IReadOnlyList<IndexColumn>> ReadIndexColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA index_xinfo({QuoteIdentifier(indexName)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<IndexColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ReadInt32(reader, 5, "index key flag") == 0)
            {
                continue;
            }
            if (ReadInt32(reader, 1, "index column id") < 0)
            {
                throw new InvalidDataException($"本地数据库索引 {indexName} 包含未评审的表达式列。");
            }
            columns.Add(new IndexColumn(
                ReadRequiredString(reader, 2, "index column name"),
                ReadInt32(reader, 3, "index sort flag") != 0,
                ReadRequiredString(reader, 4, "index collation")));
        }
        return columns;
    }

    private static async Task ValidateRoleMemoryForeignKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_list(role_memory_revisions)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actual = new List<ForeignKeyColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            actual.Add(new ForeignKeyColumn(
                ReadInt32(reader, 0, "foreign-key id"),
                ReadInt32(reader, 1, "foreign-key sequence"),
                ReadRequiredString(reader, 2, "foreign-key table"),
                ReadRequiredString(reader, 3, "foreign-key source column"),
                ReadRequiredString(reader, 4, "foreign-key target column"),
                ReadRequiredString(reader, 5, "foreign-key update action"),
                ReadRequiredString(reader, 6, "foreign-key delete action"),
                ReadRequiredString(reader, 7, "foreign-key match mode")));
        }
        ForeignKeyColumn[] expected =
        [
            new(0, 0, "role_memories", "workspace_id", "workspace_id", "NO ACTION", "RESTRICT", "NONE"),
            new(0, 1, "role_memories", "role_profile_id", "role_profile_id", "NO ACTION", "RESTRICT", "NONE"),
            new(0, 2, "role_memories", "memory_id", "memory_id", "NO ACTION", "RESTRICT", "NONE"),
        ];
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException("本地角色记忆修订外键定义无效。");
        }
    }

    private static async Task<HashSet<string>> ReadUserTableNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task RejectViewsAndTriggersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type IN ('view', 'trigger') AND name NOT LIKE 'sqlite_%'
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not long count || count != 0)
        {
            throw new InvalidDataException("本地数据库包含未评审的视图或触发器。");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private static bool IsStructuralFailure(SqliteException exception) =>
        exception.SqliteErrorCode is 1 or 11 or 17 or 18 or 19 or 20 or 24 or 26;

    private static int ReadInt32(SqliteDataReader reader, int ordinal, string field)
    {
        if (reader.GetValue(ordinal) is not long value || value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException($"本地数据库 {field} 元数据无效。");
        }
        return (int)value;
    }

    private static string ReadRequiredString(SqliteDataReader reader, int ordinal, string field)
    {
        if (reader.GetValue(ordinal) is not string value || string.IsNullOrEmpty(value))
        {
            throw new InvalidDataException($"本地数据库 {field} 元数据无效。");
        }
        return value;
    }

    private static string IndexFingerprint(IndexDefinition index) => string.Join(
        "|",
        index.Table,
        index.Name ?? "<auto>",
        index.IsUnique ? "unique" : "nonunique",
        index.Origin,
        index.IsPartial ? "partial" : "complete",
        string.Join(",", index.Columns.Select(column =>
            $"{column.Name}:{(column.IsDescending ? "desc" : "asc")}:{column.Collation}")));

    private sealed record TableDefinition(string Name, IReadOnlyList<ColumnDefinition> Columns);

    private sealed record ColumnDefinition(
        string Name,
        string Type,
        bool IsRequired,
        int PrimaryKeyOrder);

    private sealed record RawIndex(
        string Name,
        bool IsUnique,
        string Origin,
        bool IsPartial);

    private sealed record IndexDefinition(
        string Table,
        string? Name,
        bool IsUnique,
        string Origin,
        bool IsPartial,
        IReadOnlyList<IndexColumn> Columns);

    private sealed record IndexColumn(
        string Name,
        bool IsDescending,
        string Collation);

    private sealed record ForeignKeyColumn(
        int Id,
        int Sequence,
        string Table,
        string From,
        string To,
        string OnUpdate,
        string OnDelete,
        string Match);
}
