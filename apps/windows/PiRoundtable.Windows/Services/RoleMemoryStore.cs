using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PiRoundtable.Windows.Services;

internal enum RoleMemoryKind
{
    Identity,
    Preference,
    Fact,
    Decision,
    Lesson,
}

internal enum RoleMemoryWriteAuthority
{
    UserApproved,
    MeetingClosePolicy,
    AutomaticPolicy,
}

internal sealed record RoleMemoryDraft(
    string WorkspaceId,
    string RoleProfileId,
    string MemoryId,
    RoleMemoryKind Kind,
    string Content,
    RoleMemoryWriteAuthority WriteAuthority,
    string? SourceMeetingId = null,
    string? SourceEventId = null,
    double? Confidence = null);

internal sealed record RoleMemoryEntry(
    string WorkspaceId,
    string RoleProfileId,
    string MemoryId,
    RoleMemoryKind Kind,
    int Revision,
    string Content,
    RoleMemoryWriteAuthority WriteAuthority,
    string? SourceMeetingId,
    string? SourceEventId,
    double? Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SupersededAt);

internal sealed record RoleMemoryRecallItem(string MemoryId, int Revision, string Content);

internal sealed record RoleMemoryRecallFreeze(
    string AuditId,
    IReadOnlyList<string> ConsideredRevisionRefs,
    IReadOnlyList<RoleMemoryRecallItem> Selected);

internal enum RoleMemoryCandidateStatus
{
    Pending,
    Approved,
    Rejected,
}

internal sealed record RoleMemoryCandidateDraft(
    string CandidateId,
    string WorkspaceId,
    string RoleProfileId,
    string SourceMeetingId,
    string? SourceEventId,
    RoleMemoryKind Kind,
    string Content,
    double? Confidence = null);

internal sealed record RoleMemoryCandidate(
    string CandidateId,
    string WorkspaceId,
    string RoleProfileId,
    string SourceMeetingId,
    string? SourceEventId,
    RoleMemoryKind Kind,
    string Content,
    double? Confidence,
    RoleMemoryCandidateStatus Status,
    int DecisionRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record RoleMemoryCandidateDecision(
    RoleMemoryCandidate Candidate,
    RoleMemoryEntry? ApprovedMemory);

internal interface IRoleMemoryStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<RoleMemoryEntry> AppendRevisionAsync(
        RoleMemoryDraft draft,
        int? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleMemoryEntry>> LoadActiveAsync(
        string workspaceId,
        string roleProfileId,
        int maximumItems = 32,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleMemoryEntry>> LoadHistoryAsync(
        string workspaceId,
        string roleProfileId,
        string memoryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleMemoryEntry>> LoadAllAsync(
        string workspaceId,
        string roleProfileId,
        int maximumItems = 256,
        CancellationToken cancellationToken = default);

    Task<bool> SupersedeAsync(
        string workspaceId,
        string roleProfileId,
        string memoryId,
        int expectedRevision,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(
        string workspaceId,
        string roleProfileId,
        string memoryId,
        int expectedRevision,
        CancellationToken cancellationToken = default);

    Task<RoleMemoryCandidate> ProposeCandidateAsync(
        RoleMemoryCandidateDraft draft,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleMemoryCandidate>> LoadCandidatesAsync(
        string workspaceId,
        string roleProfileId,
        RoleMemoryCandidateStatus? status = null,
        int maximumItems = 64,
        CancellationToken cancellationToken = default);

    Task<RoleMemoryCandidateDecision> ReviewCandidateAsync(
        string candidateId,
        int expectedDecisionRevision,
        bool approve,
        CancellationToken cancellationToken = default);

    Task<RoleMemoryRecallFreeze> FreezeRecallAsync(
        string workspaceId,
        string roleProfileId,
        string meetingId,
        ulong runtimeGeneration,
        CancellationToken cancellationToken = default);

    Task MarkRecallInjectedAsync(
        string auditId,
        IReadOnlyList<string> selectedRevisionRefs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable per-role memory backed by the Windows user's encrypted local store.
/// Logical memories are mutable only through status/current-revision pointers;
/// revision contents themselves are append-only to preserve provenance.
/// </summary>
internal sealed partial class RoleMemoryStore : IRoleMemoryStore
{
    private const int MaximumContentCharacters = 8_000;
    private const int MaximumLoadItems = 256;
    private readonly IContentProtector _protector;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate;
    private bool _initialized;

    public RoleMemoryStore(
        string? rootDirectory = null,
        IContentProtector? protector = null,
        Func<DateTimeOffset>? now = null)
    {
        var root = LocalDataRoot.Resolve(rootDirectory);
        DatabasePath = Path.Combine(root, "data", "roundtable.db");
        _protector = protector ?? new DpapiContentProtector();
        _now = now ?? (() => DateTimeOffset.UtcNow);
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

    public async Task<RoleMemoryEntry> AppendRevisionAsync(
        RoleMemoryDraft draft,
        int? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        if (expectedRevision is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var current = await ReadLogicalMemoryAsync(connection, transaction, draft, cancellationToken);
            if (current?.SupersededAt is not null)
            {
                throw new InvalidOperationException("已归档的角色记忆不能继续追加修订。");
            }
            if (expectedRevision is not null && current?.Revision != expectedRevision)
            {
                throw new InvalidDataException("角色记忆已被其他写入更新，请重新读取后再修改。");
            }

            var now = _now();
            var revision = checked((current?.Revision ?? 0) + 1);
            if (current is null)
            {
                await InsertLogicalMemoryAsync(connection, transaction, draft, revision, now, cancellationToken);
            }
            else
            {
                await UpdateLogicalMemoryAsync(connection, transaction, draft, revision, now, cancellationToken);
            }
            await InsertRevisionAsync(connection, transaction, draft, revision, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToEntry(draft, revision, current?.CreatedAt ?? now, now, null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<RoleMemoryEntry>> LoadActiveAsync(
        string workspaceId,
        string roleProfileId,
        int maximumItems = 32,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        ValidateId(roleProfileId, nameof(roleProfileId));
        if (maximumItems is < 1 or > MaximumLoadItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.memory_id, m.memory_kind, m.current_revision,
                   r.protected_content, r.write_authority,
                   r.source_meeting_id, r.source_event_id, r.confidence,
                   m.created_at, m.updated_at, m.superseded_at
            FROM role_memories AS m
            JOIN role_memory_revisions AS r
              ON r.workspace_id = m.workspace_id
             AND r.role_profile_id = m.role_profile_id
             AND r.memory_id = m.memory_id
             AND r.revision = m.current_revision
            WHERE m.workspace_id = $workspace_id
              AND m.role_profile_id = $role_profile_id
              AND m.superseded_at IS NULL
            ORDER BY m.updated_at DESC, m.memory_id ASC
            LIMIT $maximum_items
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
        command.Parameters.AddWithValue("$maximum_items", maximumItems);
        return await ReadEntriesAsync(command, workspaceId, roleProfileId, cancellationToken);
    }

    public async Task<IReadOnlyList<RoleMemoryEntry>> LoadHistoryAsync(
        string workspaceId,
        string roleProfileId,
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        ValidateId(roleProfileId, nameof(roleProfileId));
        ValidateId(memoryId, nameof(memoryId));
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.memory_id, m.memory_kind, r.revision,
                   r.protected_content, r.write_authority,
                   r.source_meeting_id, r.source_event_id, r.confidence,
                   m.created_at, r.created_at, m.superseded_at
            FROM role_memories AS m
            JOIN role_memory_revisions AS r
              ON r.workspace_id = m.workspace_id
             AND r.role_profile_id = m.role_profile_id
             AND r.memory_id = m.memory_id
            WHERE m.workspace_id = $workspace_id
              AND m.role_profile_id = $role_profile_id
              AND m.memory_id = $memory_id
            ORDER BY r.revision ASC
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
        command.Parameters.AddWithValue("$memory_id", memoryId);
        return await ReadEntriesAsync(command, workspaceId, roleProfileId, cancellationToken);
    }

    public async Task<IReadOnlyList<RoleMemoryEntry>> LoadAllAsync(
        string workspaceId,
        string roleProfileId,
        int maximumItems = MaximumLoadItems,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        ValidateId(roleProfileId, nameof(roleProfileId));
        if (maximumItems is < 1 or > MaximumLoadItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.memory_id, m.memory_kind, m.current_revision,
                   r.protected_content, r.write_authority,
                   r.source_meeting_id, r.source_event_id, r.confidence,
                   m.created_at, m.updated_at, m.superseded_at
            FROM role_memories AS m
            JOIN role_memory_revisions AS r
              ON r.workspace_id = m.workspace_id
             AND r.role_profile_id = m.role_profile_id
             AND r.memory_id = m.memory_id
             AND r.revision = m.current_revision
            WHERE m.workspace_id = $workspace_id
              AND m.role_profile_id = $role_profile_id
            ORDER BY m.updated_at DESC, m.memory_id ASC
            LIMIT $maximum_items
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
        command.Parameters.AddWithValue("$maximum_items", maximumItems);
        return await ReadEntriesAsync(command, workspaceId, roleProfileId, cancellationToken);
    }

    public async Task<bool> SupersedeAsync(
        string workspaceId,
        string roleProfileId,
        string memoryId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        ValidateId(roleProfileId, nameof(roleProfileId));
        ValidateId(memoryId, nameof(memoryId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE role_memories
                SET superseded_at = $superseded_at, updated_at = $superseded_at
                WHERE workspace_id = $workspace_id
                  AND role_profile_id = $role_profile_id
                  AND memory_id = $memory_id
                  AND current_revision = $expected_revision
                  AND superseded_at IS NULL
                """;
            command.Parameters.AddWithValue("$superseded_at", _now().ToString("O"));
            command.Parameters.AddWithValue("$workspace_id", workspaceId);
            command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
            command.Parameters.AddWithValue("$memory_id", memoryId);
            command.Parameters.AddWithValue("$expected_revision", expectedRevision);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> RestoreAsync(
        string workspaceId,
        string roleProfileId,
        string memoryId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        ValidateId(roleProfileId, nameof(roleProfileId));
        ValidateId(memoryId, nameof(memoryId));
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE role_memories
                SET superseded_at = NULL, updated_at = $updated_at
                WHERE workspace_id = $workspace_id
                  AND role_profile_id = $role_profile_id
                  AND memory_id = $memory_id
                  AND current_revision = $expected_revision
                  AND superseded_at IS NOT NULL
                """;
            command.Parameters.AddWithValue("$updated_at", _now().ToString("O"));
            command.Parameters.AddWithValue("$workspace_id", workspaceId);
            command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
            command.Parameters.AddWithValue("$memory_id", memoryId);
            command.Parameters.AddWithValue("$expected_revision", expectedRevision);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<RoleMemoryCandidate> ProposeCandidateAsync(
        RoleMemoryCandidateDraft draft,
        CancellationToken cancellationToken = default)
    {
        ValidateCandidateDraft(draft);
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = _now();
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_candidates(
                    candidate_id, workspace_id, role_profile_id, source_meeting_id,
                    source_event_id, memory_kind, protected_content, confidence,
                    status, decision_revision, created_at, updated_at)
                VALUES(
                    $candidate_id, $workspace_id, $role_profile_id, $source_meeting_id,
                    $source_event_id, $memory_kind, $protected_content, $confidence,
                    'pending', 1, $created_at, $updated_at)
                """;
            command.Parameters.AddWithValue("$candidate_id", draft.CandidateId);
            command.Parameters.AddWithValue("$workspace_id", draft.WorkspaceId);
            command.Parameters.AddWithValue("$role_profile_id", draft.RoleProfileId);
            command.Parameters.AddWithValue("$source_meeting_id", draft.SourceMeetingId);
            command.Parameters.AddWithValue("$source_event_id", (object?)draft.SourceEventId ?? DBNull.Value);
            command.Parameters.AddWithValue("$memory_kind", KindName(draft.Kind));
            command.Parameters.AddWithValue(
                "$protected_content",
                _protector.Protect(Encoding.UTF8.GetBytes(draft.Content)));
            command.Parameters.AddWithValue("$confidence", (object?)draft.Confidence ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_at", now.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                throw new InvalidDataException("记忆候选标识已存在。", error);
            }
            return new RoleMemoryCandidate(
                draft.CandidateId,
                draft.WorkspaceId,
                draft.RoleProfileId,
                draft.SourceMeetingId,
                draft.SourceEventId,
                draft.Kind,
                draft.Content,
                draft.Confidence,
                RoleMemoryCandidateStatus.Pending,
                1,
                now,
                now);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<RoleMemoryCandidate>> LoadCandidatesAsync(
        string workspaceId,
        string roleProfileId,
        RoleMemoryCandidateStatus? status = null,
        int maximumItems = 64,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        ValidateId(roleProfileId, nameof(roleProfileId));
        if (maximumItems is < 1 or > MaximumLoadItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        }
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id, source_meeting_id, source_event_id, memory_kind,
                   protected_content, confidence, status, decision_revision,
                   created_at, updated_at
            FROM memory_candidates
            WHERE workspace_id = $workspace_id
              AND role_profile_id = $role_profile_id
              AND ($status IS NULL OR status = $status)
            ORDER BY updated_at DESC, candidate_id ASC
            LIMIT $maximum_items
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : CandidateStatusName(status.Value));
        command.Parameters.AddWithValue("$maximum_items", maximumItems);
        var candidates = new List<RoleMemoryCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(ReadCandidate(reader, workspaceId, roleProfileId));
        }
        return candidates;
    }

    public async Task<RoleMemoryCandidateDecision> ReviewCandidateAsync(
        string candidateId,
        int expectedDecisionRevision,
        bool approve,
        CancellationToken cancellationToken = default)
    {
        ValidateId(candidateId, nameof(candidateId));
        if (expectedDecisionRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDecisionRevision));
        }
        await InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var candidate = await ReadCandidateAsync(connection, transaction, candidateId, cancellationToken)
                ?? throw new InvalidDataException("找不到要审核的记忆候选。");
            if (candidate.Status != RoleMemoryCandidateStatus.Pending ||
                candidate.DecisionRevision != expectedDecisionRevision)
            {
                throw new InvalidDataException("记忆候选已被其他审核更新，请刷新后重试。");
            }

            RoleMemoryEntry? approvedMemory = null;
            var now = _now();
            if (approve)
            {
                var memoryDraft = new RoleMemoryDraft(
                    candidate.WorkspaceId,
                    candidate.RoleProfileId,
                    $"memory-{Guid.NewGuid():N}",
                    candidate.Kind,
                    candidate.Content,
                    RoleMemoryWriteAuthority.UserApproved,
                    candidate.SourceMeetingId,
                    candidate.SourceEventId,
                    candidate.Confidence);
                await InsertLogicalMemoryAsync(connection, transaction, memoryDraft, 1, now, cancellationToken);
                await InsertRevisionAsync(connection, transaction, memoryDraft, 1, now, cancellationToken);
                approvedMemory = ToEntry(memoryDraft, 1, now, now, null);
            }

            var nextRevision = checked(expectedDecisionRevision + 1);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE memory_candidates
                    SET status = $status,
                        decision_revision = $next_revision,
                        updated_at = $updated_at
                    WHERE candidate_id = $candidate_id
                      AND status = 'pending'
                      AND decision_revision = $expected_revision
                    """;
                update.Parameters.AddWithValue("$status", approve ? "approved" : "rejected");
                update.Parameters.AddWithValue("$next_revision", nextRevision);
                update.Parameters.AddWithValue("$updated_at", now.ToString("O"));
                update.Parameters.AddWithValue("$candidate_id", candidateId);
                update.Parameters.AddWithValue("$expected_revision", expectedDecisionRevision);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidDataException("记忆候选审核发生并发冲突。");
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return new RoleMemoryCandidateDecision(
                candidate with
                {
                    Status = approve ? RoleMemoryCandidateStatus.Approved : RoleMemoryCandidateStatus.Rejected,
                    DecisionRevision = nextRevision,
                    UpdatedAt = now,
                },
                approvedMemory);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<RoleMemoryRecallFreeze> FreezeRecallAsync(
        string workspaceId,
        string roleProfileId,
        string meetingId,
        ulong runtimeGeneration,
        CancellationToken cancellationToken = default)
    {
        ValidateId(meetingId, nameof(meetingId));
        if (runtimeGeneration == 0 || runtimeGeneration > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }
        var active = await LoadActiveAsync(workspaceId, roleProfileId, 32, cancellationToken);
        var considered = active.Select(RevisionRef).ToArray();
        var selected = new List<RoleMemoryRecallItem>(4);
        var characters = 0;
        foreach (var entry in active)
        {
            if (selected.Count == 4 || characters + entry.Content.Length > 6_000)
            {
                continue;
            }
            selected.Add(new RoleMemoryRecallItem(entry.MemoryId, entry.Revision, entry.Content));
            characters += entry.Content.Length;
        }
        var auditId = $"recall-{Guid.NewGuid():N}";
        await RecordRecallAuditAsync(
            auditId,
            workspaceId,
            roleProfileId,
            meetingId,
            runtimeGeneration,
            considered,
            selected.Select(item => $"{item.MemoryId}@{item.Revision}").ToArray(),
            cancellationToken);
        return new RoleMemoryRecallFreeze(auditId, considered, selected);
    }

    private async Task RecordRecallAuditAsync(
        string auditId,
        string workspaceId,
        string roleProfileId,
        string meetingId,
        ulong runtimeGeneration,
        IReadOnlyList<string> considered,
        IReadOnlyList<string> selected,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_recall_audits(
                    audit_id, meeting_id, workspace_id, role_profile_id,
                    runtime_generation, considered_refs, selected_refs,
                    injected_refs, created_at)
                VALUES(
                    $audit_id, $meeting_id, $workspace_id, $role_profile_id,
                    $runtime_generation, $considered_refs, $selected_refs,
                    $injected_refs, $created_at)
                """;
            command.Parameters.AddWithValue("$audit_id", auditId);
            command.Parameters.AddWithValue("$meeting_id", meetingId);
            command.Parameters.AddWithValue("$workspace_id", workspaceId);
            command.Parameters.AddWithValue("$role_profile_id", roleProfileId);
            command.Parameters.AddWithValue("$runtime_generation", (long)runtimeGeneration);
            command.Parameters.AddWithValue("$considered_refs", JsonSerializer.Serialize(considered));
            command.Parameters.AddWithValue("$selected_refs", JsonSerializer.Serialize(selected));
            command.Parameters.AddWithValue("$injected_refs", "[]");
            command.Parameters.AddWithValue("$created_at", _now().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MarkRecallInjectedAsync(
        string auditId,
        IReadOnlyList<string> selectedRevisionRefs,
        CancellationToken cancellationToken = default)
    {
        ValidateId(auditId, nameof(auditId));
        ArgumentNullException.ThrowIfNull(selectedRevisionRefs);
        if (selectedRevisionRefs.Count > 4 || selectedRevisionRefs.Any(item => item.Length is < 3 or > 260))
        {
            throw new ArgumentException("记忆召回引用无效。", nameof(selectedRevisionRefs));
        }
        await InitializeAsync(cancellationToken);
        var serialized = JsonSerializer.Serialize(selectedRevisionRefs);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE memory_recall_audits
                SET injected_refs = $selected_refs
                WHERE audit_id = $audit_id
                  AND selected_refs = $selected_refs
                  AND injected_refs = '[]'
                """;
            command.Parameters.AddWithValue("$audit_id", auditId);
            command.Parameters.AddWithValue("$selected_refs", serialized);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException("记忆召回审计已变化或与冻结集合不一致。");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string RevisionRef(RoleMemoryEntry entry) =>
        $"{entry.MemoryId}@{entry.Revision}";

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
            await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<IReadOnlyList<RoleMemoryEntry>> ReadEntriesAsync(
        SqliteCommand command,
        string workspaceId,
        string roleProfileId,
        CancellationToken cancellationToken)
    {
        var entries = new List<RoleMemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var content = UnprotectContent((byte[])reader[3]);
            entries.Add(new RoleMemoryEntry(
                workspaceId,
                roleProfileId,
                reader.GetString(0),
                ParseKind(reader.GetString(1)),
                reader.GetInt32(2),
                content,
                ParseAuthority(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(10)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return entries;
    }

    private string UnprotectContent(byte[] ciphertext)
    {
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("当前 Windows 用户无法解密角色记忆。", error);
        }
    }

    private async Task<RoleMemoryCandidate?> ReadCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT workspace_id, role_profile_id, source_meeting_id, source_event_id,
                   memory_kind, protected_content, confidence, status,
                   decision_revision, created_at, updated_at
            FROM memory_candidates
            WHERE candidate_id = $candidate_id
            """;
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var workspaceId = reader.GetString(0);
        var roleProfileId = reader.GetString(1);
        return new RoleMemoryCandidate(
            candidateId,
            workspaceId,
            roleProfileId,
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseKind(reader.GetString(4)),
            UnprotectContent((byte[])reader[5]),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            ParseCandidateStatus(reader.GetString(7)),
            reader.GetInt32(8),
            DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private RoleMemoryCandidate ReadCandidate(
        SqliteDataReader reader,
        string workspaceId,
        string roleProfileId) => new(
            reader.GetString(0),
            workspaceId,
            roleProfileId,
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseKind(reader.GetString(3)),
            UnprotectContent((byte[])reader[4]),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            ParseCandidateStatus(reader.GetString(6)),
            reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind));

    private static async Task<(int Revision, DateTimeOffset CreatedAt, DateTimeOffset? SupersededAt)?>
        ReadLogicalMemoryAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            RoleMemoryDraft draft,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current_revision, created_at, superseded_at
            FROM role_memories
            WHERE workspace_id = $workspace_id
              AND role_profile_id = $role_profile_id
              AND memory_id = $memory_id
            """;
        AddIdentityParameters(command, draft);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (
            reader.GetInt32(0),
            DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static async Task InsertLogicalMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoleMemoryDraft draft,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO role_memories(
                workspace_id, role_profile_id, memory_id, memory_kind,
                current_revision, created_at, updated_at, superseded_at)
            VALUES(
                $workspace_id, $role_profile_id, $memory_id, $memory_kind,
                $revision, $now, $now, NULL)
            """;
        AddIdentityParameters(command, draft);
        command.Parameters.AddWithValue("$memory_kind", KindName(draft.Kind));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateLogicalMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoleMemoryDraft draft,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE role_memories
            SET memory_kind = $memory_kind,
                current_revision = $revision,
                updated_at = $now
            WHERE workspace_id = $workspace_id
              AND role_profile_id = $role_profile_id
              AND memory_id = $memory_id
            """;
        AddIdentityParameters(command, draft);
        command.Parameters.AddWithValue("$memory_kind", KindName(draft.Kind));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoleMemoryDraft draft,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO role_memory_revisions(
                workspace_id, role_profile_id, memory_id, revision,
                protected_content, write_authority, source_meeting_id,
                source_event_id, confidence, created_at)
            VALUES(
                $workspace_id, $role_profile_id, $memory_id, $revision,
                $protected_content, $write_authority, $source_meeting_id,
                $source_event_id, $confidence, $created_at)
            """;
        AddIdentityParameters(command, draft);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue(
            "$protected_content",
            _protector.Protect(Encoding.UTF8.GetBytes(draft.Content)));
        command.Parameters.AddWithValue("$write_authority", AuthorityName(draft.WriteAuthority));
        command.Parameters.AddWithValue("$source_meeting_id", (object?)draft.SourceMeetingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_event_id", (object?)draft.SourceEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", (object?)draft.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RoleMemoryEntry ToEntry(
        RoleMemoryDraft draft,
        int revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? supersededAt) => new(
            draft.WorkspaceId,
            draft.RoleProfileId,
            draft.MemoryId,
            draft.Kind,
            revision,
            draft.Content,
            draft.WriteAuthority,
            draft.SourceMeetingId,
            draft.SourceEventId,
            draft.Confidence,
            createdAt,
            updatedAt,
            supersededAt);

    private static void AddIdentityParameters(SqliteCommand command, RoleMemoryDraft draft)
    {
        command.Parameters.AddWithValue("$workspace_id", draft.WorkspaceId);
        command.Parameters.AddWithValue("$role_profile_id", draft.RoleProfileId);
        command.Parameters.AddWithValue("$memory_id", draft.MemoryId);
    }

    private static void ValidateDraft(RoleMemoryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateId(draft.WorkspaceId, nameof(draft.WorkspaceId));
        ValidateId(draft.RoleProfileId, nameof(draft.RoleProfileId));
        ValidateId(draft.MemoryId, nameof(draft.MemoryId));
        if (string.IsNullOrWhiteSpace(draft.Content) || draft.Content.Length > MaximumContentCharacters)
        {
            throw new ArgumentException(
                $"角色记忆正文必须包含 1 到 {MaximumContentCharacters} 个字符。",
                nameof(draft));
        }
        if (draft.Confidence is < 0 or > 1 || double.IsNaN(draft.Confidence ?? 0))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "角色记忆置信度必须位于 0 到 1。 ");
        }
        if (draft.SourceMeetingId is not null)
        {
            ValidateId(draft.SourceMeetingId, nameof(draft.SourceMeetingId));
        }
        if (draft.SourceEventId is not null)
        {
            ValidateId(draft.SourceEventId, nameof(draft.SourceEventId));
        }
    }

    private static void ValidateCandidateDraft(RoleMemoryCandidateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateId(draft.CandidateId, nameof(draft.CandidateId));
        ValidateId(draft.WorkspaceId, nameof(draft.WorkspaceId));
        ValidateId(draft.RoleProfileId, nameof(draft.RoleProfileId));
        ValidateId(draft.SourceMeetingId, nameof(draft.SourceMeetingId));
        if (draft.SourceEventId is not null)
        {
            ValidateId(draft.SourceEventId, nameof(draft.SourceEventId));
        }
        if (string.IsNullOrWhiteSpace(draft.Content) || draft.Content.Length > MaximumContentCharacters)
        {
            throw new ArgumentException(
                $"角色记忆候选正文必须包含 1 到 {MaximumContentCharacters} 个字符。",
                nameof(draft));
        }
        if (draft.Confidence is < 0 or > 1 || double.IsNaN(draft.Confidence ?? 0))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "角色记忆候选置信度必须位于 0 到 1。");
        }
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !SafeId().IsMatch(value))
        {
            throw new ArgumentException("标识符格式无效。", parameterName);
        }
    }

    private static string KindName(RoleMemoryKind kind) => kind switch
    {
        RoleMemoryKind.Identity => "identity",
        RoleMemoryKind.Preference => "preference",
        RoleMemoryKind.Fact => "fact",
        RoleMemoryKind.Decision => "decision",
        RoleMemoryKind.Lesson => "lesson",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static RoleMemoryKind ParseKind(string value) => value switch
    {
        "identity" => RoleMemoryKind.Identity,
        "preference" => RoleMemoryKind.Preference,
        "fact" => RoleMemoryKind.Fact,
        "decision" => RoleMemoryKind.Decision,
        "lesson" => RoleMemoryKind.Lesson,
        _ => throw new InvalidDataException("本地角色记忆类型无效。"),
    };

    private static string AuthorityName(RoleMemoryWriteAuthority authority) => authority switch
    {
        RoleMemoryWriteAuthority.UserApproved => "user_approved",
        RoleMemoryWriteAuthority.MeetingClosePolicy => "meeting_close_policy",
        RoleMemoryWriteAuthority.AutomaticPolicy => "automatic_policy",
        _ => throw new ArgumentOutOfRangeException(nameof(authority)),
    };

    private static RoleMemoryWriteAuthority ParseAuthority(string value) => value switch
    {
        "user_approved" => RoleMemoryWriteAuthority.UserApproved,
        "meeting_close_policy" => RoleMemoryWriteAuthority.MeetingClosePolicy,
        "automatic_policy" => RoleMemoryWriteAuthority.AutomaticPolicy,
        _ => throw new InvalidDataException("本地角色记忆授权来源无效。"),
    };

    private static string CandidateStatusName(RoleMemoryCandidateStatus status) => status switch
    {
        RoleMemoryCandidateStatus.Pending => "pending",
        RoleMemoryCandidateStatus.Approved => "approved",
        RoleMemoryCandidateStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static RoleMemoryCandidateStatus ParseCandidateStatus(string value) => value switch
    {
        "pending" => RoleMemoryCandidateStatus.Pending,
        "approved" => RoleMemoryCandidateStatus.Approved,
        "rejected" => RoleMemoryCandidateStatus.Rejected,
        _ => throw new InvalidDataException("本地角色记忆候选状态无效。"),
    };

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

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
