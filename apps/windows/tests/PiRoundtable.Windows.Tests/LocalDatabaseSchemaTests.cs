using Microsoft.Data.Sqlite;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class LocalDatabaseSchemaTests
{
    private static readonly string[] Version3TableNames =
    [
        "command_journal",
        "legacy_projections",
        "meeting_events",
        "memory_candidates",
        "memory_recall_audits",
        "memory_retention_jobs",
        "role_context_snapshots",
        "role_memories",
        "role_memory_revisions",
        "runtime_checkpoints",
        "schema_info",
        "subagent_runs",
    ];

    [TestMethod]
    public async Task Fresh_initialization_creates_only_the_reviewed_version_3_schema()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));

            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);

            Assert.AreEqual(3L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
            CollectionAssert.AreEqual(Version3TableNames, await ReadUserTableNamesAsync(connection));
            CollectionAssert.AreEqual(
                new[]
                {
                    "command_journal|meeting_id:TEXT:required:pk1:hidden0,command_id:TEXT:required:pk2:hidden0,fingerprint:TEXT:required:pk0:hidden0,status:TEXT:required:pk0:hidden0,sequence:INTEGER:optional:pk0:hidden0,protected_receipt:BLOB:optional:pk0:hidden0,updated_at:TEXT:required:pk0:hidden0",
                    "legacy_projections|meeting_id:TEXT:optional:pk1:hidden0,protected_projection:BLOB:required:pk0:hidden0,imported_at:TEXT:required:pk0:hidden0",
                    "meeting_events|meeting_id:TEXT:required:pk1:hidden0,event_id:TEXT:required:pk0:hidden0,sequence:INTEGER:required:pk2:hidden0,runtime_generation:INTEGER:required:pk0:hidden0,event_kind:TEXT:required:pk0:hidden0,occurred_at:TEXT:required:pk0:hidden0,visibility:TEXT:required:pk0:hidden0,protected_event:BLOB:required:pk0:hidden0",
                    "memory_candidates|candidate_id:TEXT:optional:pk1:hidden0,workspace_id:TEXT:required:pk0:hidden0,role_profile_id:TEXT:required:pk0:hidden0,source_meeting_id:TEXT:required:pk0:hidden0,source_event_id:TEXT:optional:pk0:hidden0,memory_kind:TEXT:required:pk0:hidden0,protected_content:BLOB:required:pk0:hidden0,confidence:REAL:optional:pk0:hidden0,status:TEXT:required:pk0:hidden0,decision_revision:INTEGER:required:pk0:hidden0,created_at:TEXT:required:pk0:hidden0,updated_at:TEXT:required:pk0:hidden0",
                    "memory_recall_audits|audit_id:TEXT:optional:pk1:hidden0,meeting_id:TEXT:required:pk0:hidden0,workspace_id:TEXT:required:pk0:hidden0,role_profile_id:TEXT:required:pk0:hidden0,runtime_generation:INTEGER:required:pk0:hidden0,considered_refs:TEXT:required:pk0:hidden0,selected_refs:TEXT:required:pk0:hidden0,injected_refs:TEXT:required:pk0:hidden0,created_at:TEXT:required:pk0:hidden0",
                    "memory_retention_jobs|job_id:TEXT:optional:pk1:hidden0,workspace_id:TEXT:required:pk0:hidden0,role_profile_id:TEXT:optional:pk0:hidden0,source_meeting_id:TEXT:optional:pk0:hidden0,status:TEXT:required:pk0:hidden0,not_before:TEXT:required:pk0:hidden0,created_at:TEXT:required:pk0:hidden0,updated_at:TEXT:required:pk0:hidden0",
                    "role_context_snapshots|snapshot_id:TEXT:optional:pk1:hidden0,meeting_id:TEXT:required:pk0:hidden0,role_profile_id:TEXT:required:pk0:hidden0,runtime_generation:INTEGER:required:pk0:hidden0,source_sequence:INTEGER:required:pk0:hidden0,policy_version:TEXT:required:pk0:hidden0,prefix_fingerprint:TEXT:required:pk0:hidden0,protected_snapshot:BLOB:required:pk0:hidden0,created_at:TEXT:required:pk0:hidden0",
                    "role_memories|workspace_id:TEXT:required:pk1:hidden0,role_profile_id:TEXT:required:pk2:hidden0,memory_id:TEXT:required:pk3:hidden0,memory_kind:TEXT:required:pk0:hidden0,current_revision:INTEGER:required:pk0:hidden0,created_at:TEXT:required:pk0:hidden0,updated_at:TEXT:required:pk0:hidden0,superseded_at:TEXT:optional:pk0:hidden0",
                    "role_memory_revisions|workspace_id:TEXT:required:pk1:hidden0,role_profile_id:TEXT:required:pk2:hidden0,memory_id:TEXT:required:pk3:hidden0,revision:INTEGER:required:pk4:hidden0,protected_content:BLOB:required:pk0:hidden0,write_authority:TEXT:required:pk0:hidden0,source_meeting_id:TEXT:optional:pk0:hidden0,source_event_id:TEXT:optional:pk0:hidden0,confidence:REAL:optional:pk0:hidden0,created_at:TEXT:required:pk0:hidden0",
                    "runtime_checkpoints|meeting_id:TEXT:optional:pk1:hidden0,last_sequence:INTEGER:required:pk0:hidden0,runtime_generation:INTEGER:required:pk0:hidden0,clean_shutdown:INTEGER:required:pk0:hidden0,is_closed:INTEGER:required:pk0:hidden0,updated_at:TEXT:required:pk0:hidden0",
                    "schema_info|singleton:INTEGER:optional:pk1:hidden0,schema_version:INTEGER:required:pk0:hidden0",
                    "subagent_runs|meeting_id:TEXT:required:pk1:hidden0,subagent_id:TEXT:required:pk2:hidden0,parent_role_id:TEXT:required:pk0:hidden0,status:TEXT:required:pk0:hidden0,result_delivered:INTEGER:required:pk0:hidden0,protected_state:BLOB:required:pk0:hidden0,updated_at:TEXT:required:pk0:hidden0",
                },
                await ReadTableContractsAsync(connection));
            Assert.AreEqual(
                "role_memories",
                await ScalarStringAsync(connection, """
                    SELECT tbl_name FROM sqlite_schema
                    WHERE type = 'index' AND name = 'ix_role_memories_active'
                    """));
            Assert.AreEqual(3L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('role_memory_revisions')"));
            CollectionAssert.AreEqual(
                new[]
                {
                    "command_journal|meeting_id:asc:BINARY,command_id:asc:BINARY|unique|pk|complete",
                    "legacy_projections|meeting_id:asc:BINARY|unique|pk|complete",
                    "meeting_events|event_id:asc:BINARY|unique|u|complete",
                    "meeting_events|meeting_id:asc:BINARY,sequence:asc:BINARY|unique|pk|complete",
                    "memory_candidates|candidate_id:asc:BINARY|unique|pk|complete",
                    "memory_candidates|ix_memory_candidates_review|workspace_id:asc:BINARY,role_profile_id:asc:BINARY,status:asc:BINARY,updated_at:desc:BINARY|nonunique|c|complete",
                    "memory_recall_audits|audit_id:asc:BINARY|unique|pk|complete",
                    "memory_recall_audits|ix_memory_recall_meeting|meeting_id:asc:BINARY,role_profile_id:asc:BINARY,runtime_generation:asc:BINARY|nonunique|c|complete",
                    "memory_retention_jobs|ix_memory_retention_due|status:asc:BINARY,not_before:asc:BINARY|nonunique|c|complete",
                    "memory_retention_jobs|job_id:asc:BINARY|unique|pk|complete",
                    "role_context_snapshots|ix_role_context_restore|meeting_id:asc:BINARY,role_profile_id:asc:BINARY,runtime_generation:asc:BINARY,source_sequence:asc:BINARY|unique|c|complete",
                    "role_context_snapshots|snapshot_id:asc:BINARY|unique|pk|complete",
                    "role_memories|ix_role_memories_active|workspace_id:asc:BINARY,role_profile_id:asc:BINARY,superseded_at:asc:BINARY,updated_at:desc:BINARY|nonunique|c|complete",
                    "role_memories|workspace_id:asc:BINARY,role_profile_id:asc:BINARY,memory_id:asc:BINARY|unique|pk|complete",
                    "role_memory_revisions|workspace_id:asc:BINARY,role_profile_id:asc:BINARY,memory_id:asc:BINARY,revision:asc:BINARY|unique|pk|complete",
                    "runtime_checkpoints|meeting_id:asc:BINARY|unique|pk|complete",
                    "subagent_runs|meeting_id:asc:BINARY,subagent_id:asc:BINARY|unique|pk|complete",
                },
                await ReadIndexContractsAsync(connection));
            Assert.AreEqual(0L, await ScalarInt64Async(connection, """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE type = 'table' AND name IN ('platform_schema_info', 'platform_migration_journal')
                """));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Version_1_migration_and_version_3_reinitialization_preserve_existing_rows()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await CreateVersion1FixtureAsync(connection, includeSeedRows: true);
            var version1Rows = await ReadVersion1RowSnapshotAsync(connection);

            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);

            CollectionAssert.AreEqual(version1Rows, await ReadVersion1RowSnapshotAsync(connection));
            Assert.AreEqual(3L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
            Assert.AreEqual("event-preserved", await ScalarStringAsync(
                connection,
                "SELECT event_id FROM meeting_events WHERE meeting_id = 'meeting-preserved'"));
            Assert.AreEqual("command-preserved", await ScalarStringAsync(
                connection,
                "SELECT command_id FROM command_journal WHERE meeting_id = 'meeting-preserved'"));
            Assert.AreEqual(4L, await ScalarInt64Async(
                connection,
                "SELECT last_sequence FROM runtime_checkpoints WHERE meeting_id = 'meeting-preserved'"));
            Assert.AreEqual("subagent-preserved", await ScalarStringAsync(
                connection,
                "SELECT subagent_id FROM subagent_runs WHERE meeting_id = 'meeting-preserved'"));
            Assert.AreEqual(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM legacy_projections WHERE meeting_id = 'meeting-preserved'"));

            await ExecuteAsync(connection, """
                INSERT INTO role_memories(
                    workspace_id, role_profile_id, memory_id, memory_kind,
                    current_revision, created_at, updated_at, superseded_at)
                VALUES ('workspace', 'role.architect', 'memory-1', 'preference', 1,
                    '2026-08-04T00:00:00Z', '2026-08-04T00:00:00Z', NULL);
                INSERT INTO role_memory_revisions(
                    workspace_id, role_profile_id, memory_id, revision,
                    protected_content, write_authority, source_meeting_id,
                    source_event_id, confidence, created_at)
                VALUES ('workspace', 'role.architect', 'memory-1', 1,
                    X'010203', 'user_approved', 'meeting-preserved',
                    'event-preserved', 0.9, '2026-08-04T00:00:00Z');
                """);
            var version2Rows = await ReadVersion2RowSnapshotAsync(connection);

            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);

            CollectionAssert.AreEqual(version2Rows, await ReadVersion2RowSnapshotAsync(connection));
            Assert.AreEqual(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM role_memories WHERE memory_id = 'memory-1'"));
            Assert.AreEqual(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM role_memory_revisions WHERE memory_id = 'memory-1'"));
            Assert.AreEqual("event-preserved", await ScalarStringAsync(
                connection,
                "SELECT event_id FROM meeting_events WHERE meeting_id = 'meeting-preserved'"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Unsupported_future_version_is_rejected_without_rewriting_metadata()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);
            await ExecuteAsync(connection, "UPDATE schema_info SET schema_version = 999 WHERE singleton = 1");

            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));

            StringAssert.Contains(error.Message, "999");
            Assert.AreEqual(999L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
            CollectionAssert.AreEqual(Version3TableNames, await ReadUserTableNamesAsync(connection));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow("CREATE TABLE schema_info(singleton INTEGER, schema_version INTEGER);")]
    [DataRow("CREATE TABLE schema_info(singleton INTEGER, schema_version TEXT); INSERT INTO schema_info VALUES(1, 'broken');")]
    [DataRow("CREATE TABLE schema_info(singleton INTEGER, wrong_version INTEGER); INSERT INTO schema_info VALUES(1, 2);")]
    [DataRow("CREATE TABLE schema_info(singleton INTEGER, schema_version INTEGER); INSERT INTO schema_info VALUES(1, 2); INSERT INTO schema_info VALUES(2, 2);")]
    public async Task Corrupt_schema_metadata_fails_closed(string schemaSql)
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await ExecuteAsync(connection, schemaSql);
            var schemaBefore = await ReadSchemaSqlAsync(connection);
            var rowsBefore = await ReadAllRowsAsync(connection, "schema_info");

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));

            Assert.HasCount(1, await ReadUserTableNamesAsync(connection));
            CollectionAssert.AreEqual(schemaBefore, await ReadSchemaSqlAsync(connection));
            CollectionAssert.AreEqual(rowsBefore, await ReadAllRowsAsync(connection, "schema_info"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Unversioned_business_table_is_rejected_without_inferred_ownership()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await ExecuteAsync(connection, "CREATE TABLE orphan_business_data(id TEXT PRIMARY KEY)");
            var schemaBefore = await ReadSchemaSqlAsync(connection);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));

            CollectionAssert.AreEqual(schemaBefore, await ReadSchemaSqlAsync(connection));
            CollectionAssert.AreEqual(
                new[] { "orphan_business_data" },
                await ReadUserTableNamesAsync(connection));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Pre_cancelled_initialization_does_not_create_schema()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, cancellation.Token));

            Assert.HasCount(0, await ReadUserTableNamesAsync(connection));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Declared_version_3_with_a_changed_table_shape_is_rejected()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);
            await ExecuteAsync(connection, "ALTER TABLE role_memories ADD COLUMN unexpected TEXT");
            var schemaBefore = await ReadSchemaSqlAsync(connection);

            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));

            StringAssert.Contains(error.Message, "role_memories");
            Assert.AreEqual(3L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
            CollectionAssert.AreEqual(schemaBefore, await ReadSchemaSqlAsync(connection));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow("ALTER TABLE role_memories ADD COLUMN hidden_value TEXT GENERATED ALWAYS AS (memory_id) VIRTUAL;")]
    [DataRow("CREATE INDEX unexpected_event_kind ON meeting_events(event_kind);")]
    [DataRow("CREATE UNIQUE INDEX unexpected_partial ON meeting_events(event_id) WHERE sequence > 0;")]
    [DataRow("CREATE VIEW unexpected_memory_view AS SELECT memory_id FROM role_memories;")]
    [DataRow("CREATE TRIGGER unexpected_memory_trigger AFTER INSERT ON role_memories BEGIN SELECT 1; END;")]
    [DataRow("DROP INDEX ix_role_memories_active; CREATE INDEX ix_role_memories_active ON role_memories(workspace_id, role_profile_id, superseded_at, updated_at ASC);")]
    public async Task Unreviewed_hidden_columns_schema_objects_and_index_semantics_fail_closed(string mutationSql)
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);
            await ExecuteAsync(connection, mutationSql);
            var schemaBefore = await ReadSchemaSqlAsync(connection);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));

            CollectionAssert.AreEqual(schemaBefore, await ReadSchemaSqlAsync(connection));
            Assert.AreEqual(3L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Changed_role_memory_foreign_key_action_fails_closed()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);
            await ExecuteAsync(connection, """
                DROP TABLE role_memory_revisions;
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
                        ON UPDATE CASCADE ON DELETE RESTRICT
                );
                """);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));

            Assert.AreEqual("CASCADE", await ScalarStringAsync(connection, """
                SELECT on_update FROM pragma_foreign_key_list('role_memory_revisions') LIMIT 1
                """));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Failed_migration_rolls_back_new_tables_version_and_existing_rows()
    {
        var root = TestRoot();
        try
        {
            await using var connection = await OpenConnectionAsync(DatabasePath(root));
            await CreateVersion1FixtureAsync(connection, includeSeedRows: true);
            var schemaBefore = await ReadSchemaSqlAsync(connection);
            var rowsBefore = await ReadVersion1RowSnapshotAsync(connection);
            var previousColumnLimit = SQLitePCL.raw.sqlite3_limit(
                connection.Handle,
                SQLitePCL.raw.SQLITE_LIMIT_COLUMN,
                9);

            try
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None));
            }
            finally
            {
                SQLitePCL.raw.sqlite3_limit(
                    connection.Handle,
                    SQLitePCL.raw.SQLITE_LIMIT_COLUMN,
                    previousColumnLimit);
            }

            Assert.AreEqual(1L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
            Assert.AreEqual(0L, await ScalarInt64Async(connection, """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE type = 'table' AND name IN ('role_memories', 'role_memory_revisions')
                """));
            CollectionAssert.AreEqual(schemaBefore, await ReadSchemaSqlAsync(connection));
            CollectionAssert.AreEqual(rowsBefore, await ReadVersion1RowSnapshotAsync(connection));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Event_and_memory_stores_serialize_concurrent_initialization_of_one_database()
    {
        var root = TestRoot();
        try
        {
            var initializers = Enumerable.Range(0, 16)
                .Select(index => index % 2 == 0
                    ? new MeetingEventStore(root).InitializeAsync()
                    : new RoleMemoryStore(root).InitializeAsync())
                .ToArray();

            await Task.WhenAll(initializers).WaitAsync(TimeSpan.FromSeconds(15));

            await using var connection = await OpenConnectionAsync(
                Path.Combine(root, "data", "roundtable.db"));
            Assert.AreEqual(3L, await ScalarInt64Async(connection, "SELECT schema_version FROM schema_info"));
            CollectionAssert.AreEqual(Version3TableNames, await ReadUserTableNamesAsync(connection));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Independent_connections_can_initialize_one_new_database_concurrently()
    {
        var root = TestRoot();
        var connections = new List<SqliteConnection>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                connections.Add(await OpenConnectionAsync(DatabasePath(root)));
            }
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initializers = connections.Select(async connection =>
            {
                await start.Task;
                await LocalDatabaseSchema.InitializeAsync(connection, CancellationToken.None);
            }).ToArray();

            start.SetResult();
            await Task.WhenAll(initializers).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.AreEqual(3L, await ScalarInt64Async(connections[0], "SELECT schema_version FROM schema_info"));
            CollectionAssert.AreEqual(Version3TableNames, await ReadUserTableNamesAsync(connections[0]));
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Waiting_for_an_external_schema_writer_is_bounded_and_cancellable()
    {
        var root = TestRoot();
        try
        {
            await using var owner = await OpenConnectionAsync(DatabasePath(root));
            await using var blocked = await OpenConnectionAsync(
                DatabasePath(root),
                defaultTimeoutSeconds: 9);
            await using (var transaction = owner.BeginTransaction(deferred: false))
            {
                using var cancellation = new CancellationTokenSource();
                var startedAt = System.Diagnostics.Stopwatch.StartNew();
                var initialization = LocalDatabaseSchema.InitializeAsync(blocked, cancellation.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                cancellation.Cancel();

                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    initialization.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.IsLessThan(
                    TimeSpan.FromSeconds(3),
                    startedAt.Elapsed,
                    "Schema lock cancellation must not inherit an unbounded connection timeout.");
                Assert.AreEqual(9, blocked.DefaultTimeout);
                Assert.AreEqual(9_000L, await ScalarInt64Async(blocked, "PRAGMA busy_timeout"));
            }

            await LocalDatabaseSchema.InitializeAsync(blocked, CancellationToken.None);
            Assert.AreEqual(3L, await ScalarInt64Async(blocked, "SELECT schema_version FROM schema_info"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Store_initialization_applies_the_bounded_lock_policy_before_wal_pragmas()
    {
        var root = TestRoot();
        try
        {
            var store = new MeetingEventStore(root);
            await using var owner = await OpenConnectionAsync(store.DatabasePath);
            await using (var transaction = owner.BeginTransaction(deferred: false))
            {
                using var cancellation = new CancellationTokenSource();
                var initialization = Task.Run(() => store.InitializeAsync(cancellation.Token));
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                cancellation.Cancel();

                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    initialization.WaitAsync(TimeSpan.FromSeconds(3)));
            }

            await store.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await using var verified = await OpenConnectionAsync(store.DatabasePath);
            Assert.AreEqual(3L, await ScalarInt64Async(verified, "SELECT schema_version FROM schema_info"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task External_schema_writer_timeout_restores_the_callers_connection_policy()
    {
        var root = TestRoot();
        try
        {
            await using var owner = await OpenConnectionAsync(DatabasePath(root));
            await using var blocked = await OpenConnectionAsync(
                DatabasePath(root),
                defaultTimeoutSeconds: 9);
            await using var transaction = owner.BeginTransaction(deferred: false);
            var startedAt = System.Diagnostics.Stopwatch.StartNew();

            var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                LocalDatabaseSchema.InitializeAsync(blocked, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(15)));

            StringAssert.Contains(error.Message, "架构写入锁");
            Assert.IsLessThan(
                TimeSpan.FromSeconds(10),
                startedAt.Elapsed,
                "Schema initialization must apply a bounded lock policy.");
            Assert.AreEqual(9, blocked.DefaultTimeout);
            Assert.AreEqual(9_000L, await ScalarInt64Async(blocked, "PRAGMA busy_timeout"));
            Assert.HasCount(0, await ReadUserTableNamesAsync(blocked));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task CreateVersion1FixtureAsync(
        SqliteConnection connection,
        bool includeSeedRows)
    {
        await ExecuteAsync(connection, """
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
            """);
        if (!includeSeedRows)
        {
            return;
        }
        await ExecuteAsync(connection, """
            INSERT INTO meeting_events VALUES (
                'meeting-preserved', 'event-preserved', 4, 2,
                'meeting.opened', '2026-08-04T00:00:00Z', 'public', X'0102');
            INSERT INTO command_journal VALUES (
                'meeting-preserved', 'command-preserved', 'fingerprint',
                'completed', 4, X'0304', '2026-08-04T00:00:00Z');
            INSERT INTO runtime_checkpoints VALUES (
                'meeting-preserved', 4, 2, 1, 0, '2026-08-04T00:00:00Z');
            INSERT INTO subagent_runs VALUES (
                'meeting-preserved', 'subagent-preserved', 'role.parent',
                'completed', 1, X'0506', '2026-08-04T00:00:00Z');
            INSERT INTO legacy_projections VALUES (
                'meeting-preserved', X'0708', '2026-08-04T00:00:00Z');
            """);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        int defaultTimeoutSeconds = 5)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = defaultTimeoutSeconds,
        }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            $"PRAGMA busy_timeout={defaultTimeoutSeconds * 1000}; PRAGMA journal_mode=WAL;");
        return connection;
    }

    private static async Task<string[]> ReadTableContractsAsync(SqliteConnection connection)
    {
        var contracts = new List<string>();
        foreach (var table in Version3TableNames)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_xinfo(\"{table}\")";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(
                    $"{reader.GetString(1)}:{reader.GetString(2)}:{(reader.GetInt64(3) != 0 ? "required" : "optional")}:pk{reader.GetInt64(5)}:hidden{reader.GetInt64(6)}");
            }
            contracts.Add($"{table}|{string.Join(',', columns)}");
        }
        return [.. contracts];
    }

    private static async Task<string[]> ReadIndexContractsAsync(SqliteConnection connection)
    {
        var contracts = new List<string>();
        foreach (var table in Version3TableNames)
        {
            var indexes = new List<(string Name, bool Unique, string Origin, bool Partial)>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = $"PRAGMA index_list(\"{table}\")";
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    indexes.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3), reader.GetInt64(4) != 0));
                }
            }
            foreach (var index in indexes)
            {
                await using var detail = connection.CreateCommand();
                detail.CommandText = $"PRAGMA index_xinfo(\"{index.Name}\")";
                await using var detailReader = await detail.ExecuteReaderAsync();
                var columns = new List<string>();
                while (await detailReader.ReadAsync())
                {
                    if (detailReader.GetInt64(5) != 0)
                    {
                        columns.Add($"{detailReader.GetString(2)}:{(detailReader.GetInt64(3) != 0 ? "desc" : "asc")}:{detailReader.GetString(4)}");
                    }
                }
                var name = index.Origin == "c" ? $"{index.Name}|" : string.Empty;
                contracts.Add($"{table}|{name}{string.Join(',', columns)}|{(index.Unique ? "unique" : "nonunique")}|{index.Origin}|{(index.Partial ? "partial" : "complete")}");
            }
        }
        return [.. contracts.Order(StringComparer.Ordinal)];
    }

    private static async Task<string[]> ReadVersion1RowSnapshotAsync(SqliteConnection connection)
    {
        return await ReadQueryRowsAsync(connection, """
            SELECT 'meeting_events', meeting_id, event_id, sequence, runtime_generation,
                event_kind, occurred_at, visibility, hex(protected_event)
            FROM meeting_events
            UNION ALL
            SELECT 'command_journal', meeting_id, command_id, fingerprint, status,
                IFNULL(sequence, ''), hex(IFNULL(protected_receipt, X'')), updated_at, ''
            FROM command_journal
            UNION ALL
            SELECT 'runtime_checkpoints', meeting_id, last_sequence, runtime_generation,
                clean_shutdown, is_closed, updated_at, '', ''
            FROM runtime_checkpoints
            UNION ALL
            SELECT 'subagent_runs', meeting_id, subagent_id, parent_role_id, status,
                result_delivered, hex(protected_state), updated_at, ''
            FROM subagent_runs
            UNION ALL
            SELECT 'legacy_projections', meeting_id, hex(protected_projection), imported_at,
                '', '', '', '', ''
            FROM legacy_projections
            ORDER BY 1, 2
            """);
    }

    private static async Task<string[]> ReadVersion2RowSnapshotAsync(SqliteConnection connection)
    {
        return await ReadQueryRowsAsync(connection, """
            SELECT 'role_memories', workspace_id, role_profile_id, memory_id, memory_kind,
                current_revision, created_at, updated_at, IFNULL(superseded_at, '')
            FROM role_memories
            UNION ALL
            SELECT 'role_memory_revisions', workspace_id, role_profile_id, memory_id, revision,
                hex(protected_content), write_authority, IFNULL(source_meeting_id, ''),
                IFNULL(source_event_id, '') || ':' || IFNULL(confidence, '') || ':' || created_at
            FROM role_memory_revisions
            ORDER BY 1, 2, 3, 4
            """);
    }

    private static async Task<string[]> ReadSchemaSqlAsync(SqliteConnection connection)
    {
        return await ReadQueryRowsAsync(connection, """
            SELECT type, name, tbl_name, IFNULL(sql, '')
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """);
    }

    private static async Task<string[]> ReadAllRowsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{tableName.Replace("\"", "\"\"")}\"";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index =>
            {
                var value = reader.GetValue(index);
                return value switch
                {
                    DBNull => "<null>",
                    byte[] bytes => Convert.ToHexString(bytes),
                    _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
                };
            })));
        }
        return [.. rows];
    }

    private static async Task<string[]> ReadQueryRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index =>
                Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture) ?? "<null>")));
        }
        return [.. rows];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())
            ?? throw new AssertFailedException("Expected a non-null scalar string.");
    }

    private static async Task<string[]> ReadUserTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return [.. names];
    }

    private static string DatabasePath(string root) => Path.Combine(root, "roundtable.db");

    private static string TestRoot() =>
        Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
