# Wire protocol

The JSON Schemas in `schema/` describe the client/server contract. Protocol version `1` uses additive payload and event-kind evolution within the major version. Meeting event schemas accept namespaced string kinds and publish the currently understood set as `x-knownValues`. A client that encounters an unknown event kind must preserve the sequence cursor and refresh from a snapshot or require an upgrade rather than inventing a state transition. Breaking required fields or existing semantics require a new major version and parallel server support during migration.

The server assigns `eventId`, `sequence`, and `occurredAt`. Runtime Hosts supply `meetingId`, `runtimeGeneration`, event kind, actor/target IDs, and a normalized payload. Clients resume from the largest fully applied `sequence`.

`workspace-profile.schema.json` and `roundtable-session.schema.json` are versioned configuration contracts, separate from wire protocol major version 1. They define reusable non-secret provider/model/Skill/MCP/long-term-role catalogs and frozen session participant manifests. `@pi-roundtable/protocol` implements their TypeScript types and cross-catalog integrity checks. Credential fields are opaque secure-store references; provider or MCP credential values are never valid profile data.

## Role lifecycle

Protocol v1 distinguishes durable and meeting-scoped participation without exposing an agent runtime's internal session model:

- `role.registered` adds a long-term role to the active meeting roster.
- `role.temporary_registered` creates a meeting-scoped role.
- `role.promoted` changes an active temporary role to long-term. The transition is one-way.
- `role.archived` removes a role from the active roster while retaining its meeting record.
- `role.left` removes the retained meeting role record.

For these lifecycle events, `actorId` identifies the affected role. The causal command records who requested the transition. Snapshot projection of scope and lifecycle remains planned; it will be introduced with an explicitly compatible snapshot contract rather than silently extending a strict v1 role object.

Workspace profile persistence, secure provider/MCP credential references, frozen participant prompts/model routes, catalog import status, and explicit Skill/MCP grants are implemented in the Windows local-client path. The Runtime Host resolves installed catalog entries and executes tools from an approved and attached MCP server. Durable memory contents, prompt-revision history, live capability-change events, per-tool interactive approval, SubAgent-isolated execution, and end-of-meeting promotion review remain planned; lifecycle events do not claim those services are implemented.
