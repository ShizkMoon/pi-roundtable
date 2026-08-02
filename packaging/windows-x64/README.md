# Windows x64 packaging

Run from the repository root with PowerShell 7:

```powershell
pwsh -File .\scripts\build-windows-x64.ps1
```

The script requires Node.js 24+, CMake/Ninja, and the pinned .NET 10 SDK. It runs the repository verification suite unless `-SkipVerification` is supplied, publishes the WinUI application self-contained, checks that generated XBF/PRI resources exist, bundles Node plus the production Pi Runtime Host dependencies, generates a deterministic WiX component manifest, and builds an embedded-cab WiX 4 MSI.

Outputs:

- staged application: `out\package\windows-x64\app`
- generated WiX manifest: `out\package\windows-x64\GeneratedFiles.wxs`
- installer: `out\installer\PiRoundtable-<version>-win-x64.msi`

The client updater uses a signed canonical manifest with an ECDSA P-256 public key pinned in the application. It then verifies the exact MSI byte count and SHA-256 before atomically promoting a staged package. If a manifest declares `authenticodeRequired: true`, Windows Authenticode trust must also succeed; otherwise installation fails closed. `scripts/New-WindowsUpdateManifest.ps1` signs a release manifest using a private key outside the repository. Never commit that private key.

The current MSI itself remains an unsigned local-alpha artifact, so release manifests must keep `authenticodeRequired: false` until an Authenticode certificate is integrated. Administrative extraction and an extracted-payload launch are safe verification gates that do not register the product. Real install/uninstall and upgrade/repair matrices remain release gates; ARM64 packaging remains pending. When testing administrative extraction, use a short target path or a temporary drive mapping because some production Node dependencies have deep paths even though their normal `C:\Program Files\Pi Roundtable` paths remain below the Windows Installer limit.

WiX cannot encode the embedded nonnumeric language metadata in the Windows App SDK MUI files for `gd-gb`, `mi-NZ`, and `ug-CN`; the MSI omits those six localized MUI files and Windows falls back to neutral resources for those locales. Simplified Chinese, English, and the other published resources remain included.

WiX 4 also reports two known `ICE03 File.Language` overflow warnings for the self-contained Windows App SDK binaries `Microsoft.ui.xaml.dll` and `Microsoft.UI.Xaml.Phone.dll`. Both files are still required and included. `DefaultLanguage` cannot override their PE metadata because the WiX linker replaces authored defaults with values read from the file; full MSI validation therefore remains enabled and the build must contain exactly these two warnings and no other ICE findings. Do not hide the condition with a global `SuppressIces` setting.
