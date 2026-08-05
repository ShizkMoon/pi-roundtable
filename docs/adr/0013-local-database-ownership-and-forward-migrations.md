# ADR 0013: Separate meeting memory from platform operations

- Status: accepted
- Date: 2026-08-05

## Context

The Windows Runtime Owner persists normalized meeting events, command receipts,
checkpoints, encrypted role-memory revisions/candidates, and private recovery
records in `roundtable.db`. Context snapshots and reviewed memory candidates
belong near that authoritative meeting history. Module installation, artifact
workers, artifact bytes, and diagnostic retention have different lifetimes,
backup policies, and corruption failure domains. Putting them all into the
meeting database would couple every module or file-format migration to meeting
recovery.

The reviewed contracts were required before the `roundtable.db` v3 migration
and first `platform.db` slice could land. This ADR now records both the
implemented boundaries and the larger platform schema that remains planned. A
table is not a connected user path, and a minimal artifact slice is not the
complete platform database.

## Decision

### 1. Implemented `roundtable.db` v3 boundary

`%LOCALAPPDATA%\PiRoundtable\data\roundtable.db` remains owned by Windows
meeting and role-memory stores. Version 3 contains:

- normalized meeting events, durable command receipts, runtime checkpoints,
  SubAgent recovery state, and legacy projection import state;
- logical role memories and their append-only, DPAPI-protected revisions;
- encrypted memory candidates, append-only recall audits, immutable role
  context snapshots, and bounded memory-retention jobs.

The memory kind is the current classification of one logical memory, not a
revision field. Reclassification changes that logical label; immutable revision
content, authority, provenance, confidence, and creation time remain unchanged.

Initialization distinguishes a new database from an existing versioned one.
Existing v1/v2/v3 databases must match the exact reviewed table, column,
primary-key, unique-index, named-index, and foreign-key shape before use.
Migrations run inside one SQLite transaction, reject unsupported future
versions, and preserve the original database on any failure. An unexpected user
table is corruption or ownership mixing, not an invitation to infer a schema.

### 2. Implemented `roundtable.db` v3 additions

The v2→v3 migration adds exactly four private table families while preserving
every v2 table and row:

1. `memory_candidates`
   - stable candidate ID, workspace ID, long-term role-profile ID, memory kind,
     proposal policy (`manual`, `review_required`, or `meeting_close`), and an
     optimistic decision revision;
   - DPAPI-protected proposed content, source meeting/event identifiers,
     bounded confidence, safety status, decision status, and timestamps;
   - no approval silently edits an active role session. Approval appends through
     the existing immutable memory-revision transaction.
2. `memory_recall_audits`
   - append-only audit ID, meeting ID, role-profile/session identity,
     `runtimeGeneration`, freeze sequence, ranking-policy version, entry/token/
     character budgets, and timestamp;
   - a protected selection record may contain memory IDs, revisions, scores,
     and closed reason codes, but never duplicate plaintext memory or prompts.
3. `role_context_snapshots`
   - immutable snapshot ID, meeting/role identity, `runtimeGeneration`, source
     sequence, stable-prefix fingerprint, context-policy version, byte/token
     counts, creation time, and DPAPI-protected snapshot payload;
   - restore requires every identity/fingerprint/version field to match. A bad
     snapshot is discarded independently and rebuilt from normalized events.
4. `memory_retention_jobs`
   - bounded job ID, workspace/role scope, retention-policy version, cursor,
     state, attempt count, scheduled/started/completed timestamps, and a closed
     redacted failure code;
   - jobs may supersede or quarantine private memory state but cannot delete or
     rewrite normalized meeting history.

V3 DDL, indices, limits, and the v2→v3 transaction landed together with
migration/rollback, restart, isolation, no-plaintext, and previous-version
fixtures. The connected user path currently covers manual candidate review and
bounded recall audits. Context-snapshot persistence/restore and retention-job
execution remain planned even though their immutable storage contracts exist.

### 3. Minimal `platform.db` v1 artifact boundary

`%LOCALAPPDATA%\PiRoundtable\data\platform.db` is a separate database owned by
the Windows platform/application-service layer. Its implemented version-1 slice
contains only content-addressed artifact descriptors and meeting bindings. It
never stores normalized meeting events, command receipts, role memories,
provider credentials, raw Pi sessions, or document bodies; artifact bytes live
under a SHA-256 content-addressed root.

The complete reviewed version-1 ownership is broader and remains planned for:

- signed catalog observations, immutable module versions, and active-version
  pointers;
- an append-only module install/update/remove journal and dependency
  observations;
- content-addressed artifact metadata, isolated worker jobs, quota/retention
  state, and orphan reconciliation;
- private versioned diagnostic records and retention/export-preview state;
- backup-set metadata and a platform migration journal.

The implemented slice has an independent `platform_schema_info` integer version
and validates its owned tables before use. `platform_migration_journal`, module
catalog/install records, worker jobs, diagnostics, and backup metadata are not
created as placeholders. The future journal records operation ID, from/to
version, phase, timestamps, recovery disposition, and a closed redacted failure
code. Large packages and generated files remain outside SQLite and are
referenced by digest-scoped IDs. Active pointers change only after signed-
catalog, artifact-integrity, dependency, and health checks commit.

### 4. Backup, restore, and diagnostics

The two databases are separate backup units. A workspace-level backup manifest
may bind both files and external artifact digests, but each database has its own
schema version, hashes, migration state, restore preflight, atomic replacement,
and corruption quarantine. Credentials and machine-external secrets are always
excluded. DPAPI-protected records remain bound to their documented Windows user
scope until an explicit encrypted transfer design exists.

Future migration and validation diagnostics must be closed and private. They may
identify the database kind, declared/expected version, phase, and failure code;
they may not include plaintext memory, prompts, credentials, raw provider
content, local absolute paths, SQL containing private values, or DPAPI blobs.

## Consequences

- Meeting recovery cannot be broken by a module-catalog or artifact-worker
  schema migration.
- `roundtable.db` v3 and the minimal artifact subset of `platform.db` v1 are
  implemented with direct migration/storage tests. Backup/restore, corruption
  quarantine, and the complete platform migration journal remain release gates.
- Diagnostics can later share `platform.db` retention without becoming public
  protocol events or contaminating meeting storage.
- This ADR governs the implemented v3 tables and minimal platform artifact
  slice. Backup engines, diagnostic/module stores, general artifact workers,
  complete platform migrations, and their UI remain planned.
