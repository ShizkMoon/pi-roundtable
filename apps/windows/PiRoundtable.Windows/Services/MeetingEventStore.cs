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

internal sealed record MeetingDeletionImpact(
    long EventCount,
    long CommandCount,
    long SubagentCount,
    long MemoryCandidateCount,
    long RecallAuditCount,
    long ContextSnapshotCount,
    long RetentionJobCount,
    long ArtifactCount = 0);

internal enum CommandJournalReservationDisposition
{
    Reserved,
    Duplicate,
    Conflict,
}

internal sealed record CommandJournalReservation(
    CommandJournalReservationDisposition Disposition,
    string Status,
    RuntimeCommandReceipt? Receipt);

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

    Task<CommandJournalReservation> ReserveCommandAsync(
        string meetingId,
        string commandId,
        string fingerprint,
        CancellationToken cancellationToken = default);

    Task CompleteCommandAsync(
        string meetingId,
        string fingerprint,
        RuntimeCommandReceipt receipt,
        CancellationToken cancellationToken = default);

    Task MarkCommandInterruptedAsync(
        string meetingId,
        string commandId,
        string fingerprint,
        CancellationToken cancellationToken = default);

    Task<MeetingDeletionImpact> GetDeletionImpactAsync(
        string meetingId,
        CancellationToken cancellationToken = default);

    Task DeleteMeetingAsync(string meetingId, CancellationToken cancellationToken = default);
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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IContentProtector _protector;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate;
    private bool _initialized;

    public MeetingEventStore(string? rootDirectory = null, IContentProtector? protector = null)
    {
        var root = LocalDataRoot.Resolve(rootDirectory);
        DatabasePath = Path.Combine(root, "data", "roundtable.db");
        _protector = protector ?? new DpapiContentProtector();
        _writeGate = LocalDatabaseWriteGate.For(DatabasePath);
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
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenConnectionAsync(
                    cancellationToken,
                    busyTimeoutMilliseconds: 1_000);
                await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
                await ExecuteNonQueryAsync(connection, null, "PRAGMA synchronous=FULL;", cancellationToken);
                await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken);
                await LocalDatabaseSchema.InitializeAsync(connection, cancellationToken);
                _initialized = true;
            }
            finally
            {
                _writeGate.Release();
            }
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
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, sequence, runtime_generation, event_kind,
                   occurred_at, visibility, protected_event
            FROM meeting_events
            WHERE meeting_id = $meeting_id
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        var events = new List<RuntimeMeetingEvent>();
        ulong expectedSequence = 1;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
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
        }
        var checkpoint = await ReadCheckpointAsync(
            connection,
            transaction,
            meetingId,
            cancellationToken);
        if (checkpoint is not null && checkpoint.LastSequence != expectedSequence - 1)
        {
            throw new InvalidDataException("本地事件检查点与事件日志不一致。");
        }
        await transaction.CommitAsync(cancellationToken);
        return events;
    }

    public async Task<MeetingDeletionImpact> GetDeletionImpactAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return new MeetingDeletionImpact(
            await CountAsync(connection, "meeting_events", "meeting_id", meetingId, cancellationToken),
            await CountAsync(connection, "command_journal", "meeting_id", meetingId, cancellationToken),
            await CountAsync(connection, "subagent_runs", "meeting_id", meetingId, cancellationToken),
            await CountAsync(connection, "memory_candidates", "source_meeting_id", meetingId, cancellationToken),
            await CountAsync(connection, "memory_recall_audits", "meeting_id", meetingId, cancellationToken),
            await CountAsync(connection, "role_context_snapshots", "meeting_id", meetingId, cancellationToken),
            await CountAsync(connection, "memory_retention_jobs", "source_meeting_id", meetingId, cancellationToken));
    }

    public async Task DeleteMeetingAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var (table, column) in new[]
            {
                ("memory_candidates", "source_meeting_id"),
                ("memory_recall_audits", "meeting_id"),
                ("role_context_snapshots", "meeting_id"),
                ("memory_retention_jobs", "source_meeting_id"),
                ("subagent_runs", "meeting_id"),
                ("command_journal", "meeting_id"),
                ("meeting_events", "meeting_id"),
                ("runtime_checkpoints", "meeting_id"),
                ("legacy_projections", "meeting_id"),
            })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE {column} = $meeting_id";
                command.Parameters.AddWithValue("$meeting_id", meetingId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        string column,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $meeting_id";
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
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

    public async Task<CommandJournalReservation> ReserveCommandAsync(
        string meetingId,
        string commandId,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandJournalKeys(meetingId, commandId, fingerprint);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var readCommand = connection.CreateCommand();
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT fingerprint, status, protected_receipt
                FROM command_journal
                WHERE meeting_id = $meeting_id AND command_id = $command_id
                """;
            readCommand.Parameters.AddWithValue("$meeting_id", meetingId);
            readCommand.Parameters.AddWithValue("$command_id", commandId);
            CommandJournalReservation? existingReservation = null;
            await using (var reader = await readCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    var storedFingerprint = reader.GetString(0);
                    var status = reader.GetString(1);
                    var receipt = reader.IsDBNull(2)
                        ? null
                        : DeserializeReceipt((byte[])reader[2]);
                    existingReservation = new CommandJournalReservation(
                        string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal)
                            ? CommandJournalReservationDisposition.Duplicate
                            : CommandJournalReservationDisposition.Conflict,
                        status,
                        receipt);
                }
            }
            if (existingReservation is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existingReservation;
            }

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO command_journal(
                    meeting_id, command_id, fingerprint, status,
                    sequence, protected_receipt, updated_at)
                VALUES(
                    $meeting_id, $command_id, $fingerprint, 'pending',
                    NULL, NULL, $updated_at)
                """;
            insertCommand.Parameters.AddWithValue("$meeting_id", meetingId);
            insertCommand.Parameters.AddWithValue("$command_id", commandId);
            insertCommand.Parameters.AddWithValue("$fingerprint", fingerprint);
            insertCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommandJournalReservation(
                CommandJournalReservationDisposition.Reserved,
                "pending",
                null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CompleteCommandAsync(
        string meetingId,
        string fingerprint,
        RuntimeCommandReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateCommandJournalKeys(meetingId, receipt.CommandId, fingerprint);
        if (receipt.Sequence is > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(receipt), "命令回执序号超出 SQLite INTEGER 范围。");
        }
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existing = await ReadCommandJournalAsync(
                connection,
                transaction,
                meetingId,
                receipt.CommandId,
                cancellationToken);
            if (existing is null)
            {
                throw new InvalidDataException("命令尚未写入持久日志，不能保存回执。");
            }
            if (!string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("命令 ID 对应的指纹不一致。");
            }
            if (existing.Value.Receipt is not null)
            {
                if (existing.Value.Receipt != receipt)
                {
                    throw new InvalidDataException("同一命令 ID 携带了不一致的持久回执。");
                }
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var protectedReceipt = _protector.Protect(
                JsonSerializer.SerializeToUtf8Bytes(receipt, SerializerOptions));
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE command_journal
                SET status = 'completed',
                    sequence = $sequence,
                    protected_receipt = $protected_receipt,
                    updated_at = $updated_at
                WHERE meeting_id = $meeting_id AND command_id = $command_id
                """;
            updateCommand.Parameters.AddWithValue("$sequence", receipt.Sequence is null
                ? DBNull.Value
                : checked((long)receipt.Sequence.Value));
            updateCommand.Parameters.AddWithValue("$protected_receipt", protectedReceipt);
            updateCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            updateCommand.Parameters.AddWithValue("$meeting_id", meetingId);
            updateCommand.Parameters.AddWithValue("$command_id", receipt.CommandId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MarkCommandInterruptedAsync(
        string meetingId,
        string commandId,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandJournalKeys(meetingId, commandId, fingerprint);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existing = await ReadCommandJournalAsync(
                connection,
                transaction,
                meetingId,
                commandId,
                cancellationToken);
            if (existing is null)
            {
                throw new InvalidDataException("命令尚未写入持久日志，不能标记为中断。");
            }
            if (!string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("命令 ID 对应的指纹不一致。");
            }
            if (existing.Value.Receipt is null && existing.Value.Status == "pending")
            {
                var receipt = new RuntimeCommandReceipt(
                    commandId,
                    "rejected",
                    null,
                    "command_outcome_unknown",
                    "Runtime Host 在持久回执写入前中断；为避免重复副作用，该命令不会自动重放。");
                var protectedReceipt = _protector.Protect(
                    JsonSerializer.SerializeToUtf8Bytes(receipt, SerializerOptions));
                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    UPDATE command_journal
                    SET status = 'interrupted',
                        protected_receipt = $protected_receipt,
                        updated_at = $updated_at
                    WHERE meeting_id = $meeting_id AND command_id = $command_id
                    """;
                updateCommand.Parameters.AddWithValue("$protected_receipt", protectedReceipt);
                updateCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                updateCommand.Parameters.AddWithValue("$meeting_id", meetingId);
                updateCommand.Parameters.AddWithValue("$command_id", commandId);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken,
        int busyTimeoutMilliseconds = 5_000)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1_000d)),
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                null,
                $"PRAGMA busy_timeout={busyTimeoutMilliseconds};",
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
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

    private RuntimeCommandReceipt DeserializeReceipt(byte[] ciphertext)
    {
        try
        {
            return JsonSerializer.Deserialize<RuntimeCommandReceipt>(
                _protector.Unprotect(ciphertext),
                SerializerOptions) ?? throw new InvalidDataException("本地命令回执为空。");
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("当前 Windows 用户无法解密本地命令回执。", error);
        }
    }

    private async Task<(string Fingerprint, string Status, RuntimeCommandReceipt? Receipt)?> ReadCommandJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string meetingId,
        string commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fingerprint, status, protected_receipt
            FROM command_journal
            WHERE meeting_id = $meeting_id AND command_id = $command_id
            """;
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        command.Parameters.AddWithValue("$command_id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : DeserializeReceipt((byte[])reader[2]));
    }

    private static void ValidateCommandJournalKeys(string meetingId, string commandId, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (commandId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(commandId), "命令 ID 过长。");
        }
        if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("命令指纹必须是 64 位十六进制 SHA-256。", nameof(fingerprint));
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
