# Release Candidate evidence contract

Pi Roundtable is a personal project. The Windows x64 release gate is therefore
self-contained and does not depend on purchased code-signing credentials, a
disposable VM, or several physical DPI sessions.

## Required evidence

Only a `ReleaseCandidate` report with `status: passed`,
`evidenceStatus: verified`, and `releaseEligible: true` may be published. The
report must be bound to the clean current Git commit and contain exactly these
evidence classes:

| Evidence | Required proof |
| --- | --- |
| `personal-windows-release-build` | Full repository tests, default MSI ICE validation, canonical five-asset set, version/commit binding, size and SHA-256 for every release asset |
| `isolated-qa-msi-lifecycle` with `payloadScope: full-production-payload` | Install, responsive launch, repair, major upgrade, blocked downgrade without mutation, second repair, uninstall, no residue, and unchanged production registration |

The signed ECDSA update manifest, exact release URL, MSI product identity,
release metadata, dependency inventory, CycloneDX SBOM, third-party notices,
clean repository, and current `origin/main` remain mandatory.

Authenticode is optional. When candidate metadata declares
`authenticodeRequired: true`, both the gate and publisher still require a
trusted timestamped MSI. An unsigned personal release declares `false`; the
updater still verifies the ECDSA manifest, exact file name, byte count, SHA-256,
HTTPS release URL, and monotonic version.

## Optional diagnostics

These implemented tools remain useful but do not block a personal release:

- `test-windows-production-msi-lifecycle.ps1` for a disposable clean-VM
  production UpgradeCode rehearsal;
- `run-windows-theme-visual-qa.ps1` and
  `merge-windows-visual-matrix.ps1` for real 96/144/192 DPI coverage;
- `run-windows-deepseek-roundtable.ps1` for a real-provider scenario;
- `build-signed-windows-x64.ps1` for a future Authenticode-signed build.

Running an optional tool does not weaken its own fail-closed checks, and a
missing optional report must be described as `pending`, not `verified`.

## Run and publish

Commit the release code first, then run the gate from an elevated PowerShell
session on clean `main`:

```powershell
npm run quality:release-candidate
```

The gate creates a unique report below `out/e2e/quality-gates/runs` and stores
the package below a short ignored `out/q` path. It accepts no external evidence
arguments.

Publication is a separate explicit action. The publisher verifies the fresh RC
report, current `origin/main`, the annotated tag, the exact five-asset set, and
all local/draft/pre-publication/public hashes. It never overwrites an existing
asset:

```powershell
pwsh -File .\scripts\publish-windows-release.ps1 `
  -ReleaseCandidateReportPath .\out\e2e\quality-gates\runs\<run>\quality-gate-report.json `
  -ReleaseNotesPath .\docs\release\v0.4.0.md `
  -Publish
```

After the public MSI is re-downloaded and verified, generate the next stable
manifest from those exact bytes. The manifest remains ECDSA-signed even when
the MSI is not Authenticode-signed:

```powershell
pwsh -File .\scripts\New-WindowsUpdateManifest.ps1 `
  -MsiPath .\out\e2e\release-publication\<run>\public-assets\PiRoundtable-0.4.0-win-x64.msi `
  -Version 0.4.0 `
  -AssetUrl https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.4.0/PiRoundtable-0.4.0-win-x64.msi
```

Pull-request CI remains the merge gate. The local ReleaseCandidate run is the
publication gate because it performs default ICE validation and the complete
interactive full-payload lifecycle.
