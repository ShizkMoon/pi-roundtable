# ADR 0003: Pi-first runtime with optional OMP compatibility

- Status: accepted
- Date: 2026-08-01

## Context

Pi Roundtable is a meeting product built on Pi's agent runtime primitives. Oh My Pi is a useful reference and compatibility target, but making its RPC records, sessions, or command vocabulary the product model would couple every client to one fork and prevent the meeting domain from evolving independently.

The first scaffold used `omp --mode rpc` to exercise process isolation and streaming. That adapter remains useful, but it must not define the default runtime contract.

## Decision

The default Runtime Host will embed Pi through its supported SDK surface. A domain-neutral `RuntimeAdapter` separates meeting orchestration from a concrete agent engine.

`OmpRpcClient` remains a low-level optional compatibility client inside `packages/runtime-host`. A future `OmpRuntimeAdapter` must translate OMP frames and commands into the neutral runtime contract before they reach the meeting core, sync server, or native clients.

Windows will own the first local Runtime Host. Normal local meetings must not require a remote synchronization server. Remote sync remains an optional normalized-event relay and does not become the default model executor.

## Consequences

- Pi sessions, OMP sessions, credentials, and raw runtime events remain private implementation details.
- Runtime capabilities such as prompting, steering, cancellation, tools, and subagents are expressed in Pi Roundtable vocabulary.
- OMP can be upgraded, replaced, or omitted without changing the public meeting protocol.
- Direct Pi SDK integration, provider onboarding, secure credential storage, local persistence, and MSI packaging remain separate implementation milestones and must not be reported as implemented by this ADR.
