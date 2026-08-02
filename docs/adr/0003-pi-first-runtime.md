# ADR 0003: Pi-first runtime with optional OMP compatibility

- Status: superseded by ADR 0008
- Date: 2026-08-01

## Context

Pi Roundtable is a meeting product built on Pi's agent runtime primitives. Oh My Pi is a useful reference and compatibility target, but making its RPC records, sessions, or command vocabulary the product model would couple every client to one fork and prevent the meeting domain from evolving independently.

The first scaffold used `omp --mode rpc` to exercise process isolation and streaming. This record is retained only as architectural history; the compatibility implementation was removed by ADR 0008.

## Decision

The default Runtime Host will embed Pi through its supported SDK surface. A domain-neutral `RuntimeAdapter` separates meeting orchestration from a concrete agent engine.

The historical implementation retained `OmpRpcClient` as a low-level optional compatibility client inside `packages/runtime-host` and required any future adapter to normalize frames before they reached the meeting core, sync server, or native clients. ADR 0008 removed that compatibility surface.

Windows will own the first local Runtime Host. Normal local meetings must not require a remote synchronization server. Remote sync remains an optional normalized-event relay and does not become the default model executor.

## Consequences

- Pi sessions, OMP sessions, credentials, and raw runtime events were treated as private implementation details.
- Runtime capabilities such as prompting, steering, cancellation, tools, and subagents are expressed in Pi Roundtable vocabulary.
- The public meeting protocol remained independent enough for ADR 0008 to omit OMP without a protocol break.
- Direct Pi SDK integration, provider onboarding, secure credential storage, local persistence, and MSI packaging remain separate implementation milestones and must not be reported as implemented by this ADR.
