param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$AssetUrl,

    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'PiRoundtable\release-signing\windows-update-stable-2026-08-private.pem'),

    [string]$TrustedPublicKeyPath,

    [string]$OutputPath,

    [switch]$AuthenticodeRequired
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($TrustedPublicKeyPath)) {
    $TrustedPublicKeyPath = Join-Path $repoRoot 'packaging\windows-x64\update-public-key.pem'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
}

$resolvedMsi = [System.IO.Path]::GetFullPath($MsiPath)
$resolvedPrivateKey = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$resolvedPublicKey = [System.IO.Path]::GetFullPath($TrustedPublicKeyPath)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
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

$assetUri = $null
if (![Uri]::TryCreate($AssetUrl, [UriKind]::Absolute, [ref]$assetUri) -or
    $assetUri.Scheme -ne [Uri]::UriSchemeHttps -or
    [string]::IsNullOrWhiteSpace($assetUri.Host) -or
    ![string]::IsNullOrEmpty($assetUri.UserInfo) -or
    ![string]::IsNullOrEmpty($assetUri.Query) -or
    ![string]::IsNullOrEmpty($assetUri.Fragment)) {
    throw 'AssetUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
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
