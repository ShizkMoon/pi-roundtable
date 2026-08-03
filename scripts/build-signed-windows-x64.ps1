param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Raw).Trim(),

    [string]$CertificateThumbprint,

    [string]$PfxPath,

    [string]$PfxPasswordEnvironmentVariable = 'PI_ROUNDTABLE_SIGNING_PFX_PASSWORD',

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string]$TimestampUrl,

    [string]$OutputRoot,

    [string]$InstallerOutputRoot,

    [string]$NuGetConfigFile,

    [string]$EvidenceOutputPath,

    [switch]$SkipVerification,

    [switch]$SuppressMsiValidation
)

$ErrorActionPreference = 'Stop'
$versionParts = @($Version.Split('.') | ForEach-Object { [uint32]$_ })
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw 'Version exceeds Windows Installer limits (major/minor <= 255 and patch <= 65535).'
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$startedAt = [DateTimeOffset]::UtcNow
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to bind the signed build to the current Git commit.'
}
$repositoryDirty = @(& git -C $repoRoot status --porcelain --untracked-files=normal).Count -ne 0
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the repository state for signed-build evidence.'
}
$resolvedPackageRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot 'out\package\windows-x64'
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$resolvedInstallerRoot = if ([string]::IsNullOrWhiteSpace($InstallerOutputRoot)) {
    Join-Path $repoRoot 'out\installer'
} else {
    [System.IO.Path]::GetFullPath($InstallerOutputRoot)
}
$resolvedEvidenceOutput = if ([string]::IsNullOrWhiteSpace($EvidenceOutputPath)) {
    Join-Path $resolvedPackageRoot 'signed-build-report.json'
} else {
    [System.IO.Path]::GetFullPath($EvidenceOutputPath)
}
$approvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
if (!$resolvedEvidenceOutput.StartsWith(
        $approvedOutputRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "EvidenceOutputPath must remain inside $approvedOutputRoot."
}
$certificateThumbprintsBefore = $null
$passwordEnvironmentOriginal = $null
$passwordEnvironmentCleared = $false
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -eq [string]::IsNullOrWhiteSpace($PfxPath)) {
    throw 'Specify exactly one of CertificateThumbprint or PfxPath.'
}

try {
    if (![string]::IsNullOrWhiteSpace($PfxPath)) {
        $resolvedPfx = [System.IO.Path]::GetFullPath($PfxPath)
        if (!(Test-Path -LiteralPath $resolvedPfx -PathType Leaf)) {
            throw "PFX file does not exist: $resolvedPfx"
        }
        if ($resolvedPfx.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The signing PFX must remain outside the repository.'
        }
        $certificateThumbprintsBefore = @(
            Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' |
                ForEach-Object { $_.Thumbprint.ToUpperInvariant() })
        $passwordEnvironmentOriginal = [Environment]::GetEnvironmentVariable(
            $PfxPasswordEnvironmentVariable,
            [EnvironmentVariableTarget]::Process)
        $persistedPasswordScopes = @(
            [Environment]::GetEnvironmentVariable($PfxPasswordEnvironmentVariable, [EnvironmentVariableTarget]::User),
            [Environment]::GetEnvironmentVariable($PfxPasswordEnvironmentVariable, [EnvironmentVariableTarget]::Machine)
        ) | Where-Object { ![string]::IsNullOrEmpty($_) }
        if ($persistedPasswordScopes.Count -ne 0) {
            throw "PFX password variable $PfxPasswordEnvironmentVariable must be process-scoped only; remove persisted user or machine values."
        }
        $passwordValue = $passwordEnvironmentOriginal
        if ([string]::IsNullOrEmpty($passwordValue)) {
            throw "Environment variable $PfxPasswordEnvironmentVariable does not contain the PFX password."
        }
        $securePassword = ConvertTo-SecureString $passwordValue -AsPlainText -Force
        $importedCertificates = @(
            Import-PfxCertificate `
                -FilePath $resolvedPfx `
                -CertStoreLocation 'Cert:\CurrentUser\My' `
                -Password $securePassword `
                -Exportable:$false)
        $passwordValue = $null
        $securePassword = $null
        [Environment]::SetEnvironmentVariable($PfxPasswordEnvironmentVariable, $null, 'Process')
        $passwordEnvironmentCleared = $true
        $signingCertificates = @($importedCertificates | Where-Object {
            $_.HasPrivateKey -and
            $_.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.3'
        })
        if ($signingCertificates.Count -ne 1) {
            throw "PFX must contain exactly one private-key certificate with the Code Signing EKU; found $($signingCertificates.Count)."
        }
        $CertificateThumbprint = $signingCertificates[0].Thumbprint
    }

    $arguments = @(
        '-NoProfile',
        '-File', (Join-Path $PSScriptRoot 'build-windows-x64.ps1'),
        '-Version', $Version,
        '-SigningCertificateThumbprint', $CertificateThumbprint,
        '-TimestampUrl', $TimestampUrl,
        '-RequireTrustedSignature'
    )
    if (![string]::IsNullOrWhiteSpace($OutputRoot)) { $arguments += @('-OutputRoot', $OutputRoot) }
    if (![string]::IsNullOrWhiteSpace($InstallerOutputRoot)) { $arguments += @('-InstallerOutputRoot', $InstallerOutputRoot) }
    if (![string]::IsNullOrWhiteSpace($NuGetConfigFile)) { $arguments += @('-NuGetConfigFile', $NuGetConfigFile) }
    if ($SkipVerification) { $arguments += '-SkipVerification' }
    if ($SuppressMsiValidation) { $arguments += '-SuppressMsiValidation' }
    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Signed Windows build failed with exit code $LASTEXITCODE."
    }

    $artifactPaths = [ordered]@{
        windowsExecutable = Join-Path $resolvedPackageRoot 'app\PiRoundtable.Windows.exe'
        windowsAssembly = Join-Path $resolvedPackageRoot 'app\PiRoundtable.Windows.dll'
        updaterExecutable = Join-Path $resolvedPackageRoot 'app\PiRoundtable.Updater.exe'
        nativeCore = Join-Path $resolvedPackageRoot 'app\pi_roundtable_core.dll'
        installer = Join-Path $resolvedInstallerRoot "PiRoundtable-$Version-win-x64.msi"
    }
    $artifacts = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $artifactPaths.GetEnumerator()) {
        if (!(Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
            throw "Signed build artifact is missing: $($entry.Value)"
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $entry.Value
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate -or
            $null -eq $signature.TimeStamperCertificate) {
            throw "Signed build artifact is not trusted and RFC 3161 timestamped: $($entry.Value) ($($signature.Status))."
        }
        $file = Get-Item -LiteralPath $entry.Value
        $artifacts.Add([ordered]@{
            role = $entry.Key
            path = $file.FullName
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            authenticodeStatus = $signature.Status.ToString()
            signerThumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
            timestamped = $true
            timestamperThumbprint = $signature.TimeStamperCertificate.Thumbprint.ToUpperInvariant()
        })
    }
    $report = [ordered]@{
        schemaVersion = 1
        evidenceId = [Guid]::NewGuid().ToString()
        status = $(if (!$SkipVerification -and !$SuppressMsiValidation) { 'verified' } else { 'passed' })
        evidenceClass = 'production-signed-windows-build'
        productVersion = $Version
        sourceCommit = $sourceCommit
        repositoryDirty = $repositoryDirty
        architecture = 'x64'
        buildVerificationExecuted = !$SkipVerification
        msiValidationExecuted = !$SuppressMsiValidation
        trustedSignatureRequired = $true
        rfc3161TimestampRequired = $true
        packageRoot = $resolvedPackageRoot
        installerRoot = $resolvedInstallerRoot
        artifacts = $artifacts
        startedAt = $startedAt.ToString('O')
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedEvidenceOutput) | Out-Null
    [System.IO.File]::WriteAllText(
        $resolvedEvidenceOutput,
        ($report | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Signed build evidence: $resolvedEvidenceOutput"
} finally {
    if ($null -ne $certificateThumbprintsBefore) {
        $newCertificates = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' | Where-Object {
            $_.Thumbprint.ToUpperInvariant() -notin $certificateThumbprintsBefore
        })
        foreach ($certificate in $newCertificates) {
            Remove-Item -LiteralPath $certificate.PSPath -Force -ErrorAction Stop
        }
        $remaining = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' | Where-Object {
            $_.Thumbprint.ToUpperInvariant() -notin $certificateThumbprintsBefore
        })
        if ($remaining.Count -ne 0) {
            throw 'One or more certificates imported from the signing PFX remained in CurrentUser\My.'
        }
    }
    if ($passwordEnvironmentCleared) {
        [Environment]::SetEnvironmentVariable(
            $PfxPasswordEnvironmentVariable,
            $passwordEnvironmentOriginal,
            'Process')
    }
}
