using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PiRoundtable.Windows.Services;

internal sealed record MeetingStoreCheckpoint(
    string MeetingId,
    ulong LastSequence,
    ulong RuntimeGeneration,
    bool CleanShutdown,
    bool IsClosed,
    DateTimeOffset UpdatedAt);

internal interface IMeetingEventStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> AppendAsync(RuntimeMeetingEvent meetingEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeMeetingEvent>> LoadEventsAsync(
        string meetingId,
        CancellationToken cancellationToken = default);

    Task<MeetingStoreCheckpoint?> GetCheckpointAsync(
        string meetingId,
        CancellationToken cancellationToken = default);
}

internal interface IContentProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

internal sealed class DpapiContentProtector : IContentProtector
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("PiRoundtable.Windows.LocalHistory.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => ProtectedData.Protect(
        plaintext.ToArray(),
        OptionalEntropy,
        DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ProtectedData.Unprotect(
        ciphertext.ToArray(),
        OptionalEntropy,
        DataProtectionScope.CurrentUser);
}

internal sealed class MeetingEventStore : IMeetingEventStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IContentProtector _protector;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate;
    private bool _initialized;

    public MeetingEventStore(string? rootDirectory = null, IContentProtector? protector = null)
    {
        var root = LocalDataRoot.Resolve(rootDirectory);
        DatabasePath = Path.Combine(root, "data", "roundtable.db");
        _protector = protector ?? new DpapiContentProtector();
        _writeGate = WriteGates.GetOrAdd(Path.GetFullPath(DatabasePath), static _ => new SemaphoreSlim(1, 1));
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteNonQueryAsync(connection, null, "PRAGMA synchronous=FULL;", cancellationToken);
            await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, """
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

            await using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "SELECT schema_version FROM schema_info WHERE singleton = 1";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
            if (version != SchemaVersion)
            {
                throw new InvalidDataException($"不支持的本地事件库版本：{version}。");
            }
            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<bool> AppendAsync(
        RuntimeMeetingEvent meetingEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meetingEvent);
        ValidateSqliteIntegerRange(meetingEvent);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using var duplicateCommand = connection.CreateCommand();
            duplicateCommand.Transaction = transaction;
            duplicateCommand.CommandText = """
                SELECT protected_event
                FROM meeting_events
                WHERE event_id = $event_id
                """;
            duplicateCommand.Parameters.AddWithValue("$event_id", meetingEvent.EventId);
            await using (var duplicateReader = await duplicateCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await duplicateReader.ReadAsync(cancellationToken))
                {
                    var storedEvent = DeserializeEvent((byte[])duplicateReader[0]);
                    if (!EventsEqual(storedEvent, meetingEvent))
                    {
                        throw new InvalidDataException("同一事件 ID 携带了不一致的事件内容。");
                    }
                    return false;
                }
            }

            var checkpoint = await ReadCheckpointAsync(connection, transaction, meetingEvent.MeetingId, cancellationToken);
            ValidateAppendOrder(meetingEvent, checkpoint);

            var protectedEvent = _protector.Protect(
                JsonSerializer.SerializeToUtf8Bytes(meetingEvent, SerializerOptions));
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO meeting_events(
                    meeting_id, event_id, sequence, runtime_generation,
                    event_kind, occurred_at, visibility, protected_event)
                VALUES(
                    $meeting_id, $event_id, $sequence, $runtime_generation,
                    $event_kind, $occurred_at, $visibility, $protected_event)
                """;
            insertCommand.Parameters.AddWithValue("$meeting_id", meetingEvent.MeetingId);
            insertCommand.Parameters.AddWithValue("$event_id", meetingEvent.EventId);
            insertCommand.Parameters.AddWithValue("$sequence", (long)meetingEvent.Sequence);
            insertCommand.Parameters.AddWithValue("$runtime_generation", (long)meetingEvent.RuntimeGeneration);
            insertCommand.Parameters.AddWithValue("$event_kind", meetingEvent.Kind);
            insertCommand.Parameters.AddWithValue("$occurred_at", meetingEvent.OccurredAt.ToString("O"));
            insertCommand.Parameters.AddWithValue("$visibility", meetingEvent.Visibility);
            insertCommand.Parameters.AddWithValue("$protected_event", protectedEvent);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            var cleanShutdown = meetingEvent.Kind == "runtime.lease_released";
            var isClosed = checkpoint?.IsClosed == true || meetingEvent.Kind == "meeting.closed";
            await using var checkpointCommand = connection.CreateCommand();
            checkpointCommand.Transaction = transaction;
            checkpointCommand.CommandText = """
                INSERT INTO runtime_checkpoints(
                    meeting_id, last_sequence, runtime_generation,
                    clean_shutdown, is_closed, updated_at)
                VALUES(
                    $meeting_id, $last_sequence, $runtime_generation,
                    $clean_shutdown, $is_closed, $updated_at)
                ON CONFLICT(meeting_id) DO UPDATE SET
                    last_sequence = excluded.last_sequence,
                    runtime_generation = excluded.runtime_generation,
                    clean_shutdown = excluded.clean_shutdown,
                    is_closed = excluded.is_closed,
                    updated_at = excluded.updated_at
                """;
            checkpointCommand.Parameters.AddWithValue("$meeting_id", meetingEvent.MeetingId);
            checkpointCommand.Parameters.AddWithValue("$last_sequence", (long)meetingEvent.Sequence);
            checkpointCommand.Parameters.AddWithValue("$runtime_generation", (long)meetingEvent.RuntimeGeneration);
            checkpointCommand.Parameters.AddWithValue("$clean_shutdown", cleanShutdown ? 1 : 0);
            checkpointCommand.Parameters.AddWithValue("$is_closed", isClosed ? 1 : 0);
            checkpointCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await checkpointCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<RuntimeMeetingEvent>> LoadEventsAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, sequence, runtime_generation, event_kind,
                   occurred_at, visibility, protected_event
            FROM meeting_events
            WHERE meeting_id = $meeting_id
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        var events = new List<RuntimeMeetingEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        ulong expectedSequence = 1;
        while (await reader.ReadAsync(cancellationToken))
        {
            var meetingEvent = DeserializeEvent((byte[])reader[6]);
            var indexSequence = checked((ulong)reader.GetInt64(1));
            var indexGeneration = checked((ulong)reader.GetInt64(2));
            var indexOccurredAt = DateTimeOffset.Parse(
                reader.GetString(4),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            if (
                !string.Equals(meetingEvent.MeetingId, meetingId, StringComparison.Ordinal) ||
                !string.Equals(meetingEvent.EventId, reader.GetString(0), StringComparison.Ordinal) ||
                meetingEvent.Sequence != indexSequence ||
                meetingEvent.RuntimeGeneration != indexGeneration ||
                !string.Equals(meetingEvent.Kind, reader.GetString(3), StringComparison.Ordinal) ||
                meetingEvent.OccurredAt != indexOccurredAt ||
                !string.Equals(meetingEvent.Visibility, reader.GetString(5), StringComparison.Ordinal))
            {
                throw new InvalidDataException("本地事件内容与索引不一致。");
            }
            if (meetingEvent.Sequence != expectedSequence)
            {
                throw new InvalidDataException(
                    $"本地事件序号不连续：期待 {expectedSequence}，读取到 {meetingEvent.Sequence}。");
            }
            events.Add(meetingEvent);
            expectedSequence = checked(expectedSequence + 1);
        }
        var checkpoint = await GetCheckpointAsync(meetingId, cancellationToken);
        if (checkpoint is not null && checkpoint.LastSequence != expectedSequence - 1)
        {
            throw new InvalidDataException("本地事件检查点与事件日志不一致。");
        }
        return events;
    }

    public async Task<MeetingStoreCheckpoint?> GetCheckpointAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadCheckpointAsync(connection, null, meetingId, cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken);
        return connection;
    }

    private RuntimeMeetingEvent DeserializeEvent(byte[] ciphertext)
    {
        try
        {
            return JsonSerializer.Deserialize<RuntimeMeetingEvent>(
                _protector.Unprotect(ciphertext),
                SerializerOptions) ?? throw new InvalidDataException("本地事件内容为空。");
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("当前 Windows 用户无法解密本地会议历史。", error);
        }
    }

    private static void ValidateSqliteIntegerRange(RuntimeMeetingEvent meetingEvent)
    {
        if (meetingEvent.Sequence > long.MaxValue || meetingEvent.RuntimeGeneration > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(meetingEvent),
                "事件序号和 Runtime generation 必须落在 SQLite INTEGER 范围内。");
        }
    }

    private static void ValidateAppendOrder(
        RuntimeMeetingEvent meetingEvent,
        MeetingStoreCheckpoint? checkpoint)
    {
        var expectedSequence = checkpoint is null ? 1UL : checked(checkpoint.LastSequence + 1);
        if (meetingEvent.Sequence != expectedSequence)
        {
            throw new InvalidDataException(
                $"事件序号不连续：期待 {expectedSequence}，收到 {meetingEvent.Sequence}。");
        }
        if (checkpoint?.IsClosed == true && meetingEvent.Kind != "runtime.lease_released")
        {
            throw new InvalidDataException("已结束的会议不能再追加事件。");
        }

        if (meetingEvent.Kind == "runtime.lease_acquired")
        {
            var expectedGeneration = checkpoint is null ? 1UL : checked(checkpoint.RuntimeGeneration + 1);
            if (meetingEvent.RuntimeGeneration != expectedGeneration)
            {
                throw new InvalidDataException(
                    $"Runtime generation 不连续：期待 {expectedGeneration}，收到 {meetingEvent.RuntimeGeneration}。");
            }
        }
        else if (checkpoint is null || meetingEvent.RuntimeGeneration != checkpoint.RuntimeGeneration)
        {
            throw new InvalidDataException("事件不属于当前有效的 Runtime generation。");
        }
    }

    private static bool EventsEqual(RuntimeMeetingEvent left, RuntimeMeetingEvent right) =>
        string.Equals(left.MeetingId, right.MeetingId, StringComparison.Ordinal) &&
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal) &&
        left.Sequence == right.Sequence &&
        left.RuntimeGeneration == right.RuntimeGeneration &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        left.OccurredAt == right.OccurredAt &&
        string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) &&
        string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal) &&
        string.Equals(left.CausationId, right.CausationId, StringComparison.Ordinal) &&
        string.Equals(left.Visibility, right.Visibility, StringComparison.Ordinal) &&
        left.Audience.SequenceEqual(right.Audience, StringComparer.Ordinal) &&
        JsonElement.DeepEquals(left.Payload, right.Payload);

    private static async Task<MeetingStoreCheckpoint?> ReadCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT last_sequence, runtime_generation, clean_shutdown, is_closed, updated_at
            FROM runtime_checkpoints
            WHERE meeting_id = $meeting_id
            """;
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new MeetingStoreCheckpoint(
            meetingId,
            checked((ulong)reader.GetInt64(0)),
            checked((ulong)reader.GetInt64(1)),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0,
            DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
