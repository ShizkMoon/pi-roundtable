using Microsoft.Data.Sqlite;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class MeetingControllersTests
{
    [TestMethod]
    public async Task Command_gateway_fences_detached_runtime_generations()
    {
        var first = new RecordingRuntime();
        var second = new RecordingRuntime();
        var gateway = new MeetingCommandGateway();
        gateway.Activate(first, 4);

        var receipt = await gateway.SendAsync(
            "meeting.open",
            null,
            null,
            new Dictionary<string, object?>(),
            CancellationToken.None);
        gateway.Activate(second, 5);
        gateway.Deactivate(first);
        await gateway.SendAsync(
            "meeting.close",
            null,
            null,
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.IsTrue(receipt.Accepted);
        Assert.AreEqual<ulong>(5, gateway.RuntimeGeneration);
        Assert.HasCount(1, first.Commands);
        Assert.HasCount(1, second.Commands);
        gateway.Deactivate(second);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => gateway.SendAsync(
            "meeting.open", null, null, new Dictionary<string, object?>(), CancellationToken.None));
    }

    [TestMethod]
    public void Recovery_validation_rejects_checkpoint_tail_mismatch()
    {
        var checkpoint = new MeetingStoreCheckpoint(
            "meeting-1", 3, 2, false, false, DateTimeOffset.UtcNow);
        var events = new[] { Event(sequence: 2, generation: 2) };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            MeetingSessionController.ValidateRecoveryHistory(checkpoint, events));
    }

    [TestMethod]
    public void Recovery_reconciles_an_event_committed_after_the_checkpoint_tail()
    {
        var checkpoint = new MeetingStoreCheckpoint(
            "meeting-1", 1, 2, false, false, DateTimeOffset.UtcNow);
        var events = new[] { Event(1, 2), Event(2, 2) };

        var recovered = MeetingSessionController.ReconcileRecoveryHistory(checkpoint, events);

        Assert.AreEqual<ulong>(2, recovered.Checkpoint!.LastSequence);
        Assert.IsNotNull(recovered.RecoveryNotice);
    }

    [TestMethod]
    public async Task Session_delete_removes_definition_events_and_session_owned_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var sessionStore = new RoundtableSessionStore(root);
            var eventStore = new MeetingEventStore(root);
            var artifactStore = new ArtifactStore(root, quotaBytes: 1024);
            var controller = new SessionLifecycleController(sessionStore, eventStore, artifactStore);
            var session = new RoundtableSessionConfiguration
            {
                SessionId = "session-delete",
                WorkspaceId = "workspace-test",
                Title = "Delete test",
                Phase = "draft",
                Agenda = new SessionAgendaConfiguration { Subject = "Cleanup" },
            };
            await controller.SaveAsync(session, CancellationToken.None);
            await eventStore.InitializeAsync();
            Assert.IsTrue(await eventStore.AppendAsync(Event(1, 1, session.SessionId)));
            await SeedMeetingOwnedRowsAsync(eventStore.DatabasePath, session.SessionId);
            var source = Path.Combine(root, "evidence.md");
            await File.WriteAllTextAsync(source, "bounded evidence");
            var preflight = await new DocumentPipeline().PreflightAsync(source);
            await artifactStore.ImportAsync(source, preflight.Descriptor);
            await artifactStore.BindToMeetingAsync(preflight.Descriptor.ArtifactId, session.SessionId);

            var impact = await controller.GetDeletionImpactAsync(session.SessionId, CancellationToken.None);
            Assert.AreEqual(1L, impact.EventCount);
            Assert.AreEqual(1L, impact.CommandCount);
            Assert.AreEqual(1L, impact.SubagentCount);
            Assert.AreEqual(1L, impact.MemoryCandidateCount);
            Assert.AreEqual(1L, impact.RecallAuditCount);
            Assert.AreEqual(1L, impact.ContextSnapshotCount);
            Assert.AreEqual(1L, impact.RetentionJobCount);
            Assert.AreEqual(1L, impact.ArtifactCount);
            await controller.DeleteAsync(session.SessionId, isRunning: false, CancellationToken.None);

            Assert.IsEmpty(await sessionStore.LoadAllAsync());
            Assert.IsEmpty(await eventStore.LoadEventsAsync(session.SessionId));
            Assert.AreEqual(0L, await CountMeetingOwnedRowsAsync(eventStore.DatabasePath, session.SessionId));
            Assert.AreEqual(0, (await artifactStore.GetUsageAsync()).ArtifactCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Recovery_context_is_bounded_and_preserves_private_audience_isolation()
    {
        var roles = new[]
        {
            new RoleItem("role.alpha", "Alpha", "long_term", "Alpha prompt"),
            new RoleItem("role.beta", "Beta", "long_term", "Beta prompt"),
        };
        var events = new List<RuntimeMeetingEvent>
        {
            ContextEvent(1, "message.published", "public", [], "public constraint?"),
            ContextEvent(2, "message.direct_sent", "private", ["role.alpha"], "private-alpha"),
            ContextEvent(3, "convergence.recorded", "public", [], "decision-one"),
        };
        for (ulong sequence = 4; sequence <= 260; sequence++)
        {
            events.Add(ContextEvent(
                sequence,
                "message.published",
                "public",
                [],
                $"history-{sequence}-" + new string('x', 180)));
        }

        var contexts = new MeetingRecoveryContextBuilder().Build(roles, events);

        StringAssert.Contains(contexts["role.alpha"], "private-alpha");
        Assert.IsFalse(contexts["role.beta"].Contains("private-alpha", StringComparison.Ordinal));
        StringAssert.Contains(contexts["role.beta"], "seq 3 convergence.recorded");
        StringAssert.Contains(contexts["role.beta"], "history-260");
        Assert.IsLessThanOrEqualTo(48_000, contexts["role.beta"].Length);
    }

    [TestMethod]
    public async Task Session_move_validates_runtime_and_group_then_persists_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var sessionStore = new RoundtableSessionStore(root);
            var controller = new SessionLifecycleController(
                sessionStore,
                new MeetingEventStore(root),
                new ArtifactStore(root));
            var session = new RoundtableSessionConfiguration
            {
                SessionId = "session-move",
                WorkspaceId = "workspace-test",
                GroupId = "group-a",
                Title = "Move test",
                Phase = "draft",
                Agenda = new SessionAgendaConfiguration { Subject = "Move" },
            };
            await controller.SaveAsync(session, CancellationToken.None);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.MoveAsync(
                session, "group-b", ["group-a", "group-b"], isRunning: true, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.MoveAsync(
                session, "group-missing", ["group-a", "group-b"], isRunning: false, CancellationToken.None));
            await controller.MoveAsync(
                session, "group-b", ["group-a", "group-b"], isRunning: false, CancellationToken.None);

            Assert.AreEqual("group-b", (await new RoundtableSessionStore(root).LoadAllAsync()).Single().GroupId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RuntimeMeetingEvent Event(
        ulong sequence,
        ulong generation,
        string meetingId = "meeting-1") => new(
        meetingId,
        $"event-{sequence}",
        sequence,
        generation,
        "runtime.lease_acquired",
        DateTimeOffset.UtcNow,
        "runtime.windows",
        null,
        null,
        "public",
        [],
        System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());

    private static RuntimeMeetingEvent ContextEvent(
        ulong sequence,
        string kind,
        string visibility,
        IReadOnlyList<string> audience,
        string text)
    {
        var property = kind == "convergence.recorded" ? "summary" : "message";
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { [property] = text });
        return new RuntimeMeetingEvent(
            "meeting-context",
            $"event-{sequence}",
            sequence,
            2,
            kind,
            DateTimeOffset.UtcNow,
            "user.direct_host",
            null,
            null,
            visibility,
            audience,
            payload);
    }

    private static async Task SeedMeetingOwnedRowsAsync(string databasePath, string meetingId)
    {
        await using var connection = OpenTestConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO command_journal(meeting_id, command_id, fingerprint, status, updated_at)
            VALUES($meeting_id, 'command-delete', 'fingerprint', 'pending', '2026-08-05T00:00:00Z');
            INSERT INTO subagent_runs(meeting_id, subagent_id, parent_role_id, status, result_delivered, protected_state, updated_at)
            VALUES($meeting_id, 'subagent-delete', 'role.alpha', 'running', 0, X'01', '2026-08-05T00:00:00Z');
            INSERT INTO legacy_projections(meeting_id, protected_projection, imported_at)
            VALUES($meeting_id, X'01', '2026-08-05T00:00:00Z');
            INSERT INTO memory_candidates(candidate_id, workspace_id, role_profile_id, source_meeting_id,
                memory_kind, protected_content, status, decision_revision, created_at, updated_at)
            VALUES('candidate-delete', 'workspace-test', 'role.alpha', $meeting_id,
                'Fact', X'01', 'Pending', 0, '2026-08-05T00:00:00Z', '2026-08-05T00:00:00Z');
            INSERT INTO memory_recall_audits(audit_id, meeting_id, workspace_id, role_profile_id,
                runtime_generation, considered_refs, selected_refs, injected_refs, created_at)
            VALUES('audit-delete', $meeting_id, 'workspace-test', 'role.alpha',
                1, '[]', '[]', '[]', '2026-08-05T00:00:00Z');
            INSERT INTO role_context_snapshots(snapshot_id, meeting_id, role_profile_id,
                runtime_generation, source_sequence, policy_version, prefix_fingerprint,
                protected_snapshot, created_at)
            VALUES('snapshot-delete', $meeting_id, 'role.alpha', 1, 1, 'v1',
                'fingerprint', X'01', '2026-08-05T00:00:00Z');
            INSERT INTO memory_retention_jobs(job_id, workspace_id, role_profile_id, source_meeting_id,
                status, not_before, created_at, updated_at)
            VALUES('retention-delete', 'workspace-test', 'role.alpha', $meeting_id,
                'pending', '2026-08-05T00:00:00Z', '2026-08-05T00:00:00Z', '2026-08-05T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountMeetingOwnedRowsAsync(string databasePath, string meetingId)
    {
        await using var connection = OpenTestConnection(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM meeting_events WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM runtime_checkpoints WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM command_journal WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM subagent_runs WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM legacy_projections WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM memory_candidates WHERE source_meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM memory_recall_audits WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM role_context_snapshots WHERE meeting_id = $meeting_id) +
                (SELECT COUNT(*) FROM memory_retention_jobs WHERE source_meeting_id = $meeting_id)
            """;
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }

    private static SqliteConnection OpenTestConnection(string databasePath) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        }.ToString());

    private sealed class RecordingRuntime : IRuntimeHostProcess
    {
        public List<string> Commands { get; } = [];
        public event EventHandler<RuntimeMeetingEvent>? MeetingEventReceived { add { } remove { } }
        public event EventHandler<string>? DiagnosticReceived { add { } remove { } }
        public event EventHandler<string>? EventStreamFaulted { add { } remove { } }

        public Task StartAsync(RuntimeHostStartOptions options, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<RuntimeCommandReceipt> SendCommandAsync(
            string kind,
            string? actorId,
            string? targetId,
            IReadOnlyDictionary<string, object?> payload,
            CancellationToken cancellationToken,
            string? commandId = null)
        {
            Commands.Add(kind);
            return Task.FromResult(new RuntimeCommandReceipt(
                commandId ?? Guid.NewGuid().ToString("N"), "accepted", 1, null, null));
        }

        public Task StopAsync(RuntimeHostShutdownMode mode, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Terminate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
