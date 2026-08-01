# Architecture

## 1. Deployment model

A meeting is controlled by one Runtime Host. In the first implementation cycle that host will run beside the Windows application and embed Pi through its supported SDK surface. Oh My Pi remains an optional compatibility runtime behind an adapter. Linux hosts the optional synchronization plane: normalized event storage, lease fencing, replay cursors, authentication, and later push notifications. Android is a presentation and control client and never launches Pi locally.

This split avoids two conflicting sources of truth. If a runtime moves to another machine, the sync server increments `runtimeGeneration`; events from the old generation are rejected even if the old process reconnects late.

## 2. Components

### Protocol

`protocol/schema` is the wire contract. Every persisted event contains a protocol version, meeting ID, monotonic sequence, runtime generation, event kind, occurrence time, and an optional normalized payload. Platform clients must tolerate unknown payload keys but must reject unsupported protocol major versions.

### C++ meeting core

`core` applies ordered events and enforces deterministic meeting invariants. It does not parse OMP events, open sockets, access storage, or render UI. A narrow C ABI makes the reducer consumable from native shells without exporting C++ ABI details.

### Runtime Host

`packages/runtime-host` defines a domain-neutral runtime contract and owns all Pi/OMP compatibility. The planned default adapter embeds Pi directly. The currently implemented OMP transport:

1. starts `omp --mode rpc`;
2. waits for the `ready` frame;
3. negotiates protocol v2 when advertised;
4. validates and reassembles `rpc_chunk` frames;
5. correlates command responses by ID;
6. will pass validated OMP frames to a planned `OmpRuntimeAdapter` for normalization before synchronization.

Role scheduling, tool policy, host-tool callbacks, direct Pi integration, and runtime-to-domain event normalization are intentionally separate from the low-level adapter and remain planned.

### Sync server

`packages/sync-server` begins with an in-memory implementation so lease and replay semantics are executable. HTTP uploads events; SSE supplies foreground real-time updates and reconnect cursors. Production storage, authentication, E2EE, rate limits, and distributed lease coordination remain planned.

### Native clients

Windows uses WinUI 3 and will own the first local runtime integration. A normal local meeting must work without a remote sync server. Android uses Kotlin/Compose Material 3 with layouts that adapt at 600 dp and 840 dp. Both consume normalized protocol models; neither imports Pi or OMP internals.

## 3. Interruption model

Interruption is not simulated by visually reordering chat messages. The authoritative event order is:

1. `speech.started` for the current speaker;
2. `interruption.requested` from another role;
3. `speech.cancelled` for the current generation;
4. `speech.started` for the interrupting role.

The core rejects a second interruption while one is pending, a cancellation targeting a non-speaker, and a new speaker that bypasses the pending handoff. Runtime-specific cancellation maps to OMP `abort`, `steer`, or `abort_and_prompt` according to the orchestration policy.

## 4. Role lifecycle

The meeting core distinguishes a long-term role from a meeting-scoped temporary role. Registration adds an active participant. A temporary role may be promoted once to long-term. Archiving removes the role from the active roster while retaining its meeting record; leaving removes that record. All transitions remain ordered meeting events and carry the active `runtimeGeneration`.

This implemented lifecycle is only the deterministic meeting foundation. Durable role profiles, auto-join policy, responsibilities, authorization, memory, prompt optimization, and end-of-meeting retention workflows remain planned.

## 5. Data ownership

The synchronization plane stores domain events and snapshots, not raw provider credentials, local tool output directories, or complete OMP session files. Large artifacts should be referenced by scoped object identifiers. Secrets stay with the Runtime Host or a future dedicated secrets service.
