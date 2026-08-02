# ADR 0007: Public and private conversation lanes

- Status: accepted
- Date: 2026-08-01

## Context

A roundtable needs two different communication contracts. The host's contribution to the meeting record is public even when it names a subset of participants, because every participant must be able to reason from the shared discussion. The host also needs a genuinely private thread with one participant that must not enter any other role's model context. Treating both as display filters over one transcript would leak private context and make replay authorization ambiguous.

The client must also show useful role state without presenting provider reasoning records or chain-of-thought as a product feature.

## Decision

The public protocol contract adds an explicit `visibility` lane and optional `audience` to normalized events. Commands use separate `speech.broadcast` and `speech.direct` intents:

1. `speech.broadcast` publishes the host message to the public record. Mentions select which roles answer next; they do not restrict who observes the message. With no mention, all active roles are queued in deterministic order. Only one role owns the public floor at a time.
2. `speech.direct` requires one target role. Its message, model response, and related runtime events carry private visibility and a host-plus-role audience. Private speech advances the authoritative event sequence but does not mutate the public meeting floor.
3. Each role runtime receives unseen public messages before its next public turn. Private messages are supplied only to the selected role runtime.
4. Clients and storage must apply an audience check before projecting a private event. The sync server now requires a signed, scoped device token for every `/v1` route, rejects malformed/private-audience uploads, and filters private replay/SSE against the authenticated user, device, and delegated audience identities. Client-side E2EE remains a separate layer.
5. The role inspector exposes normalized state and a bounded activity summary. Raw chain-of-thought, provider reasoning records, and private messages for other roles are not part of the client contract.

Every write still carries the active `runtimeGeneration`; visibility does not create a second sequence or runtime owner.

## Consequences

- Public transcript and private threads are persisted as separate message projections inside each session.
- Mention UI is routing for response selection, not an access-control boundary.
- The C++ core can validate public-floor ownership without treating private responses as public speakers.
- Sync authentication and audience-filtered private replay are implemented at the relay boundary. Future client E2EE and key-envelope management must preserve the same audience contract.
- Tool and MCP activity must inherit the initiating conversation lane unless a later additive protocol decision introduces a narrower artifact-sharing rule.
