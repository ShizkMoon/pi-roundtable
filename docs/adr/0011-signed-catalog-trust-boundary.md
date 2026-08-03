# ADR 0011: Verify signed catalogs in a pure distribution trust boundary

- Status: accepted
- Date: 2026-08-03

## Context

The updater, future Module Center, offline layouts, and artifact imports need
the same answer to a narrow question: whether an untrusted catalog can be
treated as a trusted set of immutable asset descriptors. Reimplementing JSON,
signature, URI, and replay checks in each caller would create policy drift.
Putting download, installation, or persistent state changes into the verifier
would instead make verification dependent on the current machine and make
malicious-input testing expensive.

This boundary must also remain useful to the planned `ModuleCatalogV1` without
claiming that a catalog verifier is a module resolver or installer.

## Decision

1. `PiRoundtable.Distribution` owns a dependency-free `SignedCatalogDocument`
   version 1 contract, deterministic producer canonicalization, and
   `SignedCatalogVerifier`. Its inputs are untrusted UTF-8 bytes plus an
   explicit trusted policy, clock, key set, and anti-rollback floor. It performs
   no filesystem, network, registry, process, credential, or installation I/O.
2. Version 1 binds catalog identity, kind, channel, architecture, HTTPS origin,
   epoch, sequence, issue/expiry times, ordered asset descriptors, signature
   algorithm, and key ID. It uses ECDSA P-256/SHA-256 with a fixed 64-byte IEEE
   P1363 signature. The signature value is the only field excluded from the
   LF-terminated canonical bytes.
3. Parsing is case-sensitive and rejects comments, trailing commas, excessive
   depth, duplicate properties, unknown properties, missing properties, and
   oversized content. Assets have bounded identifiers, semantic versions,
   sizes, SHA-256 values, media types, safe leaf names, and canonical HTTPS
   URLs below one same-origin directory prefix without credentials, queries,
   fragments, dot segments, or encoded separators.
4. Trust keys are selected by exact key ID and carry independent not-before,
   not-after, and revocation policy. A catalog must be current, must have been
   issued while its key was valid, and must advance the caller-supplied
   `(epoch, sequence)` floor. Verification returns the next floor but never
   persists it; the owning transaction advances durable state only when it
   accepts the catalog.
5. Rejections use closed failure and field enums. Diagnostics cannot contain
   rejected JSON, URIs, paths, public-key material, or credentials. Successful
   values are normalized immutable objects whose constructors are internal to
   the distribution assembly.
6. Version 1 describes signed assets, not modules. A future
   `ModuleCatalogV1` layers module metadata, dependency policy, installation
   journals, health checks, and rollback ownership above this verifier. Module
   installation, background refresh, Authenticode execution policy, and atomic
   file promotion are not implied by this ADR.

## Consequences

- Update, module, import, and offline-layout code can share one trust substrate
  while retaining separate authorization and side-effect boundaries.
- Key rotation can overlap multiple trusted public keys; policy revocation
  invalidates a key without shipping private material or changing the signed
  document format.
- Persisting an anti-rollback floor too early could suppress a still-needed
  catalog, so state ownership stays outside the pure verifier.
- Deterministic adversarial and seeded malformed-input tests cover the current
  contract. Sustained fuzzing, handle-relative staging, reparse-safe creation,
  Authenticode-on-the-same-handle, and atomic promotion remain separate pending
  work.
