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

The current installer is an unsigned local-alpha artifact. Administrative extraction and an extracted-payload launch are safe verification gates that do not register the product. Real install/uninstall, upgrade/repair, signing, and ARM64 packaging remain pending. When testing administrative extraction, use a short target path or a temporary drive mapping because some production Node dependencies have deep paths even though their normal `C:\Program Files\Pi Roundtable` paths remain below the Windows Installer limit.

WiX cannot encode the embedded nonnumeric language metadata in the Windows App SDK MUI files for `gd-gb`, `mi-NZ`, and `ug-CN`; the MSI omits those six localized MUI files and Windows falls back to neutral resources for those locales. Simplified Chinese, English, and the other published resources remain included.
