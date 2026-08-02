# ADR 0005: Local Runtime Host vertical slice

- Status: accepted
- Date: 2026-08-01

## Context

The direct Pi adapter was implemented as a library, while the Windows application was still a static shell. The first usable client needs a local process boundary that preserves meeting ownership, does not expose Pi records, and can later be bundled by the MSI without making the sync server a model executor.

## Decision

Windows launches one supervised local Runtime Host process per meeting. The host owns the meeting generation, global event sequence, role-to-Pi-session map, and the basic interruption handoff. It consumes public `MeetingCommand` values and emits public normalized `MeetingEvent` and `CommandReceipt` values inside a versioned stdio JSONL frame.

Meeting identity, runtime identity, generation, and non-secret process overrides are injected into the child environment at startup. Provider and MCP credentials are stored by reference in Windows Credential Manager, selected for the active participant manifests, and delivered in a required one-time stdin initialization frame. Credential values are not written to application JSON, meeting events, logs, or the child environment by the Windows supervisor; the Runtime Host materializes approved MCP header/environment values only at the consuming transport.

The Windows projection applies every normalized event to the C++ meeting core before updating visible state. A rejected sequence, generation, phase, speaker, role, or interruption transition is therefore surfaced instead of silently becoming UI state.

The local host starts with built-in tools and delegation disabled unless the frozen participant manifest grants them. It supports meeting open/close, long-term and temporary role creation, temporary-role promotion, role archive/removal, prompting, cancellation, explicit interrupt-then-handoff, explicitly granted Skill paths, approved MCP tool discovery/execution, private interactive tool approval, and bounded non-recursive Pi SubAgents. Windows recovery checkpoints and normalized event replay are implemented above the Host boundary. Durable long-term memory, prompt optimization, exact Pi private-session restoration, and remote failover remain planned.

## Consequences

- A normal Windows meeting no longer depends on the sync server.
- Multiple role sessions share one authoritative sequence and runtime generation.
- The stdio wrapper is a local transport; the payloads remain the public protocol contract.
- The developer build requires Node, the built Runtime Host output, and a built x64 C++ core. The unsigned x64 MSI bundles those dependencies for end users; signing and ARM64 native-core packaging remain separate work.
