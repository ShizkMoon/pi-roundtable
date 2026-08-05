using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class MeetingCommandGateway
{
    private readonly object _gate = new();
    private IRuntimeHostProcess? _runtime;
    private ulong _runtimeGeneration;

    public ulong RuntimeGeneration
    {
        get
        {
            lock (_gate)
            {
                return _runtimeGeneration;
            }
        }
    }

    public void Activate(IRuntimeHostProcess runtime, ulong runtimeGeneration)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtimeGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }
        lock (_gate)
        {
            _runtime = runtime;
            _runtimeGeneration = runtimeGeneration;
        }
    }

    public void Deactivate(IRuntimeHostProcess? runtime = null)
    {
        lock (_gate)
        {
            if (runtime is not null && !ReferenceEquals(_runtime, runtime))
            {
                return;
            }
            _runtime = null;
            _runtimeGeneration = 0;
        }
    }

    public Task<RuntimeCommandReceipt> SendAsync(
        string kind,
        string? actorId,
        string? targetId,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken,
        string? commandId = null)
    {
        IRuntimeHostProcess runtime;
        lock (_gate)
        {
            runtime = _runtime ?? throw new InvalidOperationException("当前没有可接收命令的 Runtime Owner。");
            if (_runtimeGeneration == 0)
            {
                throw new InvalidOperationException("Runtime generation 尚未激活。");
            }
        }
        // RuntimeHostProcess owns command serialization and writes its frozen
        // active generation into every command frame. Callers cannot provide or
        // override that field through payload data.
        return runtime.SendCommandAsync(kind, actorId, targetId, payload, cancellationToken, commandId);
    }
}

internal sealed record MeetingRecoveryState(
    MeetingStoreCheckpoint? Checkpoint,
    IReadOnlyList<RuntimeMeetingEvent> Events,
    string? RecoveryNotice = null)
{
    public bool IsRecovery => Events.Any(meetingEvent => meetingEvent.Kind == "meeting.opened");
}

internal sealed class MeetingSessionController(
    IRuntimeHostFactory runtimeHostFactory,
    IMeetingEventStore eventStore)
{
    private readonly IRuntimeHostFactory _runtimeHostFactory =
        runtimeHostFactory ?? throw new ArgumentNullException(nameof(runtimeHostFactory));
    private readonly IMeetingEventStore _eventStore =
        eventStore ?? throw new ArgumentNullException(nameof(eventStore));

    public async Task<MeetingRecoveryState> LoadRecoveryAsync(
        string meetingId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _eventStore.GetCheckpointAsync(meetingId, cancellationToken);
        var events = await _eventStore.LoadEventsAsync(meetingId, cancellationToken);
        return ReconcileRecoveryHistory(checkpoint, events);
    }

    public IRuntimeHostProcess CreateRuntime() => _runtimeHostFactory.Create(_eventStore);

    internal static MeetingRecoveryState ReconcileRecoveryHistory(
        MeetingStoreCheckpoint? checkpoint,
        IReadOnlyList<RuntimeMeetingEvent> historicalEvents)
    {
        for (var index = 0; index < historicalEvents.Count; index++)
        {
            if (historicalEvents[index].Sequence != (ulong)index + 1)
            {
                throw new InvalidDataException("会议事件历史存在序号缺口，不能安全恢复。");
            }
        }
        if (checkpoint is null)
        {
            if (historicalEvents.Count == 0)
            {
                return new MeetingRecoveryState(null, historicalEvents);
            }
            var recoveredTail = historicalEvents[^1];
            return new MeetingRecoveryState(
                CheckpointFromTail(recoveredTail),
                historicalEvents,
                "恢复检查点缺失；已从权威规范化事件历史重建恢复游标。");
        }
        if (historicalEvents.Count == 0)
        {
            if (checkpoint.LastSequence != 0 || checkpoint.RuntimeGeneration != 0)
            {
                throw new InvalidDataException("会议恢复检查点与空事件历史不一致。");
            }
            return new MeetingRecoveryState(checkpoint, historicalEvents);
        }
        var tail = historicalEvents[^1];
        if (tail.Sequence == checkpoint.LastSequence &&
            tail.RuntimeGeneration == checkpoint.RuntimeGeneration)
        {
            return new MeetingRecoveryState(checkpoint, historicalEvents);
        }
        if (checkpoint.LastSequence < tail.Sequence &&
            (checkpoint.LastSequence == 0 || historicalEvents.Any(meetingEvent =>
                meetingEvent.Sequence == checkpoint.LastSequence &&
                meetingEvent.RuntimeGeneration == checkpoint.RuntimeGeneration)))
        {
            return new MeetingRecoveryState(
                CheckpointFromTail(tail),
                historicalEvents,
                "事件历史领先于恢复检查点；已使用权威事件尾部恢复，未恢复任何 provider 私有会话状态。");
        }
        throw new InvalidDataException("会议恢复检查点与事件历史末尾不一致。");
    }

    internal static void ValidateRecoveryHistory(
        MeetingStoreCheckpoint? checkpoint,
        IReadOnlyList<RuntimeMeetingEvent> historicalEvents) =>
        _ = ReconcileRecoveryHistory(checkpoint, historicalEvents);

    private static MeetingStoreCheckpoint CheckpointFromTail(RuntimeMeetingEvent tail) => new(
        tail.MeetingId,
        tail.Sequence,
        tail.RuntimeGeneration,
        CleanShutdown: false,
        IsClosed: tail.Kind == "meeting.closed",
        UpdatedAt: tail.OccurredAt);
}

internal sealed class MeetingRecoveryContextBuilder
{
    private const int MaximumCharactersPerRole = 48_000;

    public IReadOnlyDictionary<string, string> Build(
        IReadOnlyList<RoleItem> activeRoles,
        IReadOnlyList<RuntimeMeetingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(activeRoles);
        ArgumentNullException.ThrowIfNull(events);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in activeRoles)
        {
            var visible = events.Where(meetingEvent =>
                    meetingEvent.Visibility == "public" ||
                    meetingEvent.Audience.Contains(role.RoleId, StringComparer.Ordinal))
                .ToArray();
            var messageLines = visible
                .Where(meetingEvent =>
                    meetingEvent.Kind is "message.published" or "message.direct_sent" &&
                    meetingEvent.Payload.TryGetProperty("message", out var message) &&
                    message.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(meetingEvent =>
                    $"- seq {meetingEvent.Sequence}: {Bound(meetingEvent.Payload.GetProperty("message").GetString() ?? string.Empty, 240)}")
                .ToArray();
            var constraints = messageLines.TakeLast(12).ToArray();
            var privateHistory = visible
                .Where(meetingEvent => meetingEvent.Visibility == "private" &&
                    meetingEvent.Kind == "message.direct_sent" &&
                    meetingEvent.Payload.TryGetProperty("message", out var message) &&
                    message.ValueKind == System.Text.Json.JsonValueKind.String)
                .TakeLast(8)
                .Select(meetingEvent =>
                    $"- seq {meetingEvent.Sequence}: {Bound(meetingEvent.Payload.GetProperty("message").GetString() ?? string.Empty, 240)}")
                .ToArray();
            var unresolved = messageLines
                .Where(line => line.TrimEnd().EndsWith("?", StringComparison.Ordinal) ||
                               line.TrimEnd().EndsWith("？", StringComparison.Ordinal))
                .TakeLast(12)
                .ToArray();
            var landmarks = visible
                .Where(meetingEvent => meetingEvent.Kind is "agenda.item_changed" or "convergence.recorded")
                .TakeLast(12)
                .Select(meetingEvent =>
                    $"- seq {meetingEvent.Sequence} {meetingEvent.Kind}: {Bound(meetingEvent.Payload.GetRawText(), 300)}")
                .ToArray();

            var transcript = new System.Text.StringBuilder();
            foreach (var meetingEvent in visible)
            {
                switch (meetingEvent.Kind)
                {
                    case "message.published" when meetingEvent.Payload.TryGetProperty("message", out var publicMessage):
                    case "message.direct_sent" when meetingEvent.Payload.TryGetProperty("message", out publicMessage):
                        transcript.Append("\n[user seq ").Append(meetingEvent.Sequence).Append("] ")
                            .Append(publicMessage.GetString());
                        break;
                    case "speech.started":
                        transcript.Append("\n[").Append(meetingEvent.ActorId ?? "role")
                            .Append(" seq ").Append(meetingEvent.Sequence).Append("] ");
                        break;
                    case "speech.delta" when meetingEvent.Payload.TryGetProperty("delta", out var delta):
                        transcript.Append(delta.GetString());
                        break;
                    case "agenda.item_changed":
                    case "convergence.recorded":
                        transcript.Append("\n[").Append(meetingEvent.Kind).Append(" seq ")
                            .Append(meetingEvent.Sequence).Append("] ")
                            .Append(Bound(meetingEvent.Payload.GetRawText(), 2_048));
                        break;
                }
            }

            var historyIndex = new System.Text.StringBuilder();
            var indexedEvents = visible.Where(meetingEvent => meetingEvent.Kind is
                    "message.published" or "message.direct_sent" or "speech.completed" or
                    "agenda.item_changed" or "convergence.recorded")
                .ToArray();
            var blockSize = Math.Max(1, (int)Math.Ceiling(indexedEvents.Length / 16d));
            foreach (var block in indexedEvents.Chunk(blockSize))
            {
                var first = block[0];
                var last = block[^1];
                var line = block.Length == 1
                    ? $"- seq {first.Sequence} event {first.EventId} {first.Kind}: {EventExcerpt(first)}"
                    : $"- seq {first.Sequence}-{last.Sequence}, {block.Length} visible events; first {first.Kind}: {EventExcerpt(first)}; last {last.Kind}: {EventExcerpt(last)}";
                if (historyIndex.Length + line.Length + 1 > 6_000)
                {
                    break;
                }
                historyIndex.AppendLine(line);
            }
            var transcriptText = transcript.ToString();
            var recent = transcriptText.Length <= 28_000
                ? transcriptText
                : transcriptText[^28_000..];
            var context = new System.Text.StringBuilder()
                .AppendLine("Recovered normalized meeting context (not a restored provider/Pi session).")
                .Append("Visible source sequence: ")
                .Append(visible.FirstOrDefault()?.Sequence ?? 0)
                .Append('-')
                .Append(visible.LastOrDefault()?.Sequence ?? 0)
                .AppendLine(".")
                .AppendLine("Older visible event index (authoritative events remain in local history):")
                .AppendLine(historyIndex.Length == 0 ? "- none recorded" : historyIndex.ToString())
                .AppendLine("Key user constraints retained from visible history:")
                .AppendLine(constraints.Length == 0 ? "- none recorded" : string.Join("\n", constraints))
                .AppendLine("Recent private user context for this role only:")
                .AppendLine(privateHistory.Length == 0 ? "- none recorded" : string.Join("\n", privateHistory))
                .AppendLine("Decisions and agenda landmarks with source sequence:")
                .AppendLine(landmarks.Length == 0 ? "- none recorded" : string.Join("\n", landmarks))
                .AppendLine("Unresolved user questions:")
                .AppendLine(unresolved.Length == 0 ? "- none recorded" : string.Join("\n", unresolved))
                .AppendLine("Recent visible transcript:")
                .Append(recent)
                .ToString();
            result[role.RoleId] = context.Length <= MaximumCharactersPerRole
                ? context
                : context[..MaximumCharactersPerRole];
        }
        return result;
    }

    private static string Bound(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string EventExcerpt(RuntimeMeetingEvent meetingEvent)
    {
        if (meetingEvent.Kind is "message.published" or "message.direct_sent" &&
            meetingEvent.Payload.TryGetProperty("message", out var message))
        {
            return Bound(message.GetString() ?? string.Empty, 96);
        }
        return Bound(meetingEvent.Payload.GetRawText(), 96);
    }
}

internal sealed class UnsupportedMeetingEventException(RuntimeMeetingEvent meetingEvent)
    : InvalidOperationException(
        $"Runtime Host 返回了当前客户端尚不支持的事件 {meetingEvent.Kind}（序号 {meetingEvent.Sequence}）。游标已保留，会议已安全暂停；请升级客户端后恢复。")
{
    public RuntimeMeetingEvent MeetingEvent { get; } = meetingEvent;
}

internal sealed class MeetingProjectionController(
    IMeetingCoreFactory meetingCoreFactory,
    IMeetingEventStore eventStore) : IDisposable
{
    private readonly IMeetingCoreFactory _meetingCoreFactory =
        meetingCoreFactory ?? throw new ArgumentNullException(nameof(meetingCoreFactory));
    private readonly IMeetingEventStore _eventStore =
        eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    private IMeetingCoreSession? _core;

    public void Begin()
    {
        _core?.Dispose();
        _core = _meetingCoreFactory.Create();
    }

    public RuntimeMeetingEvent? Replay(IReadOnlyList<RuntimeMeetingEvent> events)
    {
        var core = _core ?? throw new InvalidOperationException("会议投影尚未初始化。");
        foreach (var meetingEvent in events)
        {
            if (!core.SupportsEventKind(meetingEvent.Kind))
            {
                return meetingEvent;
            }
            core.Apply(meetingEvent);
        }
        return null;
    }

    public async Task AcceptAsync(RuntimeMeetingEvent meetingEvent, CancellationToken cancellationToken)
    {
        var core = _core;
        if (core is not null && !core.SupportsEventKind(meetingEvent.Kind))
        {
            if (!await _eventStore.AppendAsync(meetingEvent, cancellationToken))
            {
                throw new InvalidDataException("未知事件与本地历史中的事件标识或序号冲突。");
            }
            throw new UnsupportedMeetingEventException(meetingEvent);
        }
        core?.Apply(meetingEvent);
        try
        {
            if (!await _eventStore.AppendAsync(meetingEvent, cancellationToken))
            {
                throw new InvalidDataException("Runtime Host 事件与本地历史中的事件标识或序号冲突。");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "会议事件已通过本地 reducer，但持久化失败；投影已封闭并需要从权威事件日志重建。",
                error);
        }
    }

    public void Reset()
    {
        _core?.Dispose();
        _core = null;
    }

    public void Dispose() => Reset();
}

internal sealed class SessionLifecycleController(
    RoundtableSessionStore sessionStore,
    IMeetingEventStore eventStore,
    IArtifactStore artifactStore)
{
    private readonly RoundtableSessionStore _sessionStore =
        sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    private readonly IMeetingEventStore _eventStore =
        eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    private readonly IArtifactStore _artifactStore =
        artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<string>> RecoverPendingDeletesAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (sessionId, ticket) in _sessionStore.GetPendingDeletes())
            {
                try
                {
                    await _eventStore.DeleteMeetingAsync(sessionId, cancellationToken);
                    await _artifactStore.DeleteMeetingAsync(sessionId, cancellationToken);
                    RoundtableSessionStore.CompleteDelete(ticket);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    diagnostics.Add("一个已删除会话仍有本地清理待重试；其他会话未受影响。");
                }
            }
            return diagnostics;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        RoundtableSessionConfiguration session,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _sessionStore.SaveAsync(session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MoveAsync(
        RoundtableSessionConfiguration session,
        string targetGroupId,
        IReadOnlyCollection<string> existingGroupIds,
        bool isRunning,
        CancellationToken cancellationToken)
    {
        if (isRunning)
        {
            throw new InvalidOperationException("运行中的会话不能移动；请先暂停或结束会议。");
        }
        if (!existingGroupIds.Contains(targetGroupId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("目标会话组不存在或已被删除。");
        }
        await _gate.WaitAsync(cancellationToken);
        var previousGroupId = session.GroupId;
        try
        {
            session.GroupId = targetGroupId;
            await _sessionStore.SaveAsync(session, cancellationToken);
        }
        catch
        {
            session.GroupId = previousGroupId;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MeetingDeletionImpact> GetDeletionImpactAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var impact = await _eventStore.GetDeletionImpactAsync(sessionId, cancellationToken);
        return impact with
        {
            ArtifactCount = await _artifactStore.GetMeetingArtifactCountAsync(
                sessionId,
                cancellationToken),
        };
    }

    public async Task<bool> DeleteAsync(
        string sessionId,
        bool isRunning,
        CancellationToken cancellationToken)
    {
        if (isRunning)
        {
            throw new InvalidOperationException("运行中的会话不能删除；请先暂停或结束会议。");
        }
        await _gate.WaitAsync(cancellationToken);
        string? ticket = null;
        var databaseCommitted = false;
        try
        {
            ticket = _sessionStore.StageDelete(sessionId);
            await _eventStore.DeleteMeetingAsync(sessionId, cancellationToken);
            databaseCommitted = true;
            await _artifactStore.DeleteMeetingAsync(sessionId, cancellationToken);
            RoundtableSessionStore.CompleteDelete(ticket);
            return true;
        }
        catch when (databaseCommitted)
        {
            // The staged definition remains hidden and is an idempotent durable
            // cleanup ticket. The UI can remove the session immediately while
            // startup recovery finishes the platform-data cleanup.
            return false;
        }
        catch
        {
            if (!databaseCommitted && ticket is not null)
            {
                _sessionStore.RollbackDelete(sessionId, ticket);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
