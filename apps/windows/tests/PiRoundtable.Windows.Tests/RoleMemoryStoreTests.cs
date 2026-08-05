using System.Text;
using Microsoft.Data.Sqlite;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class RoleMemoryStoreTests
{
    [TestMethod]
    public async Task Memory_store_encrypts_content_and_preserves_immutable_revisions()
    {
        var root = TestRoot();
        try
        {
            var firstStore = CreateStore(root);
            var first = await firstStore.AppendRevisionAsync(Draft("Keep answers concise."));
            var second = await firstStore.AppendRevisionAsync(
                Draft("Keep answers concise and cite evidence."),
                expectedRevision: first.Revision);

            var restartedStore = CreateStore(root);
            var active = await restartedStore.LoadActiveAsync("workspace-main", "role.architect");
            Assert.HasCount(1, active);
            Assert.AreEqual(2, active[0].Revision);
            Assert.AreEqual("Keep answers concise and cite evidence.", active[0].Content);

            var history = await restartedStore.LoadHistoryAsync(
                "workspace-main",
                "role.architect",
                "response-style");
            Assert.HasCount(2, history);
            Assert.AreEqual("Keep answers concise.", history[0].Content);
            Assert.AreEqual("meeting-2", history[1].SourceMeetingId);

            foreach (var path in Directory.EnumerateFiles(
                         Path.GetDirectoryName(firstStore.DatabasePath)!,
                         "roundtable.db*"))
            {
                var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
                Assert.IsFalse(text.Contains("Keep answers concise", StringComparison.Ordinal));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Active_memory_is_isolated_by_workspace_and_role_then_soft_deleted()
    {
        var root = TestRoot();
        try
        {
            var store = CreateStore(root);
            var target = await store.AppendRevisionAsync(Draft("Architect memory"));
            await store.AppendRevisionAsync(Draft("Other role memory") with
            {
                RoleProfileId = "role.reviewer",
                MemoryId = "review-style",
            });
            await store.AppendRevisionAsync(Draft("Other workspace memory") with
            {
                WorkspaceId = "workspace-other",
            });

            Assert.HasCount(1, await store.LoadActiveAsync("workspace-main", "role.architect"));
            Assert.IsTrue(await store.SupersedeAsync(
                target.WorkspaceId,
                target.RoleProfileId,
                target.MemoryId,
                target.Revision));
            Assert.IsEmpty(await store.LoadActiveAsync("workspace-main", "role.architect"));
            Assert.HasCount(1, await store.LoadHistoryAsync(
                "workspace-main",
                "role.architect",
                "response-style"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Expected_revision_prevents_lost_updates_across_store_instances()
    {
        var root = TestRoot();
        try
        {
            var firstStore = CreateStore(root);
            var secondStore = CreateStore(root);
            var initial = await firstStore.AppendRevisionAsync(Draft("Initial"));
            await firstStore.AppendRevisionAsync(Draft("Winner"), initial.Revision);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => secondStore.AppendRevisionAsync(Draft("Stale writer"), initial.Revision));
            var active = await secondStore.LoadActiveAsync("workspace-main", "role.architect");
            Assert.AreEqual("Winner", active.Single().Content);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Invalid_memory_is_rejected_before_database_mutation()
    {
        var root = TestRoot();
        try
        {
            var store = CreateStore(root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => store.AppendRevisionAsync(Draft(" ")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => store.AppendRevisionAsync(Draft("valid") with { WorkspaceId = "../escape" }));
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                () => store.AppendRevisionAsync(Draft("valid") with { Confidence = 1.1 }));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Candidate_review_is_revision_fenced_and_approval_is_atomic()
    {
        var root = TestRoot();
        try
        {
            var store = CreateStore(root);
            var candidate = await store.ProposeCandidateAsync(new RoleMemoryCandidateDraft(
                "candidate-1",
                "workspace-main",
                "role.architect",
                "meeting-2",
                "event-7",
                RoleMemoryKind.Decision,
                "Keep the reviewed rollback constraint.",
                0.8));

            Assert.AreEqual(RoleMemoryCandidateStatus.Pending, candidate.Status);
            var decision = await store.ReviewCandidateAsync(
                candidate.CandidateId,
                candidate.DecisionRevision,
                approve: true);

            Assert.AreEqual(RoleMemoryCandidateStatus.Approved, decision.Candidate.Status);
            Assert.IsNotNull(decision.ApprovedMemory);
            Assert.AreEqual(
                "Keep the reviewed rollback constraint.",
                (await store.LoadActiveAsync("workspace-main", "role.architect")).Single().Content);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReviewCandidateAsync(
                candidate.CandidateId,
                candidate.DecisionRevision,
                approve: false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Recall_is_bounded_audited_and_restore_does_not_rewrite_revisions()
    {
        var root = TestRoot();
        try
        {
            var store = CreateStore(root);
            var entry = await store.AppendRevisionAsync(Draft("Active recall"));
            Assert.IsTrue(await store.SupersedeAsync(
                entry.WorkspaceId,
                entry.RoleProfileId,
                entry.MemoryId,
                entry.Revision));
            Assert.IsTrue(await store.RestoreAsync(
                entry.WorkspaceId,
                entry.RoleProfileId,
                entry.MemoryId,
                entry.Revision));

            var frozen = await store.FreezeRecallAsync(
                "workspace-main",
                "role.architect",
                "meeting-2",
                3);

            Assert.HasCount(1, frozen.Selected);
            Assert.AreEqual("response-style@1", frozen.ConsideredRevisionRefs.Single());
            Assert.AreEqual("[]", await ReadInjectedRefsAsync(store.DatabasePath, frozen.AuditId));
            await store.MarkRecallInjectedAsync(
                frozen.AuditId,
                frozen.Selected.Select(item => $"{item.MemoryId}@{item.Revision}").ToArray());
            Assert.AreEqual("[\"response-style@1\"]", await ReadInjectedRefsAsync(store.DatabasePath, frozen.AuditId));
            Assert.HasCount(1, await store.LoadHistoryAsync(
                "workspace-main",
                "role.architect",
                "response-style"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static RoleMemoryStore CreateStore(string root) => new(
        root,
        now: () => new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero));

    private static RoleMemoryDraft Draft(string content) => new(
        "workspace-main",
        "role.architect",
        "response-style",
        RoleMemoryKind.Preference,
        content,
        RoleMemoryWriteAuthority.UserApproved,
        "meeting-2",
        "event-7",
        0.9);

    private static string TestRoot() =>
        Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));

    private static async Task<string> ReadInjectedRefsAsync(string databasePath, string auditId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT injected_refs FROM memory_recall_audits WHERE audit_id = $audit_id";
        command.Parameters.AddWithValue("$audit_id", auditId);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
