# ADR 0008: Pi-only Windows local alpha

- Status: accepted
- Date: 2026-08-02
- Supersedes: ADR 0003 runtime-selection decision

## Context

The product must first become a reliable, installable Windows application. Maintaining a second model-runtime compatibility surface would consume testing and recovery work without improving that milestone. Earlier Oh My Pi experiments were useful references for process supervision, framed streaming, cancellation, and adapter boundaries, but compatibility is no longer a product requirement.

## Decision

Pi is the only supported model runtime. `packages/runtime-host` embeds the pinned public Pi SDK behind the neutral `RuntimeAdapter`; Pi session records, credentials, and SDK types remain private to that package. The Oh My Pi client, protocol decoder, public exports, fixtures, compatibility tests, version baseline, and future-adapter claims are removed.

The neutral adapter and bounded stdio process boundary remain because they separate meeting orchestration from Pi mechanics and allow deterministic tests. They do not promise multi-runtime compatibility.

The delivery sequence is Windows local-first: durable normalized events, turn-boundary recovery, bounded SubAgent execution, native UI, and an unsigned x64 packaging pipeline are implemented before Android client integration. Normal Windows meetings do not require a remote server. The relay now has signed device authentication, private audience filtering, and optional PostgreSQL persistence, but the Windows/Android end-to-end remote experience remains a later milestone. Authenticode, the complete installer lifecycle matrix, ARM64, E2EE, and Android integration remain separate work.

## Consequences

- Runtime and release testing cover one pinned Pi integration.
- Historical OMP references may remain only in superseded ADR context.
- New provider/runtime features must be expressed through the public meeting protocol and neutral adapter rather than new runtime-specific client contracts.
- Android remote-sync integration, client E2EE, ARM64, and exact Pi private-session restoration remain separate milestones; the implemented relay backend does not change the local-first runtime boundary.
