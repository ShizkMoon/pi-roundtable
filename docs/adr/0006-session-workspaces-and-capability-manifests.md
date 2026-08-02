# ADR 0006: Session workspaces and explicit role capability manifests

- Status: accepted
- Date: 2026-08-01

## Context

The first Windows vertical slice placed the active role roster at the left edge and treated provider/model input as meeting startup fields. That proved the local execution boundary, but it is not the intended product model. Pi Roundtable is a session-oriented Agent client: users return to conversations, while providers, models, long-term roles, Skills, and MCP servers are reusable workspace assets.

A temporary role is also more than a display name. It is an invited participant whose creator must define its purpose, system prompt, model route, capabilities, delegation policy, and retention decision. Allowing the model to infer those permissions from prose would mix identity with authorization and make tool execution unauditable.

## Decision

Pi Roundtable uses four distinct layers:

1. A **workspace profile** stores reusable, non-secret configuration: providers, model catalog entries, Skill descriptors, MCP server descriptors, long-term role profiles, and defaults.
2. A **session** is the primary client object and the durable boundary for one roundtable conversation. In the first runtime implementation, `sessionId` is also the meeting ID used by the event protocol.
3. A **role profile** is a reusable long-term identity. Joining a session creates a participant binding with frozen prompt, model, memory, and capability snapshots so later profile edits do not rewrite history.
4. A **temporary-role invitation** is a session-scoped capability manifest. It requires an inviter, purpose, complete system prompt, explicit model route, explicit Skill/MCP/tool grants, delegation rules, and retention policy before the Runtime Host may create the role.

The current Windows milestone persists the session definition and its frozen participant manifests. Normalized transcript/event history is stored in SQLite with current-user DPAPI protection; reopening replays accepted events through a fresh reducer before a resumable live meeting acquires the next runtime generation.

Provider endpoints and model selections are durable configuration. Provider secrets are not: workspace JSON stores only an opaque `credentialRef`; Windows resolves that reference through Windows Credential Manager immediately before launching the Runtime Host.

Capability grants are deny-by-default. Skills and MCP servers live in workspace catalogs and roles reference them by stable ID. MCP grants include explicit tool allowlists and approval mode. A prompt cannot grant a capability that is absent from the manifest.

Network search, browsing, and other high-volume discovery capabilities default to `subagent_required`. A delegated worker returns a bounded summary with citations and artifact references; raw search traces and tool chatter remain outside the parent role's primary context unless explicitly requested.

Long-term memory and prompt evolution are separate policies. A long-term role may retain selected memory across sessions, while prompt self-optimization defaults to proposing a revision that requires review. Neither policy gives the role additional tool authority.

## Consequences

- The Windows shell becomes session-first: session rail, active conversation, and contextual role/capability inspector.
- The existing meeting event log remains authoritative for live state; workspace/session configuration does not become an alternate event stream.
- Clients may synchronize non-secret profiles later, but credential material remains platform-local or in a dedicated secret service.
- Runtime adapters receive resolved per-role model and prompt inputs. Pi-specific Skill or MCP mechanics remain private to `packages/runtime-host`.
- The initial capability milestone may persist and validate grants before every grant has an executor. Such entries must be labelled `planned` until a policy-enforced runtime adapter is verified.
