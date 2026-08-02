# ADR 0001: One authoritative runtime owner per meeting

- Status: accepted
- Date: 2026-08-01

## Context

Desktop machines can run Pi locally, while mobile clients are UI-only and a Linux server provides synchronization. Allowing several runtimes to generate for the same role concurrently would make interruption, tool execution, costs, and transcript ordering ambiguous.

## Decision

Exactly one Runtime Host owns a meeting lease at a time. The server assigns a monotonically increasing `runtimeGeneration` whenever ownership changes. Every appended event carries the owner ID and generation. A stale owner cannot append after failover even if its network connection remains alive.

A same-owner lease renewal retains the generation. A takeover after expiry or explicit release increments it. Events retain a meeting-wide monotonically increasing `sequence` independent of runtime generation.

## Consequences

- Interruption and tool cancellation have one authoritative executor.
- Clients can replay by sequence without merging competing model histories.
- Offline mobile clients may queue commands, but the server must revalidate them against the current owner and meeting state.
- The optional PostgreSQL store now provides durable transactional lease, generation, sequence, and event writes. The in-memory store remains only an executable development reference; multi-replica notification/coordination and production operations are still separate gates.
