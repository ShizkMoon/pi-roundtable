# ADR 0013: Separate meeting memory from platform operations

- Status: accepted
- Date: 2026-08-04

## Context

The Windows Runtime Owner already persists normalized meeting events, command
receipts, checkpoints, and encrypted role-memory revisions in
`roundtable.db`. Context snapshots and reviewed memory candidates belong near
that authoritative meeting history. Module installation, artifact workers, and
diagnostic retention have different lifetimes, backup policies, and corruption
failure domains. Putting them all into the meeting database would couple every
module or file-format migration to meeting recovery.

The repository needs reviewed storage contracts before either the
`roundtable.db` v3 migration or the first `platform.db` migration is allowed to
land. A specification is not an implemented table or a connected user path.

## Decision

### 1. Existing `roundtable.db` v2 boundary

`%LOCALAPPDATA%\PiRoundtable\data\roundtable.db` remains owned by Windows
meeting and role-memory stores. Version 2 contains only:

- normalized meeting events, durable command receipts, runtime checkpoints,
  SubAgent recovery state, and legacy projection import state;
- logical role memories and their append-only, DPAPI-protected revisions.

The memory kind is the current classification of one logical memory, not a
revision field. Reclassification changes that logical label; immutable revision
content, authority, provenance, confidence, and creation time remain unchanged.

Initialization distinguishes a new database from an existing versioned one.
Existing v1/v2 databases must match the exact reviewed table, column, primary
key, unique-index, named-index, and foreign-key shape before use. Migrations run
inside one SQLite transaction, reject unsupported future versions, and preserve
the original database on any failure. An unexpected user table is corruption or
ownership mixing, not an invitation to infer a schema.

### 2. Reviewed `roundtable.db` v3 contract

Version 3 is **planned**, not created by v0.4. Its first production migration is
blocked until the v2 direct-schema tests and this contract remain green. The
migration will add exactly four private table families while preserving every
v2 table and row:

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

V3 DDL, indices, limits, and the v2→v3 transaction will be introduced together
with migration/rollback, restart, isolation, no-plaintext, and previous-version
fixtures. No placeholder v3 table is allowed before that change.

### 3. Future `platform.db` v1 boundary

`%LOCALAPPDATA%\PiRoundtable\data\platform.db` is a separate **planned**
database owned by the Windows platform/application-service layer. It never
stores normalized meeting events, command receipts, role memories, provider
credentials, raw Pi sessions, or document bodies that belong in content-
addressed artifact files.

Its reviewed version-1 ownership covers:

- signed catalog observations, immutable module versions, and active-version
  pointers;
- an append-only module install/update/remove journal and dependency
  observations;
- content-addressed artifact metadata, isolated worker jobs, quota/retention
  state, and orphan reconciliation;
- private versioned diagnostic records and retention/export-preview state;
- backup-set metadata and a platform migration journal.

`platform_schema_info` has an independent integer version. A
`platform_migration_journal` records operation ID, from/to version, phase,
started/updated/completed timestamps, recovery disposition, and a closed
redacted failure code. Large packages and generated files remain outside SQLite
and are referenced by digest-scoped IDs. Active pointers change only after
signed-catalog, artifact-integrity, dependency, and health checks commit.

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
- V0.5 may implement `roundtable.db` v3 only after the v2 compatibility gate;
  v0.6 may create `platform.db` v1 only with its own interrupted-migration,
  backup/restore, concurrent-open, and prior-version evidence.
- Diagnostics can later share `platform.db` retention without becoming public
  protocol events or contaminating meeting storage.
- This ADR implements the v3/platform ownership specifications only. V3 tables,
  `platform.db`, backup engines, diagnostic stores, and their UI remain planned.
