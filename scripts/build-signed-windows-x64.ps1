param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.3.0',

    [string]$CertificateThumbprint,

    [string]$PfxPath,

    [string]$PfxPasswordEnvironmentVariable = 'PI_ROUNDTABLE_SIGNING_PFX_PASSWORD',

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string]$TimestampUrl,

    [string]$OutputRoot,

    [string]$InstallerOutputRoot,

    [string]$NuGetConfigFile,

    [switch]$SkipVerification,

    [switch]$SuppressMsiValidation
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
        $passwordEnvironmentOriginal = [Environment]::GetEnvironmentVariable($PfxPasswordEnvironmentVariable)
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
