param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$AssetUrl,

    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'PiRoundtable\release-signing\windows-update-stable-2026-08-private.pem'),

    [string]$TrustedPublicKeyPath,

    [string]$OutputPath,

    [string]$CurrentManifestPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$ExpectedSignerThumbprint,

    [switch]$AuthenticodeRequired
)

$ErrorActionPreference = 'Stop'
$versionParts = @($Version.Split('.') | ForEach-Object { [uint32]$_ })
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw 'Version exceeds Windows Installer limits (major/minor <= 255 and patch <= 65535).'
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'windows-packaging.ps1')
if ([string]::IsNullOrWhiteSpace($TrustedPublicKeyPath)) {
    $TrustedPublicKeyPath = Join-Path $repoRoot 'packaging\windows-x64\update-public-key.pem'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
}
if ([string]::IsNullOrWhiteSpace($CurrentManifestPath)) {
    $CurrentManifestPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
}

$resolvedMsi = [System.IO.Path]::GetFullPath($MsiPath)
$resolvedPrivateKey = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$resolvedPublicKey = [System.IO.Path]::GetFullPath($TrustedPublicKeyPath)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedCurrentManifest = [System.IO.Path]::GetFullPath($CurrentManifestPath)
if (!(Test-Path -LiteralPath $resolvedMsi -PathType Leaf) -or
    ![string]::Equals([System.IO.Path]::GetExtension($resolvedMsi), '.msi', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'MsiPath must identify an existing MSI file.'
}
if (!(Test-Path -LiteralPath $resolvedPrivateKey -PathType Leaf)) {
    throw 'The update signing private key does not exist.'
}
if (!(Test-Path -LiteralPath $resolvedPublicKey -PathType Leaf)) {
    throw 'The pinned update public key does not exist.'
}
if ($resolvedPrivateKey.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The update signing private key must remain outside the repository.'
}
if (!$AuthenticodeRequired) {
    throw 'Stable release manifests must require production Authenticode.'
}

$expectedFileName = "PiRoundtable-$Version-win-x64.msi"
$expectedAssetUrl = "https://github.com/ShizkMoon/pi-roundtable/releases/download/v$Version/$expectedFileName"
if (![string]::Equals([System.IO.Path]::GetFileName($resolvedMsi), $expectedFileName, [StringComparison]::Ordinal)) {
    throw "MsiPath must use the canonical release file name $expectedFileName."
}
$msiProductVersion = Get-MsiProperty -Path $resolvedMsi -Name 'ProductVersion'
$msiProductName = Get-MsiProperty -Path $resolvedMsi -Name 'ProductName'
$msiUpgradeCode = (Get-MsiProperty -Path $resolvedMsi -Name 'UpgradeCode').ToUpperInvariant()
if ($msiProductVersion -ne $Version -or
    $msiProductName -ne 'Pi Roundtable' -or
    $msiUpgradeCode -ne '{8F84BF2C-3DBB-4F28-8B97-78D8B384365A}') {
    throw 'MSI internal product identity does not match the requested production release.'
}
$expectedSigner = ($ExpectedSignerThumbprint -replace '\s', '').ToUpperInvariant()
$authenticode = Get-AuthenticodeSignature -LiteralPath $resolvedMsi
if ($authenticode.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $authenticode.SignerCertificate -or
    $null -eq $authenticode.TimeStamperCertificate -or
    $authenticode.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expectedSigner) {
    throw 'MSI must carry the expected trusted, RFC 3161 timestamped production Authenticode signature.'
}

$assetUri = $null
if (![Uri]::TryCreate($AssetUrl, [UriKind]::Absolute, [ref]$assetUri) -or
    $assetUri.Scheme -ne [Uri]::UriSchemeHttps -or
    [string]::IsNullOrWhiteSpace($assetUri.Host) -or
    ![string]::IsNullOrEmpty($assetUri.UserInfo) -or
    ![string]::IsNullOrEmpty($assetUri.Query) -or
    ![string]::IsNullOrEmpty($assetUri.Fragment) -or
    ![string]::Equals($assetUri.AbsoluteUri, $expectedAssetUrl, [StringComparison]::Ordinal)) {
    throw "AssetUrl must exactly match $expectedAssetUrl."
}

if (Test-Path -LiteralPath $resolvedCurrentManifest -PathType Leaf) {
    $currentManifest = Get-Content -LiteralPath $resolvedCurrentManifest -Raw | ConvertFrom-Json
    $currentVersion = $null
    if ($currentManifest.version -isnot [string] -or
        ![Version]::TryParse([string]$currentManifest.version, [ref]$currentVersion) -or
        $currentVersion.ToString(3) -ne [string]$currentManifest.version) {
        throw 'CurrentManifestPath does not contain a canonical three-part version.'
    }
    $candidateVersion = [Version]$Version
    if ($candidateVersion -le $currentVersion) {
        throw "Version $Version must be newer than the current stable version $currentVersion."
    }
}

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $privatePem = [System.IO.File]::ReadAllText($resolvedPrivateKey)
    $key.ImportFromPem($privatePem)
    $derivedPublicKey = $key.ExportSubjectPublicKeyInfoPem().Trim()
    $trustedPublicKey = [System.IO.File]::ReadAllText($resolvedPublicKey).Trim()
    if (![string]::Equals($derivedPublicKey, $trustedPublicKey, [StringComparison]::Ordinal)) {
        throw 'The private key does not match the public key pinned by the application.'
    }

    $file = Get-Item -LiteralPath $resolvedMsi
    $sha256 = (Get-FileHash -LiteralPath $resolvedMsi -Algorithm SHA256).Hash.ToUpperInvariant()
    $publishedAt = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    $authenticodeText = if ($AuthenticodeRequired) { 'true' } else { 'false' }
    $canonical = @(
        'manifestVersion=1'
        'productId=PiRoundtable.Windows'
        'channel=stable'
        'architecture=x64'
        "version=$Version"
        "publishedAt=$publishedAt"
        "asset.url=$($assetUri.AbsoluteUri)"
        "asset.fileName=$($file.Name)"
        "asset.size=$($file.Length.ToString([Globalization.CultureInfo]::InvariantCulture))"
        "asset.sha256=$sha256"
        "asset.authenticodeRequired=$authenticodeText"
        'signature.algorithm=ECDSA_P256_SHA256'
        'signature.keyId=stable-2026-08'
    ) -join "`n"
    $canonical += "`n"
    $canonicalBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($canonical)
    $signature = $key.SignData(
        $canonicalBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)

    $manifest = [ordered]@{
        manifestVersion = 1
        productId = 'PiRoundtable.Windows'
        channel = 'stable'
        architecture = 'x64'
        version = $Version
        publishedAt = $publishedAt
        asset = [ordered]@{
            url = $assetUri.AbsoluteUri
            fileName = $file.Name
            size = $file.Length
            sha256 = $sha256
            authenticodeRequired = [bool]$AuthenticodeRequired
        }
        signature = [ordered]@{
            algorithm = 'ECDSA_P256_SHA256'
            keyId = 'stable-2026-08'
            value = [Convert]::ToBase64String($signature)
        }
    }
    $outputDirectory = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    [System.IO.File]::WriteAllText(
        $resolvedOutput,
        ($manifest | ConvertTo-Json -Depth 5) + "`n",
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Signed update manifest: $resolvedOutput"
    Write-Host "Version: $Version"
    Write-Host "MSI SHA256: $sha256"
    Write-Host 'Authenticode required:' ([bool]$AuthenticodeRequired)
} finally {
    $key.Dispose()
}
