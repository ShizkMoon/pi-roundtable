param(
    [string]$AppRoot = 'out\package\windows-x64\app',

    [string]$MsiPath = 'out\installer\PiRoundtable-0.3.0-win-x64.msi',

    [string]$OutputRoot = 'out\e2e\signing-pipeline'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'windows-signing.ps1')

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

$resolvedAppRoot = Resolve-RepositoryPath $AppRoot
$resolvedMsi = Resolve-RepositoryPath $MsiPath
$resolvedOutput = Resolve-RepositoryPath $OutputRoot
$approvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
if (!$resolvedOutput.StartsWith(
        $approvedOutputRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside $approvedOutputRoot."
}
foreach ($required in @(
    (Join-Path $resolvedAppRoot 'PiRoundtable.Updater.exe'),
    $resolvedMsi)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Signing smoke-test input does not exist: $required"
    }
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$copies = @(
    [ordered]@{
        source = Join-Path $resolvedAppRoot 'PiRoundtable.Updater.exe'
        copy = Join-Path $resolvedOutput 'PiRoundtable.Updater.signed-test.exe'
    },
    [ordered]@{
        source = $resolvedMsi
        copy = Join-Path $resolvedOutput 'PiRoundtable.signed-test.msi'
    }
)
foreach ($item in $copies) {
    Copy-Item -LiteralPath $item.source -Destination $item.copy -Force
    $item.unsignedSha256 = (Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash.ToUpperInvariant()
}

$certificate = $null
try {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=Pi Roundtable Ephemeral Signing QA $([Guid]::NewGuid().ToString('N'))" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(1)
    $signatures = Invoke-WindowsArtifactSigning `
        -Path @($copies | ForEach-Object copy) `
        -CertificateThumbprint $certificate.Thumbprint `
        -AllowUntimestamped

    foreach ($item in $copies) {
        $currentSourceHash = (Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($currentSourceHash -ne $item.unsignedSha256) {
            throw "Signing smoke test modified the original artifact: $($item.source)"
        }
        $item.signedSha256 = (Get-FileHash -LiteralPath $item.copy -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($item.signedSha256 -eq $item.unsignedSha256) {
            throw "Signed copy did not change: $($item.copy)"
        }
    }

    $certificatePath = $certificate.PSPath
    Remove-Item -LiteralPath $certificatePath -Force
    $certificate = $null
    if (Test-Path -LiteralPath $certificatePath) {
        throw 'The ephemeral signing certificate remained in the user certificate store.'
    }

    $report = [ordered]@{
        status = 'verified'
        trustScope = 'ephemeral self-signed pipeline test; not a production release identity'
        certificatePersisted = $false
        originalsUnchanged = $true
        copies = $copies
        signatures = $signatures
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $reportPath = Join-Path $resolvedOutput 'signing-pipeline-report.json'
    [System.IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    $report | ConvertTo-Json -Depth 8
} finally {
    if ($null -ne $certificate) {
        Remove-Item -LiteralPath $certificate.PSPath -Force -ErrorAction SilentlyContinue
    }
}
