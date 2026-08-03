# ADR 0012: Keep Windows artifact staging handle-owned through promotion

- Status: accepted
- Date: 2026-08-04

## Context

The desktop updater and future module, import, and offline-layout workflows all
need to turn an untrusted byte stream into a published local artifact. A
path-only sequence such as check-directory, create-file, close, reopen for
Authenticode, and `File.Move` permits path components or the leaf to change
between trust decisions. A random temporary name alone does not close those
races, coordinate concurrent writers, or recover files left by a process
crash.

The solution must stay in the dependency-free distribution assembly and use
documented Windows APIs. It must not move network policy, signed-manifest trust,
installation authorization, or process execution into that assembly.

## Decision

1. `PiRoundtable.Distribution` opens every existing or newly created directory
   component with `FILE_FLAG_OPEN_REPARSE_POINT`, rejects reparse points from
   handle metadata, and withholds delete sharing for the lifetime of staging.
   Missing components are created one at a time only while their resolved
   ancestors remain locked.
2. A unique `<stem>.<guid>.partial<extension>` leaf is created with `CREATE_NEW`,
   no-follow semantics, read/write/delete access, write-through I/O, and a
   single owned `SafeFileHandle`. Copy bounds, SHA-256, a post-flush second
   verification pass, and optional Authenticode all observe that same file
   object.
3. Promotion calls documented `SetFileInformationByHandle(FileRenameInfo)` on
   that source handle. It uses a root-null absolute extended-length destination
   while every destination directory component remains locked. No path reopen,
   undocumented native object-manager call, or copy-and-delete promotion is an
   allowed release boundary.
4. One durable no-follow lock leaf serializes a staging directory across
   processes. The kernel releases its exclusive handle after a crash. While the
   lease is held, callers may delete only orphan names that exactly match the
   GUID staging grammar or the updater's two exact legacy temporary suffixes;
   cleanup opens and deletes each leaf by handle and rejects reparse points.
5. Cancellation is honored before promotion. Promotion and its state-sidecar
   commit form a non-cancellable commit region. The sidecar is replaceable cache
   metadata rather than artifact authority: if sidecar publication fails after
   a verified package was promoted, the package remains and the next call must
   re-lock, re-hash, re-apply required Authenticode policy, and repair state
   without trusting the stale path.
6. Failure before promotion requests deletion through the still-open leaf and
   retries during disposal. A reader that did not share delete may defer that
   cleanup; the next directory lease safely retries the orphan. The independent
   updater helper still reopens without following reparse points and re-verifies
   exact size and SHA-256 immediately before invoking Windows Installer.

## Consequences

- The same distribution primitive can support updater, module, import, and
  offline-layout transactions without giving it network or install authority.
- Concurrent calls for one version reuse the first verified result instead of
  racing two promotions or state writes.
- Long destination paths use the extended Windows path form consistently with
  creation and verified open.
- The persistent lock leaf is an intentional small control file. Random partial
  leaves are recoverable crash state, not durable product data.
- Deterministic tests cover success, cancellation/timeout while waiting, reparse
  rejection, ancestor locks, Authenticode handle identity, replacement, long
  paths, orphan cleanup, concurrent reuse, and sidecar-failure recovery.
  Sustained fuzzing, forced process termination at every commit instruction,
  and clean-VM release evidence remain pending release gates.
