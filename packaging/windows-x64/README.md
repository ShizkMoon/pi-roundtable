# Windows x64 packaging

Run from the repository root with PowerShell 7:

```powershell
pwsh -File .\scripts\build-windows-x64.ps1
```

The script requires Node.js 24+, npm 12+, CMake/Ninja, and the pinned .NET 10 SDK. The 0.2.2 release reference environment is Node.js 24.16.0 with npm 12.0.1, which is also pinned in the Windows CI job. It runs the repository verification suite unless `-SkipVerification` is supplied, publishes the WinUI application self-contained, checks that generated XBF/PRI resources exist, bundles Node plus the production Pi Runtime Host dependencies, generates a deterministic WiX component manifest, and builds an embedded-cab WiX 4 MSI.

The default release path runs Windows Installer ICE validation and treats all warnings other than the two documented WinUI language metadata warnings as failures. GitHub Actions may pass `-SuppressMsiValidation` only after the equivalent release package has passed the default validation path on a Windows release machine. The switch still builds the complete MSI and does not skip project tests unless `-SkipVerification` is also explicit.

Outputs:

- staged application: `out\package\windows-x64\app`
- generated WiX manifest: `out\package\windows-x64\GeneratedFiles.wxs`
- installer: `out\installer\PiRoundtable-<version>-win-x64.msi`

The client updater uses a signed canonical manifest with an ECDSA P-256 public key pinned in the application. It then holds a cross-process directory lease and one no-follow package handle through bounded download, post-flush size/SHA-256 re-verification, and atomic promotion; strictly named crash-orphan leaves are cleaned only while that lease is held. If a manifest declares `authenticodeRequired: true`, Windows Authenticode trust must also succeed against that same locked file object; otherwise installation fails closed. `scripts/New-WindowsUpdateManifest.ps1` signs a release manifest using a private key outside the repository. Never commit that private key.

Without an external production certificate the MSI remains an unsigned local-alpha artifact, so release manifests must keep `authenticodeRequired: false`. Formal builds accept either an installed certificate thumbprint or a PFX outside the repository; the PFX password is read only from the process-scoped `PI_ROUNDTABLE_SIGNING_PFX_PASSWORD` (persisted user/machine values are rejected), imported non-exportable for the build, and removed in `finally`:

```powershell
$env:PI_ROUNDTABLE_SIGNING_PFX_PASSWORD = '<secret from CI secret store>'
pwsh -File .\scripts\build-signed-windows-x64.ps1 `
  -Version 0.3.0 `
  -PfxPath C:\secure\pi-roundtable-release.pfx `
  -TimestampUrl https://timestamp.example.com
```

The build signs first-party EXE/DLL files before generating `GeneratedFiles.wxs`, signs the MSI after WiX linking, verifies the selected signer and timestamp, and calculates release hashes last. It writes `out\package\windows-x64\signed-build-report.json`, binding all five signed artifacts to `VERSION`, the current Git commit, byte size, SHA-256, signer, and timestamper. A build using `-SkipVerification` or `-SuppressMsiValidation` is recorded only as `passed`; it cannot satisfy the Release Candidate gate. Pull-request/local mechanics can be tested without retaining a certificate:

```powershell
pwsh -File .\scripts\test-windows-signing-pipeline.ps1
```

That smoke test creates a one-day, non-exportable self-signed certificate in `CurrentUser\My`, signs copies of an application binary and MSI without modifying the originals, verifies signer identity, and removes the certificate in `finally`. Its report explicitly records that it is not trusted production signing.

Candidate promotion does not reuse the signed stable manifest as candidate metadata. The exact successful current-main CI run emits a separate version/commit/file/size/SHA-256/AuthentiCode-policy record; the promotion workflow requires the draft tag to match that version, verifies the candidate before upload, refuses to clobber a differing asset, and downloads the draft asset again for independent verification. The committed signed stable manifest remains the older upgrade baseline. When candidate metadata sets `authenticodeRequired` to `true`, promotion also requires the repository variable `WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT` to contain the expected 40-hex production leaf-certificate thumbprint. A merely trusted, timestamped signature from a different signer fails closed. PFX imports snapshot `CurrentUser\My`, clear the password environment variable before spawning the child build, and remove every certificate newly added to that store before exit.

The isolated lifecycle harness never registers the production UpgradeCode. By default it builds a compact MSI from the real WinUI application shell so CI can deterministically exercise the Windows Installer state machine; release machines should also run `-UseFullPayload` against all bundled runtime files:

```powershell
pwsh -File .\scripts\test-windows-msi-lifecycle.ps1
pwsh -File .\scripts\test-windows-msi-lifecycle.ps1 -UseFullPayload
```

Both modes cover baseline install, responsive app launch when enabled, file deletion and `/fomus` repair, major upgrade, blocked downgrade with candidate preservation, a second repair, uninstall, product/registry/shortcut/folder cleanup, and production-registration invariance. Administrative extraction remains a non-registering complementary gate. When testing extraction or the full lifecycle payload, use a short target path or a temporary drive mapping because some production Node dependencies have deep paths even though their normal `C:\Program Files\Pi Roundtable` paths remain below the Windows Installer limit.

The current Windows workstation has verified both modes, including the complete 22,594-file payload: baseline and candidate launches were responsive; the major upgrade completed; downgrade returned 1603 without mutating the candidate; both deliberate file deletions were repaired; uninstall left no QA ProductCode, directory, registry key, or Start Menu folder; and the production 0.2.2 registration was unchanged. These are local, time-bounded release-gate results rather than a permanent guarantee for later payloads.

Compact operations have a 12-minute per-`msiexec` limit; `-UseFullPayload` raises it to 45 minutes because a major upgrade must transact more than 22,000 components. `-MsiTimeoutMinutes` may override the limit on a slower release machine. If the limit is exceeded, the harness waits a bounded consistency grace period (and, if necessary, terminates and waits for the exact client process) before it attempts product enumeration or cleanup; it never disposes a live `msiexec` handle and reports a false-clean state.

Theme and scaling evidence is collected separately for each real desktop DPI:

```powershell
pwsh -File .\scripts\run-windows-theme-visual-qa.ps1 `
  -AppRoot .\out\package\windows-x64\app `
  -OutputRoot .\out\e2e\visual-144 `
  -ExpectedDpi 144

.\scripts\merge-windows-visual-matrix.ps1 -ReportPath @(
  '.\out\e2e\visual-96\theme-visual-qa-report.json'
  '.\out\e2e\visual-144\theme-visual-qa-report.json'
  '.\out\e2e\visual-192\theme-visual-qa-report.json'
)
```

The per-session script verifies light, dark, and real Windows high contrast at 720/900/1280/1520 DIP, restores the original high-contrast flags and scheme, and records actual `GetDpiForWindow` output. Each report is bound to the repository version, Git commit, and tested WinUI EXE SHA-256. The aggregate gate requires exactly the real 96/144/192 DPI evidence from the same executable; it does not emulate scaling. The final Release Candidate workflow and clean-VM production lifecycle report format are defined in [`docs/quality/release-candidate-evidence.md`](../../docs/quality/release-candidate-evidence.md). ARM64 packaging remains pending.

WiX cannot encode the embedded nonnumeric language metadata in the Windows App SDK MUI files for `gd-gb`, `mi-NZ`, and `ug-CN`; the MSI omits those six localized MUI files and Windows falls back to neutral resources for those locales. Simplified Chinese, English, and the other published resources remain included.

WiX 4 also reports two known `ICE03 File.Language` overflow warnings for the self-contained Windows App SDK binaries `Microsoft.ui.xaml.dll` and `Microsoft.UI.Xaml.Phone.dll`. Both files are still required and included. `DefaultLanguage` cannot override their PE metadata because the WiX linker replaces authored defaults with values read from the file; full MSI validation therefore remains enabled and the build must contain exactly these two warnings and no other ICE findings. Do not hide the condition with a global `SuppressIces` setting.
