# ADR 0004: Modern agent execution and Windows baseline

- Status: accepted
- Date: 2026-08-01

## Context

Pi Roundtable needs a modern agent architecture without turning Pi, a provider, or a framework into the product's public model. The Windows client also needs a long-lived platform baseline suitable for an installable local Runtime Owner.

## Decision

The Windows client targets .NET 10 LTS and Windows App SDK 2.3.1. The repository pins the verified .NET SDK feature band and allows servicing-patch roll-forward. Windows builds are framework- and Windows App SDK-self-contained so the installer does not require a separately installed .NET runtime. Current-user secure credential storage and bundled x64 MSI delivery are now implemented; signing, ARM64 packaging, and the real upgrade/repair matrix remain separate implementation work.

The Runtime Host follows these rules:

1. Each active role has one isolated, supervised Pi session. Meeting orchestration remains above the SDK.
2. Normalized domain events are authoritative. Raw Pi transcripts, reasoning data, credentials, and internal SDK types never cross the Runtime Host boundary.
3. Tools are unavailable by default. A role receives only explicit, meeting-scoped capabilities; granted MCP tools honor a host approval policy before side effects.
4. Provider credentials are resolved just in time and supplied in memory. Persistence belongs to the Windows Credential Manager adapter, not Pi session files or the sync server.
5. Commands have stable IDs and idempotent acknowledgement so reconnection and retry do not duplicate accepted work.
6. Runtime generation fencing remains authoritative across local execution, checkpoints, failover, and future background workers.

MCP-compatible consent boundaries, durable Windows checkpoints, and human approval are now implemented behind the reserved boundaries. OpenTelemetry GenAI tracing with content capture disabled by default and budget/loop limits remain planned extension points.

## Consequences

- Pi can evolve behind a pinned adapter without leaking SDK records into clients.
- Long-term and temporary roles can share the same execution contract while differing in persistence, memory, and retention policy.
- Agent autonomy is bounded by host capabilities and meeting ownership instead of prompt text alone.
- The first MSI can remain small and local-first while later adding recovery, diagnostics, and remote synchronization without changing the public protocol.
