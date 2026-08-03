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

    public static async Task InitializeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_info (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                schema_version INTEGER NOT NULL
            );
            INSERT INTO schema_info(singleton, schema_version)
            VALUES (1, 1)
            ON CONFLICT(singleton) DO NOTHING;

            CREATE TABLE IF NOT EXISTS meeting_events (
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

            CREATE TABLE IF NOT EXISTS command_journal (
                meeting_id TEXT NOT NULL,
                command_id TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                status TEXT NOT NULL,
                sequence INTEGER,
                protected_receipt BLOB,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, command_id)
            );

            CREATE TABLE IF NOT EXISTS runtime_checkpoints (
                meeting_id TEXT PRIMARY KEY,
                last_sequence INTEGER NOT NULL,
                runtime_generation INTEGER NOT NULL,
                clean_shutdown INTEGER NOT NULL,
                is_closed INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS subagent_runs (
                meeting_id TEXT NOT NULL,
                subagent_id TEXT NOT NULL,
                parent_role_id TEXT NOT NULL,
                status TEXT NOT NULL,
                result_delivered INTEGER NOT NULL,
                protected_state BLOB NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, subagent_id)
            );

            CREATE TABLE IF NOT EXISTS legacy_projections (
                meeting_id TEXT PRIMARY KEY,
                protected_projection BLOB NOT NULL,
                imported_at TEXT NOT NULL
            );
            """, cancellationToken);

        var version = await ReadVersionAsync(connection, transaction, cancellationToken);
        if (version == 1)
        {
            await MigrateVersion1To2Async(connection, transaction, cancellationToken);
            version = 2;
        }
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"不支持的本地数据库版本：{version}。");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateVersion1To2Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS role_memories (
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

            CREATE TABLE IF NOT EXISTS role_memory_revisions (
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

            CREATE INDEX IF NOT EXISTS ix_role_memories_active
            ON role_memories(workspace_id, role_profile_id, superseded_at, updated_at DESC);

            UPDATE schema_info SET schema_version = 2 WHERE singleton = 1;
            """, cancellationToken);
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM schema_info WHERE singleton = 1";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
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
}
