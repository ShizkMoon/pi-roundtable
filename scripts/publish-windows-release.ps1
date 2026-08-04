param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Raw).Trim(),

    [Parameter(Mandatory = $true)]
    [string]$ReleaseCandidateReportPath,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseNotesPath,

    [string]$Repository = 'ShizkMoon/pi-roundtable',

    [string]$PublicationReportPath,

    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-ExistingFile {
    param([string]$Path, [string]$Label)
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Label does not exist: $resolved"
    }
    return $resolved
}

function Read-JsonFile {
    param([string]$Path, [string]$Label)
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "$Label is not valid JSON: $Path"
    }
}

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments, [string]$FailureMessage)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Assert-StrictChild {
    param([string]$Path, [string]$Root, [string]$Label)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (!$resolvedPath.StartsWith(
            $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain inside $resolvedRoot."
    }
    return $resolvedPath
}

function Read-RcEvidenceFile {
    param([object]$ReleaseCandidate, [string]$Name)
    $property = $ReleaseCandidate.evidence.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "ReleaseCandidate evidence descriptor is missing: $Name"
    }
    $descriptor = $property.Value
    $evidencePath = Resolve-ExistingFile ([string]$descriptor.path) "ReleaseCandidate evidence $Name"
    $bytes = [System.IO.File]::ReadAllBytes($evidencePath)
    $evidenceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($evidenceHash -ne [string]$descriptor.sha256) {
        throw "ReleaseCandidate evidence bytes changed after the gate: $Name"
    }
    try {
        $report = [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    } catch {
        throw "ReleaseCandidate evidence is not valid JSON: $Name"
    }
    $evidenceId = [Guid]::Empty
    $verifiedAt = [DateTimeOffset]::MinValue
    $descriptorVerifiedAt = [DateTimeOffset]::MinValue
    if ($report.status -ne 'verified' -or
        ![Guid]::TryParse([string]$report.evidenceId, [ref]$evidenceId) -or
        $evidenceId -eq [Guid]::Empty -or
        [string]$report.evidenceId -ne [string]$descriptor.evidenceId -or
        ![DateTimeOffset]::TryParse([string]$report.verifiedAt, [ref]$verifiedAt) -or
        ![DateTimeOffset]::TryParse([string]$descriptor.verifiedAt, [ref]$descriptorVerifiedAt) -or
        $verifiedAt -ne $descriptorVerifiedAt -or
        $verifiedAt -lt [DateTimeOffset]::UtcNow.AddHours(-24) -or
        $verifiedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "ReleaseCandidate evidence identity or freshness is invalid: $Name"
    }
    $startedProperty = $report.PSObject.Properties['startedAt']
    $completedProperty = $report.PSObject.Properties['completedAt']
    $startedAt = [DateTimeOffset]::MinValue
    if ($null -ne $startedProperty -and
        (![DateTimeOffset]::TryParse([string]$startedProperty.Value, [ref]$startedAt) -or
         $startedAt -gt $verifiedAt)) {
        throw "ReleaseCandidate evidence has an invalid execution start: $Name"
    }
    if ($null -ne $completedProperty) {
        $completedAt = [DateTimeOffset]::MinValue
        if (![DateTimeOffset]::TryParse([string]$completedProperty.Value, [ref]$completedAt) -or
            $completedAt -ne $verifiedAt -or
            ($null -ne $startedProperty -and $completedAt -lt $startedAt)) {
            throw "ReleaseCandidate evidence has an invalid execution completion: $Name"
        }
    }
    return [ordered]@{ path = $evidencePath; report = $report; sha256 = $evidenceHash; verifiedAt = $verifiedAt }
}

function Assert-OptionalAuthenticode {
    param([string]$MsiPath, [bool]$Required)
    if (!$Required) {
        return
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $MsiPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode is required by candidate metadata but is not trusted and timestamped: $MsiPath"
    }
}

function Assert-ExactReleaseAssetSet {
    param([object]$Release, [string]$Label)
    $assets = @($Release.assets)
    if ($assets.Count -ne $assetNames.Count -or
        @($assets | Where-Object { $_.name -notin $assetNames }).Count -ne 0) {
        throw "$Label does not contain the exact release asset set."
    }
    foreach ($assetName in $assetNames) {
        if (@($assets | Where-Object name -eq $assetName).Count -ne 1) {
            throw "$Label does not contain exactly one $assetName asset."
        }
    }
}

if ($Repository -ne 'ShizkMoon/pi-roundtable') {
    throw 'Publication is restricted to ShizkMoon/pi-roundtable.'
}
$repositoryVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($Version -ne $repositoryVersion) {
    throw "Requested Version $Version does not match repository VERSION $repositoryVersion."
}
if ((& git -C $repoRoot branch --show-current).Trim() -ne 'main') {
    throw 'Publication must run from main.'
}
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the current source commit.'
}
$gitStatus = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $gitStatus.Count -ne 0) {
    throw 'Publication requires a clean repository.'
}
Invoke-Checked 'git' @('-C', $repoRoot, 'fetch', 'origin', 'main', '--tags') 'Unable to refresh origin/main and release tags'
if ((& git -C $repoRoot rev-parse origin/main).Trim() -ne $sourceCommit) {
    throw 'The local release candidate is not the current origin/main commit.'
}

$rcPath = Resolve-ExistingFile $ReleaseCandidateReportPath 'ReleaseCandidate report'
$notesPath = Resolve-ExistingFile $ReleaseNotesPath 'Release notes'
$rcBytes = [System.IO.File]::ReadAllBytes($rcPath)
$rcHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rcBytes))
try {
    $rc = [System.Text.Encoding]::UTF8.GetString($rcBytes) | ConvertFrom-Json
} catch {
    throw "ReleaseCandidate report is not valid JSON: $rcPath"
}
$requiredEvidence = @('fullPayloadIsolatedLifecycle', 'releaseBuild')
$actualEvidence = @($rc.evidence.PSObject.Properties.Name)
if ($rc.schemaVersion -ne 2 -or $rc.scope -ne 'ReleaseCandidate' -or
    $rc.status -ne 'passed' -or $rc.evidenceStatus -ne 'verified' -or
    $rc.releaseEligible -ne $true -or $rc.productVersion -ne $Version -or
    $rc.sourceCommit -ne $sourceCommit -or $rc.repositoryDirty -ne $false -or
    (@($requiredEvidence | Sort-Object) -join ',') -ne (@($actualEvidence | Sort-Object) -join ',')) {
    throw 'ReleaseCandidate report is not eligible or is not bound to the current main commit.'
}
$rcCompletedAt = [DateTimeOffset]::MinValue
if (![DateTimeOffset]::TryParse([string]$rc.completedAt, [ref]$rcCompletedAt) -or
    $rcCompletedAt -lt [DateTimeOffset]::UtcNow.AddHours(-24) -or
    $rcCompletedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    throw 'ReleaseCandidate report is stale or has an invalid completion timestamp.'
}

$buildEvidence = Read-RcEvidenceFile $rc 'releaseBuild'
$lifecycleEvidence = Read-RcEvidenceFile $rc 'fullPayloadIsolatedLifecycle'
$rcRunRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $rcPath))
$artifactRoot = Assert-StrictChild ([string]$rc.artifactRoot) (Join-Path $repoRoot 'out') 'ReleaseCandidate artifact root'
$null = Assert-StrictChild $lifecycleEvidence.path $artifactRoot 'Lifecycle evidence path'
if (![string]::Equals(
        [System.IO.Path]::GetFullPath((Split-Path -Parent $buildEvidence.path)),
        $rcRunRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    $buildEvidence.verifiedAt -gt $rcCompletedAt -or
    $lifecycleEvidence.verifiedAt -gt $rcCompletedAt) {
    throw 'ReleaseCandidate evidence is not bound to this exact gate run.'
}
$build = $buildEvidence.report
$lifecycle = $lifecycleEvidence.report
$outputRoot = Join-Path $repoRoot 'out'
$packageRoot = Assert-StrictChild ([string]$build.packageRoot) $outputRoot 'Release package root'
$installerRoot = Assert-StrictChild ([string]$build.installerRoot) $outputRoot 'Release installer root'
$requiredRoles = @('installer', 'releaseMetadata', 'dependencyInventory', 'sbom', 'thirdPartyNotices')
$actualRoles = @($build.releaseAssets.role | Sort-Object)
if ($build.schemaVersion -ne 1 -or $build.evidenceClass -ne 'personal-windows-release-build' -or
    $build.productVersion -ne $Version -or $build.sourceCommit -ne $sourceCommit -or
    $build.repositoryDirty -ne $false -or !$build.buildVerificationExecuted -or
    !$build.msiValidationExecuted -or $build.architecture -ne 'x64' -or
    (@($requiredRoles | Sort-Object) -join ',') -ne ($actualRoles -join ',')) {
    throw 'Personal release build evidence no longer satisfies the release contract.'
}

$prefix = "PiRoundtable-$Version-win-x64"
$canonicalNames = @{
    installer = "$prefix.msi"
    releaseMetadata = "$prefix.release.json"
    dependencyInventory = "$prefix.dependencies.json"
    sbom = "$prefix.sbom.cdx.json"
    thirdPartyNotices = "$prefix.third-party-notices.txt"
}
$assetsByRole = @{}
$seenPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($role in $requiredRoles) {
    $matches = @($build.releaseAssets | Where-Object role -eq $role)
    if ($matches.Count -ne 1) {
        throw "Release build must contain exactly one asset for role $role."
    }
    $asset = $matches[0]
    $path = Resolve-ExistingFile ([string]$asset.path) "Release asset $role"
    $expectedPath = Join-Path $installerRoot $canonicalNames[$role]
    if (![string]::Equals($path, [System.IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase) -or
        !$seenPaths.Add($path) -or [string]$asset.fileName -ne $canonicalNames[$role] -or
        (Get-Item -LiteralPath $path).Length -ne [long]$asset.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$asset.sha256) {
        throw "Release asset is noncanonical or changed: $role"
    }
    $assetsByRole[$role] = [ordered]@{ descriptor = $asset; path = $path }
}
$appDescriptor = $build.applicationExecutable
$appPath = Resolve-ExistingFile ([string]$appDescriptor.path) 'Release application executable'
if (![string]::Equals(
        $appPath,
        [System.IO.Path]::GetFullPath((Join-Path $packageRoot 'app\PiRoundtable.Windows.exe')),
        [StringComparison]::OrdinalIgnoreCase) -or
    (Get-Item -LiteralPath $appPath).Length -ne [long]$appDescriptor.size -or
    (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$appDescriptor.sha256) {
    throw 'Release application executable changed after the candidate gate.'
}

$metadataPath = [string]$assetsByRole.releaseMetadata.path
$msiPath = [string]$assetsByRole.installer.path
$metadata = Read-JsonFile $metadataPath 'Release metadata'
if ($metadata.productVersion -ne $Version -or $metadata.sourceCommit -ne $sourceCommit -or
    $metadata.fileName -ne "$prefix.msi" -or
    [bool]$metadata.authenticodeRequired -ne [bool]$build.authenticodeRequired) {
    throw 'Release metadata is not bound to the candidate gate.'
}
Assert-OptionalAuthenticode -MsiPath $msiPath -Required ([bool]$metadata.authenticodeRequired)

$lifecycleOperations = @($lifecycle.steps | ForEach-Object { [string]$_.operation })
if ($lifecycle.schemaVersion -ne 2 -or
    $lifecycle.evidenceClass -ne 'isolated-qa-msi-lifecycle' -or
    $lifecycle.sourceCommit -ne $sourceCommit -or
    $lifecycle.repositoryVersion -ne $Version -or
    $lifecycle.candidate.productVersion -ne $Version -or
    $lifecycle.payloadScope -ne 'full-production-payload' -or
    $null -ne $lifecycle.failure -or
    !$lifecycle.isolation.productionRegistrationUnchanged -or
    @($lifecycle.isolation.qaProductsRemaining).Count -ne 0 -or
    @($lifecycle.steps | Where-Object { $_.operation -eq 'launch' -and $_.skipped }).Count -ne 0 -or
    @($lifecycle.steps | Where-Object operation -eq 'repair').Count -lt 2 -or
    @($lifecycle.steps | Where-Object operation -eq 'repair-verification').Count -lt 2 -or
    @($lifecycle.steps | Where-Object operation -eq 'launch').Count -lt 2 -or
    @($lifecycle.steps | Where-Object {
        $_.operation -eq 'payload-verification' -and
        $_.allManifestFilesPresent -and [int]$_.fileCount -ge 1000
    }).Count -ne 1 -or
    @($lifecycle.steps | Where-Object {
        $_.operation -eq 'runtime-smoke' -and
        $_.syntax -and $_.protocolImport -and $_.runtimeHostImport
    }).Count -lt 2 -or
    @('install', 'repair', 'repair-verification', 'launch', 'downgrade-verification', 'uninstall' |
        Where-Object { $_ -notin $lifecycleOperations }).Count -ne 0) {
    throw 'Full-payload lifecycle evidence no longer satisfies the release contract.'
}

$auditBase = if ([string]::IsNullOrWhiteSpace($PublicationReportPath)) {
    Join-Path $repoRoot "out\e2e\release-publication\v$Version"
} else {
    Split-Path -Parent ([System.IO.Path]::GetFullPath($PublicationReportPath))
}
$auditBase = Assert-StrictChild $auditBase $outputRoot 'Publication evidence directory'
New-Item -ItemType Directory -Force -Path $auditBase | Out-Null
$auditRoot = Join-Path $auditBase ("run-{0}" -f [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $auditRoot | Out-Null
$reportPath = if ([string]::IsNullOrWhiteSpace($PublicationReportPath)) {
    Join-Path $auditRoot 'release-publication-report.json'
} else {
    Assert-StrictChild ([System.IO.Path]::GetFullPath($PublicationReportPath)) $outputRoot 'Publication report'
}

$candidateSnapshotRoot = Join-Path $auditRoot 'candidate-assets'
New-Item -ItemType Directory -Path $candidateSnapshotRoot | Out-Null
foreach ($role in $requiredRoles) {
    $descriptor = $assetsByRole[$role].descriptor
    $snapshotPath = Join-Path $candidateSnapshotRoot $canonicalNames[$role]
    Copy-Item -LiteralPath ([string]$assetsByRole[$role].path) -Destination $snapshotPath
    if ((Get-Item -LiteralPath $snapshotPath).Length -ne [long]$descriptor.size -or
        (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$descriptor.sha256) {
        throw "Unable to create an immutable publication snapshot for $role."
    }
    $assetsByRole[$role].path = $snapshotPath
}
$installerRoot = $candidateSnapshotRoot
$metadataPath = [string]$assetsByRole.releaseMetadata.path
$msiPath = [string]$assetsByRole.installer.path
$metadata = Read-JsonFile $metadataPath 'Snapshot release metadata'
Assert-OptionalAuthenticode -MsiPath $msiPath -Required ([bool]$metadata.authenticodeRequired)

$originUrl = ([string](& git -C $repoRoot remote get-url origin)).Trim()
if ($LASTEXITCODE -ne 0 -or $originUrl -notmatch 'github\.com[:/]ShizkMoon/pi-roundtable(?:\.git)?$') {
    throw 'Git origin does not identify ShizkMoon/pi-roundtable.'
}
if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI is required for publication.'
}
Invoke-Checked 'gh' @('auth', 'status') 'GitHub CLI authentication is unavailable'
Invoke-Checked 'gh' @('repo', 'view', $Repository, '--json', 'nameWithOwner') 'Unable to access the publication repository'

$tag = "v$Version"
$stableManifestPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
$verifyArguments = @(
    'scripts/verify-windows-release-candidate.mjs',
    '--metadata', $metadataPath,
    '--msi', $msiPath,
    '--materials-directory', $installerRoot,
    '--stable-manifest', $stableManifestPath,
    '--version', $Version,
    '--source-commit', $sourceCommit,
    '--release-tag', $tag)
Push-Location -LiteralPath $repoRoot
try {
    Invoke-Checked 'node' $verifyArguments 'Local release asset verification failed'
} finally {
    Pop-Location
}

Invoke-Checked 'git' @('-C', $repoRoot, 'fetch', 'origin', 'main', '--tags') 'Unable to refresh main immediately before tagging'
if ((& git -C $repoRoot rev-parse origin/main).Trim() -ne $sourceCommit) {
    throw 'origin/main advanced while release evidence was being verified.'
}
& git -C $repoRoot show-ref --verify --quiet "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'git' @('-C', $repoRoot, 'tag', '--annotate', $tag, '--message', "Pi Roundtable $Version") "Unable to create tag $tag"
}
if ((& git -C $repoRoot cat-file -t "refs/tags/$tag").Trim() -ne 'tag' -or
    (& git -C $repoRoot rev-parse "$tag^{commit}").Trim() -ne $sourceCommit) {
    throw "Tag $tag must be annotated and point to $sourceCommit."
}
$existingRemoteTagLine = ([string](& git -C $repoRoot ls-remote origin "refs/tags/$tag^{}" )).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect remote tag $tag."
}
if ([string]::IsNullOrWhiteSpace($existingRemoteTagLine)) {
    Invoke-Checked 'git' @('-C', $repoRoot, 'push', 'origin', "refs/tags/$tag") "Unable to push tag $tag"
} elseif (($existingRemoteTagLine -split '\s+')[0] -ne $sourceCommit) {
    throw "Remote tag $tag does not point to $sourceCommit."
}

$assetNames = @($requiredRoles | ForEach-Object { $canonicalNames[$_] })
$releaseJson = & gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'gh' @('release', 'create', $tag, '--repo', $Repository, '--verify-tag', '--draft', '--title', "Pi Roundtable $Version", '--notes-file', $notesPath) "Unable to create draft release $tag"
    $releaseJson = & gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect draft release $tag."
    }
}
$release = $releaseJson | ConvertFrom-Json
if (!$release.isDraft -and !$Publish) {
    throw "Release $tag is already public; use -Publish for idempotent verification."
}
if (@($release.assets | Where-Object { $_.name -notin $assetNames }).Count -ne 0) {
    throw "Release $tag contains unexpected assets."
}
foreach ($role in $requiredRoles) {
    $assetPath = [string]$assetsByRole[$role].path
    $assetName = $canonicalNames[$role]
    $release = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
    $matches = @($release.assets | Where-Object name -eq $assetName)
    if ($matches.Count -gt 1) {
        throw "Release $tag contains duplicate asset $assetName."
    }
    if ($matches.Count -eq 0) {
        if (!$release.isDraft) {
            throw "Published release $tag is missing required asset $assetName."
        }
        Invoke-Checked 'gh' @('release', 'upload', $tag, '--repo', $Repository, $assetPath) "Unable to upload $assetName"
    }
}
$release = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
Assert-ExactReleaseAssetSet -Release $release -Label "Release $tag after upload"

function Download-AndVerifyReleaseAssets {
    param([string]$Destination, [string]$Label)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($role in $requiredRoles) {
        $assetName = $canonicalNames[$role]
        Invoke-Checked 'gh' @('release', 'download', $tag, '--repo', $Repository, '--pattern', $assetName, '--dir', $Destination) "Unable to download $Label asset $assetName"
        $localHash = (Get-FileHash -LiteralPath ([string]$assetsByRole[$role].path) -Algorithm SHA256).Hash.ToUpperInvariant()
        $downloadedHash = (Get-FileHash -LiteralPath (Join-Path $Destination $assetName) -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($localHash -ne $downloadedHash) {
            throw "$Label release asset differs from the candidate: $assetName"
        }
    }
    $downloadArguments = @($verifyArguments)
    $downloadArguments[2] = Join-Path $Destination "$prefix.release.json"
    $downloadArguments[4] = Join-Path $Destination "$prefix.msi"
    $downloadArguments[6] = $Destination
    Push-Location -LiteralPath $repoRoot
    try {
        Invoke-Checked 'node' $downloadArguments "$Label release asset verification failed"
    } finally {
        Pop-Location
    }
    Assert-OptionalAuthenticode `
        -MsiPath (Join-Path $Destination "$prefix.msi") `
        -Required ([bool]$metadata.authenticodeRequired)
}

$draftDownloadRoot = Join-Path $auditRoot 'draft-assets'
Download-AndVerifyReleaseAssets -Destination $draftDownloadRoot -Label 'Draft'
$prePublishRelease = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
Assert-ExactReleaseAssetSet -Release $prePublishRelease -Label "Release $tag before publication"
if (!$Publish -and (!$prePublishRelease.isDraft -or $prePublishRelease.isPrerelease)) {
    throw "Release $tag changed publication state during draft preparation."
}
$remoteMainLine = ([string](& git -C $repoRoot ls-remote origin 'refs/heads/main')).Trim()
$remoteTagLine = ([string](& git -C $repoRoot ls-remote origin "refs/tags/$tag^{}" )).Trim()
if ($LASTEXITCODE -ne 0 -or
    ($remoteMainLine -split '\s+')[0] -ne $sourceCommit -or
    ($remoteTagLine -split '\s+')[0] -ne $sourceCommit) {
    throw 'Remote main or the release tag changed before publication.'
}
$currentBranch = (& git -C $repoRoot branch --show-current).Trim()
$currentHead = (& git -C $repoRoot rev-parse HEAD).Trim()
$currentStatus = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $currentBranch -ne 'main' -or
    $currentHead -ne $sourceCommit -or $currentStatus.Count -ne 0) {
    throw 'The local main checkout changed before publication.'
}

if ($Publish) {
    $prePublicationRoot = Join-Path $auditRoot 'pre-publication-assets'
    Download-AndVerifyReleaseAssets -Destination $prePublicationRoot -Label 'Pre-publication'
    Invoke-Checked 'gh' @('release', 'edit', $tag, '--repo', $Repository, '--draft=false', '--prerelease=false', '--latest') "Unable to publish release $tag"
}

$finalRelease = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
if ($Publish -and ($finalRelease.isDraft -or $finalRelease.isPrerelease)) {
    throw "Release $tag did not become a public stable release."
}
if (!$Publish -and (!$finalRelease.isDraft -or $finalRelease.isPrerelease)) {
    throw "Release $tag did not remain a draft."
}
Assert-ExactReleaseAssetSet -Release $finalRelease -Label "Final release $tag"
$verifiedAssetRoot = $draftDownloadRoot
if ($Publish) {
    $publicDownloadRoot = Join-Path $auditRoot 'public-assets'
    Download-AndVerifyReleaseAssets -Destination $publicDownloadRoot -Label 'Public'
    $publicMainLine = ([string](& git -C $repoRoot ls-remote origin 'refs/heads/main')).Trim()
    $publicTagLine = ([string](& git -C $repoRoot ls-remote origin "refs/tags/$tag^{}" )).Trim()
    if ($LASTEXITCODE -ne 0 -or
        ($publicMainLine -split '\s+')[0] -ne $sourceCommit -or
        ($publicTagLine -split '\s+')[0] -ne $sourceCommit) {
        throw 'Public release verification observed a changed main commit or tag.'
    }
    $verifiedAssetRoot = $publicDownloadRoot
}

$publicationReport = [ordered]@{
    schemaVersion = 2
    evidenceId = [Guid]::NewGuid().ToString()
    status = $(if ($Publish) { 'verified' } else { 'prepared' })
    evidenceClass = $(if ($Publish) { 'github-release-publication' } else { 'github-release-draft-preparation' })
    productVersion = $Version
    sourceCommit = $sourceCommit
    releaseTag = $tag
    releaseUrl = [string]$finalRelease.url
    published = !$finalRelease.isDraft
    authenticodeRequired = [bool]$metadata.authenticodeRequired
    releaseCandidateReportSha256 = $rcHash
    releaseBuildReportSha256 = $buildEvidence.sha256
    assets = @($assetNames | ForEach-Object {
        $path = Join-Path $verifiedAssetRoot $_
        [ordered]@{
            fileName = $_
            size = (Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    })
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
[System.IO.File]::WriteAllText($reportPath, ($publicationReport | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
Write-Host "Release publication evidence: $reportPath"
Write-Host "Release URL: $($finalRelease.url)"
Write-Host "Published: $(!$finalRelease.isDraft)"
