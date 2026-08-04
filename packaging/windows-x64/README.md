# Windows x64 packaging

Run from the repository root with PowerShell 7:

```powershell
pwsh -File .\scripts\build-windows-x64.ps1
```

The script requires Node.js 24+, npm 12+, CMake/Ninja, and the pinned .NET 10
SDK. It runs the repository verification suite, publishes the self-contained
WinUI application, bundles Node and the production Pi Runtime Host, generates a
deterministic WiX component manifest, and builds an embedded-cab WiX 4 MSI.

The normal release path runs Windows Installer ICE validation. Exactly two
known Windows App SDK language metadata warnings are allowed; an unknown,
missing, or additional ICE finding fails the build. `-SkipVerification` and
`-SuppressMsiValidation` are CI/development shortcuts and do not constitute a
ReleaseCandidate run.

Outputs:

- staged application: `out\package\windows-x64\app`;
- installer: `out\installer\PiRoundtable-<version>-win-x64.msi`;
- candidate metadata: `*.release.json`;
- dependency inventory, CycloneDX SBOM, and third-party notices.

## Personal-project release gate

The required v0.4 release gate has no certificate, VM, DPI, or provider input:

```powershell
npm run quality:release-candidate
```

It builds the exact clean commit with full tests and default ICE validation,
verifies the canonical five-asset set and hashes, then runs the complete
self-contained payload through the isolated QA installer lifecycle:

- baseline install and responsive launch;
- file deletion and `/fomus` repair;
- major upgrade and candidate launch;
- blocked downgrade without candidate mutation;
- second repair;
- uninstall and residue checks;
- unchanged production registration.

The lifecycle uses a dedicated QA ProductName, UpgradeCode, install directory,
registry path, and Start Menu directory. It never upgrades or removes the
production product. Compact runs have a 12-minute `msiexec` limit;
`-UseFullPayload` uses 45 minutes and waits for the exact installer process
before cleanup.

Publish only a fresh `releaseEligible: true` report:

```powershell
pwsh -File .\scripts\publish-windows-release.ps1 `
  -ReleaseCandidateReportPath .\out\e2e\quality-gates\runs\<run>\quality-gate-report.json `
  -ReleaseNotesPath .\docs\release\v0.4.0.md `
  -Publish
```

The publisher requires clean current `main`, verifies the annotated tag and the
exact five assets, refuses clobbering, and compares local, draft,
pre-publication, and public bytes.

## Update integrity and optional Authenticode

The client updater always requires a canonical manifest signed by the pinned
ECDSA P-256 key. It verifies the versioned HTTPS URL, exact file name, byte
count, SHA-256, architecture, channel, and manifest signature while holding a
no-follow package handle through staging and promotion.

Authenticode is optional for this personal project. An unsigned release writes
`authenticodeRequired: false`. If a future release sets it to `true`, the
existing updater, manifest generator, and publisher require Windows trust and a
timestamp and fail closed when either is missing.

After a public release is re-downloaded and verified, create the stable manifest
from those exact bytes. The private ECDSA key must stay outside the repository:

```powershell
pwsh -File .\scripts\New-WindowsUpdateManifest.ps1 `
  -MsiPath .\out\e2e\release-publication\<run>\public-assets\PiRoundtable-0.4.0-win-x64.msi `
  -Version 0.4.0 `
  -AssetUrl https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.4.0/PiRoundtable-0.4.0-win-x64.msi
```

Optional formal signing remains available through
`build-signed-windows-x64.ps1`; the PFX or certificate must remain outside the
repository. `test-windows-signing-pipeline.ps1` uses an ephemeral certificate
to test signing mechanics without claiming a production identity.

## Optional extended diagnostics

The following tools remain implemented but do not block a personal release:

- `test-windows-production-msi-lifecycle.ps1` for stable-to-candidate rehearsal
  on a detected disposable clean VM;
- `run-windows-theme-visual-qa.ps1` and `merge-windows-visual-matrix.ps1` for
  real 96/144/192 DPI coverage;
- `run-windows-deepseek-roundtable.ps1` for real-provider scenarios.

Each optional script retains its strict input and evidence checks. A result that
was not run is `pending`, never `verified`.

WiX omits six localized MUI files whose nonnumeric language metadata cannot be
encoded (`gd-gb`, `mi-NZ`, and `ug-CN`); Windows falls back to neutral resources.
The two allowed ICE03 warnings concern `Microsoft.ui.xaml.dll` and
`Microsoft.UI.Xaml.Phone.dll`; both binaries remain included.
