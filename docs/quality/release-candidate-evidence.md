# Release Candidate evidence contract

This document defines the implemented final-gate validation contract for a
Windows x64 Release Candidate. Production clean-VM evidence capture is implemented as a
separate fail-closed harness. The gate prevents a successful command, an old report, an
ephemeral signing test, or an isolated QA installer from being presented as
production release evidence.

## Evidence classes

The quality report has two independent results:

- `status: passed|failed` records whether every invoked command exited
  successfully.
- `evidenceStatus: verified|failed|not-applicable` records whether structured
  evidence was parsed and matched the current build identity.

Only a `ReleaseCandidate` report with `status: passed`,
`evidenceStatus: verified`, and `releaseEligible: true` is release-gate
evidence. Fast reports intentionally use `not-applicable`. Every run writes a
unique directory under `out/e2e/quality-gates/runs`; it never overwrites a
report for an earlier run. Large harvested payloads use a separate unique short
path under `out/q` because Windows Installer ICE subprocesses fail on deeply
nested source paths; the report records that exact work path.

The gate keeps the following evidence classes separate:

| Evidence | What it proves | What it cannot prove |
| --- | --- | --- |
| `ephemeral-signing-pipeline-smoke` | signing order, copy isolation, certificate cleanup | production identity, trusted chain, RFC 3161 timestamp |
| `isolated-qa-msi-lifecycle` | installer state machine under a dedicated QA UpgradeCode | upgrade of an actually published production MSI |
| `production-signed-windows-build` | full tests and ICE ran, five release artifacts are trusted and timestamped | installed upgrade behavior or visual correctness |
| `production-clean-vm-stable-to-candidate` | real production UpgradeCode transition on a disposable clean VM | other DPI sessions or other artifact bytes |
| `real-windows-dpi-visual-matrix` | light, dark, high-contrast and responsive widths at real 96/144/192 DPI | installer or update behavior |
| `real-provider-windows-roundtable` | real DeepSeek/Pi three-role scenarios bound to one candidate executable | installer, DPI matrix, or another provider |

## Build and collect evidence

First commit the candidate version and all candidate code. The signed build
captures the full 40-character commit and records whether the worktree was
dirty. Its PFX password must exist only in the current process environment:

```powershell
$env:PI_ROUNDTABLE_SIGNING_PFX_PASSWORD = '<secret from the release secret store>'
pwsh -NoProfile -File .\scripts\build-signed-windows-x64.ps1 `
  -PfxPath C:\secure\pi-roundtable-release.pfx `
  -TimestampUrl https://timestamp.example.com
Remove-Item Env:\PI_ROUNDTABLE_SIGNING_PFX_PASSWORD
```

Do not use `-SkipVerification` or `-SuppressMsiValidation`. The resulting
`signed-build-report.json` records the package root and exact installer path.
Run `run-windows-theme-visual-qa.ps1` against that recorded package on three
real desktop sessions with `-ExpectedDpi 96`, `144`, and `192`. Copying the
package to another machine is allowed; changing the WinUI executable is not.

Download the stable MSI named by the committed, signed
`packaging/windows-x64/update-manifest.json`. The final gate recalculates its
file name, size, and SHA-256 and rejects any mismatch.

The production lifecycle report must come from a disposable clean Windows VM.
Generate it with `scripts/test-windows-production-msi-lifecycle.ps1`; the script
requires an explicit disposable-VM attestation, verifies that it is running in a
detected VM, rejects pre-existing Pi Roundtable state, and limits cleanup to the
exact ProductCodes installed by that run. The report has this minimum JSON shape:

```json
{
  "schemaVersion": 1,
  "evidenceId": "00000000-0000-0000-0000-000000000000",
  "status": "verified",
  "evidenceClass": "production-clean-vm-stable-to-candidate",
  "sourceCommit": "40 lowercase hexadecimal characters",
  "environment": {
    "cleanVm": true,
    "architecture": "x64",
    "vmImage": "immutable image identity",
    "snapshotId": "disposable snapshot identity",
    "osBuild": "Windows build"
  },
  "baseline": {
    "version": "stable manifest version",
    "fileName": "stable MSI file name",
    "size": 1,
    "sha256": "stable MSI SHA-256"
  },
  "candidate": {
    "version": "current VERSION",
    "fileName": "candidate MSI file name",
    "size": 1,
    "sha256": "signed candidate MSI SHA-256"
  },
  "checks": {
    "installBaseline": true,
    "launchBaseline": true,
    "upgradeCandidate": true,
    "launchCandidate": true,
    "repairCandidate": true,
    "downgradeBlocked": true,
    "uninstallCandidate": true,
    "noProductsRemaining": true
  },
  "rebootRequired": false,
  "startedAt": "ISO-8601 timestamp",
  "completedAt": "ISO-8601 timestamp",
  "verifiedAt": "ISO-8601 timestamp"
}
```

The producer retains VM image/snapshot identity, Windows build, MSI logs, exit
codes, and reboot state for audit. The final gate validates the fields above and
accepts reports no more than 24 hours old. This remains an explicit
release-owner/clean-VM execution step, not a local workstation substitute.

## Run the final gate

```powershell
npm run quality:release-candidate -- `
  -SignedBuildReportPath .\out\package\windows-x64\signed-build-report.json `
  -ExpectedSignerThumbprint '<repository production signer thumbprint>' `
  -StableMsiPath C:\release-evidence\PiRoundtable-<stable>-win-x64.msi `
  -ProductionLifecycleReportPath C:\release-evidence\production-lifecycle.json `
  -RealProviderEvidencePath C:\release-evidence\deepseek\evidence.json `
  -VisualReportPath96 C:\release-evidence\visual-96\theme-visual-qa-report.json `
  -VisualReportPath144 C:\release-evidence\visual-144\theme-visual-qa-report.json `
  -VisualReportPath192 C:\release-evidence\visual-192\theme-visual-qa-report.json
```

The gate re-verifies all signed artifact bytes and Authenticode timestamps,
runs a fresh full-payload lifecycle under the isolated QA UpgradeCode, verifies
the stable baseline bytes, validates the separate production clean-VM report,
validates a real-provider three-role report, and merges the real-DPI matrix. All
reports must agree with current `VERSION`, the clean Git commit, and the signed
candidate artifact hashes.

Publication is a separate explicit action. The publisher requires the same
fresh RC and signed-build report, revalidates every evidence descriptor and
artifact, compares the signer with the GitHub repository variable
`WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT`, creates or resumes the exact-commit
draft, re-downloads the exact five-asset set, and publishes only with
`-Publish`:

```powershell
pwsh -File .\scripts\publish-windows-release.ps1 `
  -ReleaseCandidateReportPath .\out\e2e\quality-gates\runs\<run>\quality-gate-report.json `
  -SignedBuildReportPath .\out\package\windows-x64\signed-build-report.json `
  -ReleaseDirectory .\out\installer `
  -ReleaseNotesPath .\docs\release\v0.4.0.md `
  -Publish
```

The pull-request CI Windows job is intentionally not equivalent: it may reuse
earlier full validation to suppress an expensive duplicate ICE pass and may
skip an interactive launch in its isolated lifecycle. CI is a merge gate, not
a release eligibility attestation.
