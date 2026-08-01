# ADR 0005: Local Runtime Host vertical slice

- Status: accepted
- Date: 2026-08-01

## Context

The direct Pi adapter was implemented as a library, while the Windows application was still a static shell. The first usable client needs a local process boundary that preserves meeting ownership, does not expose Pi records, and can later be bundled by the MSI without making the sync server a model executor.

## Decision

Windows launches one supervised local Runtime Host process per meeting. The host owns the meeting generation, global event sequence, role-to-Pi-session map, and the basic interruption handoff. It consumes public `MeetingCommand` values and emits public normalized `MeetingEvent` and `CommandReceipt` values inside a versioned stdio JSONL frame.

Provider ID, model ID, meeting identity, runtime identity, and generation are injected into the child environment at startup. The API key is delivered in a required one-time stdin initialization frame and is not written to application configuration, meeting events, logs, or the child environment. This in-memory handoff is an implemented interim boundary; Windows Credential Manager integration remains planned.

The Windows projection applies every normalized event to the C++ meeting core before updating visible state. A rejected sequence, generation, phase, speaker, role, or interruption transition is therefore surfaced instead of silently becoming UI state.

The local host starts with tools and subagents disabled. It supports meeting open/close, long-term and temporary role creation, temporary-role promotion, role archive/removal, prompting, cancellation, and explicit interrupt-then-handoff. Durable profiles, long-term memory, prompt optimization, approval-gated tools, recovery checkpoints, and remote failover remain planned.

## Consequences

- A normal Windows meeting no longer depends on the sync server.
- Multiple role sessions share one authoritative sequence and runtime generation.
- The stdio wrapper is a local transport; the payloads remain the public protocol contract.
- The current developer build requires Node, the built Runtime Host output, and a built x64 C++ core. MSI bundling and ARM64 native-core packaging remain separate work.
