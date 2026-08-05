# ADR 0010: Bound context, memory, plugins, and document artifacts at the Windows runtime

- Status: accepted
- Date: 2026-08-03

## Context

Long-running roundtables need automatic context compaction, cache-stable role
prompts, durable role memory, auxiliary Pi resources, and office/document input.
Those features cross several trust boundaries. Raw Pi sessions and extension
types are not the public protocol, Pi extensions execute with host OS
permissions, and untrusted office packages can contain macros, external XML
entities, traversal paths, or compression bombs.

## Decision

1. Runtime context policy remains private to `packages/runtime-host`. A role gets
   one deterministic system prefix, provider session affinity, short cache
   retention by default, and Pi auto-compaction at a bounded window threshold.
   Dynamic agenda and routing instructions stay in user turns.
2. Windows owns durable role memory. Memory contents use DPAPI in the versioned
   local SQLite store. Logical memories point to immutable revisions carrying
   provenance, confidence, and write authority. Memory is scoped by workspace
   and long-term role profile.
3. Pi plugin compatibility is capability based:
   - approved Pi Skills load as native resources;
   - executable/tool plugins use the existing MCP bridge, tool allowlist, and
     approval policy;
   - raw Pi extensions never load in the authoritative meeting process. A
     future extension bridge must isolate the process and expose normalized
     tools before this status changes.
4. Windows document input first passes through `DocumentPipeline`. Markdown and
   TeX remain inert UTF-8 source; DrawIO and OOXML are parsed with DTD/external
   entities disabled and bounded package expansion; PDFs are signature-checked
   metadata-only until a bounded text extractor is selected. Macro-enabled
   Office packages are rejected.
5. These implementation details do not change `protocol/schema`. A future
   cross-device artifact or memory projection requires a separate normalized
   protocol proposal and must never contain DPAPI ciphertext or raw Pi records.

## Consequences

- Existing meetings and public event contracts remain compatible.
- Prefix caching improves when a provider supports it; providers that do not
  support caching safely ignore the hint.
- Manual memory candidate review and bounded next-session recall are implemented
  through explicit Windows/Runtime Host integration. Automatic meeting-close
  extraction, safety scanning, and retention remain separate planned work.
- Markdown/TeX/DrawIO/OOXML input can be normalized safely. PDF body extraction,
  KaTeX-quality typesetting, and office/PDF output remain separate deliverables,
  not implied by format recognition.
- Confirmed public-composer inputs are stored in a private content-addressed
  artifact store with independent metadata, quota, meeting references, and
  restart reconciliation. This minimal slice does not imply general artifact
  workers, previews, private-composer parity, or backup/restore.
