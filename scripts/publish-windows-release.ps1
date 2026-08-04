param(
    [string]$Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Raw).Trim(),

    [Parameter(Mandatory = $true)]
    [string]$ReleaseCandidateReportPath,

    [Parameter(Mandatory = $true)]
    [string]$SignedBuildReportPath,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseNotesPath,

    [string]$Repository = 'ShizkMoon/pi-roundtable',

    [string]$PublicationReportPath,

    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-ExistingFile([string]$Path, [string]$Label) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Label does not exist: $resolved"
    }
    return $resolved
}

function Read-JsonFile([string]$Path, [string]$Label) {
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "$Label is not valid JSON: $Path"
    }
}

function Invoke-Checked([string]$Command, [string[]]$Arguments, [string]$FailureMessage) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Read-RcEvidenceFile([object]$ReleaseCandidate, [string]$Name) {
    $property = $ReleaseCandidate.evidence.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "ReleaseCandidate evidence descriptor is missing: $Name"
    }
    $descriptor = $property.Value
    $evidencePath = Resolve-ExistingFile ([string]$descriptor.path) "ReleaseCandidate evidence $Name"
    $evidenceHash = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($evidenceHash -ne [string]$descriptor.sha256) {
        throw "ReleaseCandidate evidence bytes changed after the gate: $Name"
    }
    $report = Read-JsonFile $evidencePath "ReleaseCandidate evidence $Name"
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
    if ($null -ne $startedProperty) {
        $evidenceStartedAt = [DateTimeOffset]::MinValue
        if (![DateTimeOffset]::TryParse([string]$startedProperty.Value, [ref]$evidenceStartedAt) -or
            $evidenceStartedAt -gt $verifiedAt) {
            throw "ReleaseCandidate evidence has an invalid execution start: $Name"
        }
    }
    if ($null -ne $completedProperty) {
        $evidenceCompletedAt = [DateTimeOffset]::MinValue
        if (![DateTimeOffset]::TryParse([string]$completedProperty.Value, [ref]$evidenceCompletedAt) -or
            $evidenceCompletedAt -ne $verifiedAt -or
            ($null -ne $startedProperty -and $evidenceCompletedAt -lt $evidenceStartedAt)) {
            throw "ReleaseCandidate evidence completion does not match its verification time: $Name"
        }
    }
    return [ordered]@{ path = $evidencePath; report = $report }
}

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'Version must be a canonical three-part version.'
}
if ($Repository -ne 'ShizkMoon/pi-roundtable') {
    throw 'Production publication is restricted to ShizkMoon/pi-roundtable.'
}
$repositoryVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($Version -ne $repositoryVersion) {
    throw "Requested Version $Version does not match repository VERSION $repositoryVersion."
}
if ((& git -C $repoRoot branch --show-current).Trim() -ne 'main') {
    throw 'Production publication must run from the protected main branch.'
}
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the current source commit.'
}
$gitStatus = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect repository cleanliness before production publication.'
}
if ($gitStatus.Count -ne 0) {
    throw 'Production publication requires a clean repository.'
}
Invoke-Checked 'git' @('-C', $repoRoot, 'fetch', 'origin', 'main', '--tags') 'Unable to refresh origin/main and release tags'
if ((& git -C $repoRoot rev-parse origin/main).Trim() -ne $sourceCommit) {
    throw 'The local candidate is not the current protected-main commit.'
}
$expectedSigner = ([string](& gh variable get WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT --repo $Repository 2>$null)).Trim().ToUpperInvariant()
if ($LASTEXITCODE -ne 0 -or $expectedSigner -notmatch '^[0-9A-F]{40}$') {
    throw 'Repository variable WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT must identify the production signing certificate.'
}

$rcPath = Resolve-ExistingFile $ReleaseCandidateReportPath 'ReleaseCandidate report'
$signedPath = Resolve-ExistingFile $SignedBuildReportPath 'Signed build report'
$notesPath = Resolve-ExistingFile $ReleaseNotesPath 'Release notes'
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
if (!(Test-Path -LiteralPath $releaseRoot -PathType Container)) {
    throw "ReleaseDirectory does not exist: $releaseRoot"
}
$rc = Read-JsonFile $rcPath 'ReleaseCandidate report'
$signed = Read-JsonFile $signedPath 'Signed build report'
$requiredEvidence = @('signedBuild', 'stableBaseline', 'fullPayloadIsolatedLifecycle', 'productionLifecycle', 'realProvider', 'visualMatrix')
$actualEvidence = @($rc.evidence.PSObject.Properties.Name)
if ($rc.schemaVersion -ne 2 -or $rc.scope -ne 'ReleaseCandidate' -or
    $rc.status -ne 'passed' -or $rc.evidenceStatus -ne 'verified' -or
    $rc.releaseEligible -ne $true -or $rc.productVersion -ne $Version -or
    $rc.sourceCommit -ne $sourceCommit -or $rc.repositoryDirty -ne $false -or
    (@($requiredEvidence | Sort-Object) -join ',') -ne (@($actualEvidence | Sort-Object) -join ',')) {
    throw 'ReleaseCandidate report is not release-eligible or is not bound to the current protected-main commit.'
}
$rcCompletedAt = [DateTimeOffset]::MinValue
if (![DateTimeOffset]::TryParse([string]$rc.completedAt, [ref]$rcCompletedAt) -or
    $rcCompletedAt -lt [DateTimeOffset]::UtcNow.AddHours(-24) -or
    $rcCompletedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    throw 'ReleaseCandidate report is stale or has an invalid completion timestamp.'
}
$signedEvidence = Read-RcEvidenceFile $rc 'signedBuild'
$fullLifecycleEvidence = Read-RcEvidenceFile $rc 'fullPayloadIsolatedLifecycle'
$productionLifecycleEvidence = Read-RcEvidenceFile $rc 'productionLifecycle'
$providerEvidence = Read-RcEvidenceFile $rc 'realProvider'
$visualMatrixEvidence = Read-RcEvidenceFile $rc 'visualMatrix'
if (![string]::Equals($signedEvidence.path, $signedPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The supplied signed-build report is not the exact report validated by ReleaseCandidate.'
}
$fullLifecycle = $fullLifecycleEvidence.report
$fullLifecycleOperations = @($fullLifecycle.steps | ForEach-Object { [string]$_.operation })
if ($fullLifecycle.schemaVersion -ne 2 -or
    $fullLifecycle.evidenceClass -ne 'isolated-qa-msi-lifecycle' -or
    $fullLifecycle.sourceCommit -ne $sourceCommit -or
    $fullLifecycle.repositoryVersion -ne $Version -or
    $fullLifecycle.candidate.productVersion -ne $Version -or
    $fullLifecycle.payloadScope -ne 'full-production-payload' -or
    $null -ne $fullLifecycle.failure -or
    !$fullLifecycle.isolation.productionRegistrationUnchanged -or
    @($fullLifecycle.isolation.qaProductsRemaining).Count -ne 0 -or
    @($fullLifecycle.steps | Where-Object { $_.operation -eq 'launch' -and $_.skipped }).Count -ne 0 -or
    @($fullLifecycle.steps | Where-Object operation -eq 'repair').Count -lt 2 -or
    @($fullLifecycle.steps | Where-Object operation -eq 'repair-verification').Count -lt 2 -or
    @($fullLifecycle.steps | Where-Object operation -eq 'launch').Count -lt 2 -or
    @('install', 'repair', 'repair-verification', 'launch', 'downgrade-verification', 'uninstall' |
        Where-Object { $_ -notin $fullLifecycleOperations }).Count -ne 0) {
    throw 'ReleaseCandidate full-payload lifecycle evidence no longer satisfies the release contract.'
}
$signedHash = (Get-FileHash -LiteralPath $signedPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($rc.evidence.signedBuild.sha256 -ne $signedHash -or
    $signed.schemaVersion -ne 1 -or $signed.status -ne 'verified' -or
    $signed.evidenceClass -ne 'production-signed-windows-build' -or
    $signed.productVersion -ne $Version -or $signed.sourceCommit -ne $sourceCommit -or
    $signed.repositoryDirty -ne $false -or !$signed.buildVerificationExecuted -or
    !$signed.msiValidationExecuted -or !$signed.trustedSignatureRequired -or
    !$signed.rfc3161TimestampRequired) {
    throw 'Signed build report is not release-grade or does not match the ReleaseCandidate report.'
}
$packageRoot = [IO.Path]::GetFullPath([string]$signed.packageRoot)
$installerRoot = [IO.Path]::GetFullPath([string]$signed.installerRoot)
$expectedSignedPaths = @{
    windowsExecutable = Join-Path $packageRoot 'app\PiRoundtable.Windows.exe'
    windowsAssembly = Join-Path $packageRoot 'app\PiRoundtable.Windows.dll'
    updaterExecutable = Join-Path $packageRoot 'app\PiRoundtable.Updater.exe'
    nativeCore = Join-Path $packageRoot 'app\pi_roundtable_core.dll'
    installer = Join-Path $installerRoot "PiRoundtable-$Version-win-x64.msi"
}
$signedArtifactsByRole = @{}
$seenSignedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($role in @('windowsExecutable', 'windowsAssembly', 'updaterExecutable', 'nativeCore', 'installer')) {
    $matches = @($signed.artifacts | Where-Object role -eq $role)
    if ($matches.Count -ne 1) { throw "Signed build report must contain exactly one $role artifact." }
    $artifact = $matches[0]
    $artifactPath = Resolve-ExistingFile ([string]$artifact.path) "Signed artifact $role"
    if (![string]::Equals($artifactPath, [IO.Path]::GetFullPath($expectedSignedPaths[$role]), [StringComparison]::OrdinalIgnoreCase) -or
        !$seenSignedPaths.Add($artifactPath)) {
        throw "Signed artifact has a noncanonical or duplicate role path: $role"
    }
    $artifactFile = Get-Item -LiteralPath $artifactPath
    $artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $artifactSignature = Get-AuthenticodeSignature -LiteralPath $artifactPath
    if ($artifactFile.Length -ne [long]$artifact.size -or
        $artifactHash -ne [string]$artifact.sha256 -or
        $artifactSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $artifactSignature.SignerCertificate -or
        $null -eq $artifactSignature.TimeStamperCertificate -or
        $artifactSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expectedSigner -or
        [string]$artifact.signerThumbprint -ne $expectedSigner -or
        !$artifact.timestamped) {
        throw "Signed artifact is not bound to the configured production signer: $role"
    }
    $signedArtifactsByRole[$role] = $artifact
}
$candidateAppHash = [string]$signedArtifactsByRole.windowsExecutable.sha256

$prefix = "PiRoundtable-$Version-win-x64"
$assetNames = @(
    "$prefix.msi",
    "$prefix.release.json",
    "$prefix.dependencies.json",
    "$prefix.sbom.cdx.json",
    "$prefix.third-party-notices.txt"
)
$assetPaths = @($assetNames | ForEach-Object { Resolve-ExistingFile (Join-Path $releaseRoot $_) "Release asset $_" })
$metadataPath = Join-Path $releaseRoot "$prefix.release.json"
$metadata = Read-JsonFile $metadataPath 'Release metadata'
if ($metadata.productVersion -ne $Version -or $metadata.sourceCommit -ne $sourceCommit -or
    $metadata.authenticodeRequired -ne $true -or $metadata.fileName -ne "$prefix.msi") {
    throw 'Release metadata is not a production-signed candidate bound to the current commit.'
}

$installerArtifact = @($signedArtifactsByRole.installer)
$msiPath = Join-Path $releaseRoot "$prefix.msi"
$msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToUpperInvariant()
$msiSignature = Get-AuthenticodeSignature -LiteralPath $msiPath
if ((Get-Item -LiteralPath $msiPath).Length -ne [long]$installerArtifact[0].size -or
    $msiHash -ne [string]$installerArtifact[0].sha256 -or
    $msiSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $msiSignature.SignerCertificate -or $null -eq $msiSignature.TimeStamperCertificate -or
    $msiSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne [string]$installerArtifact[0].signerThumbprint) {
    throw 'Release MSI does not match the trusted, timestamped signed-build artifact.'
}

$stableDescriptor = $rc.evidence.stableBaseline
$stableManifestPath = Resolve-ExistingFile ([string]$stableDescriptor.manifestPath) 'Stable manifest evidence'
$stableMsiPath = Resolve-ExistingFile ([string]$stableDescriptor.msiPath) 'Stable MSI evidence'
$stableManifestHash = (Get-FileHash -LiteralPath $stableManifestPath -Algorithm SHA256).Hash.ToUpperInvariant()
$stableMsiHash = (Get-FileHash -LiteralPath $stableMsiPath -Algorithm SHA256).Hash.ToUpperInvariant()
$stableManifest = Read-JsonFile $stableManifestPath 'Stable manifest evidence'
if ($stableManifestHash -ne [string]$stableDescriptor.manifestSha256 -or
    $stableManifest.version -ne [string]$stableDescriptor.version -or
    (Get-Item -LiteralPath $stableMsiPath).Length -ne [long]$stableDescriptor.msiSize -or
    $stableMsiHash -ne [string]$stableDescriptor.msiSha256 -or
    (Split-Path -Leaf $stableMsiPath) -ne [string]$stableManifest.asset.fileName -or
    (Get-Item -LiteralPath $stableMsiPath).Length -ne [long]$stableManifest.asset.size -or
    $stableMsiHash -ne [string]$stableManifest.asset.sha256 -or
    [version]$stableManifest.version -ge [version]$Version) {
    throw 'Stable baseline evidence changed or no longer matches the committed stable manifest.'
}
if ($stableManifest.asset.authenticodeRequired) {
    $stableSignature = Get-AuthenticodeSignature -LiteralPath $stableMsiPath
    if ($stableSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $stableSignature.SignerCertificate -or
        $null -eq $stableSignature.TimeStamperCertificate) {
        throw 'Stable baseline requires Authenticode but is not trusted and timestamped.'
    }
}

$productionLifecycle = $productionLifecycleEvidence.report
if ($productionLifecycle.schemaVersion -ne 1 -or
    $productionLifecycle.evidenceClass -ne 'production-clean-vm-stable-to-candidate' -or
    $productionLifecycle.sourceCommit -ne $sourceCommit -or
    !$productionLifecycle.environment.cleanVm -or
    !$productionLifecycle.environment.disposable -or
    !$productionLifecycle.environment.virtualMachineDetected -or
    $productionLifecycle.environment.architecture -ne 'x64' -or
    [string]::IsNullOrWhiteSpace([string]$productionLifecycle.environment.vmImage) -or
    [string]::IsNullOrWhiteSpace([string]$productionLifecycle.environment.snapshotId) -or
    [string]::IsNullOrWhiteSpace([string]$productionLifecycle.environment.osBuild) -or
    $productionLifecycle.baseline.version -ne [string]$stableManifest.version -or
    $productionLifecycle.baseline.fileName -ne [string]$stableManifest.asset.fileName -or
    $productionLifecycle.baseline.sha256 -ne $stableMsiHash -or
    [long]$productionLifecycle.baseline.size -ne (Get-Item -LiteralPath $stableMsiPath).Length -or
    $productionLifecycle.candidate.version -ne $Version -or
    $productionLifecycle.candidate.fileName -ne "$prefix.msi" -or
    $productionLifecycle.candidate.sha256 -ne $msiHash -or
    [long]$productionLifecycle.candidate.size -ne (Get-Item -LiteralPath $msiPath).Length -or
    $productionLifecycle.rebootRequired -ne $false -or
    @('installBaseline', 'launchBaseline', 'upgradeCandidate', 'launchCandidate', 'repairCandidate',
        'downgradeBlocked', 'uninstallCandidate', 'noProductsRemaining' | Where-Object {
            $productionLifecycle.checks.$_ -ne $true
        }).Count -ne 0) {
    throw 'Production clean-VM lifecycle evidence no longer satisfies the release contract.'
}

$provider = $providerEvidence.report
if ($provider.schemaVersion -ne 1 -or
    $provider.evidenceClass -ne 'real-provider-windows-roundtable' -or
    $provider.productVersion -ne $Version -or
    $provider.sourceCommit -ne $sourceCommit -or
    $provider.appExecutableSha256 -ne $candidateAppHash -or
    $provider.functionalStatus -ne 'verified' -or $provider.visualStatus -ne 'verified' -or
    $provider.client -ne 'PiRoundtable.Windows' -or $provider.provider -ne 'DeepSeek' -or
    [int]$provider.rounds -lt 3 -or @($provider.outputEvidence).Count -lt 5 -or
    (@($provider.scenarios) -join ',') -ne 'single-at-markdown,multi-at,free-discussion-autonomous-floor' -or
    $provider.credentialDeletedAfterRun -ne $true -or $provider.secretLeakScan -ne 'passed') {
    throw 'Real-provider evidence no longer satisfies the signed-candidate contract.'
}
if ($provider.facilitatedEvidence.initialMarkerVerified -ne $true -or
    $provider.facilitatedEvidence.initialLengthVerified -ne $true -or
    $provider.facilitatedEvidence.forbiddenLabelsAbsent -ne $true -or
    $provider.facilitatedEvidence.autonomousBoundaryChallengeVerified -ne $true) {
    throw 'Real-provider facilitated-discussion semantics are not verified.'
}
$providerRoot = [IO.Path]::GetFullPath((Split-Path -Parent $providerEvidence.path))
$requiredProviderRoles = @('session', 'screenshot.single-at-markdown', 'screenshot.multi-at', 'screenshot.free-discussion-autonomous-floor')
if ((@($provider.artifacts.role | Sort-Object) -join ',') -ne (@($requiredProviderRoles | Sort-Object) -join ',')) {
    throw 'Real-provider evidence does not contain the exact session and screenshot artifact set.'
}
foreach ($artifact in @($provider.artifacts)) {
    $artifactPath = Resolve-ExistingFile ([string]$artifact.path) "Real-provider artifact $($artifact.role)"
    if (!$artifactPath.StartsWith($providerRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Item -LiteralPath $artifactPath).Length -ne [long]$artifact.size -or
        (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$artifact.sha256) {
        throw "Real-provider artifact changed or escaped its run directory: $($artifact.role)"
    }
    if ([string]$artifact.role -like 'screenshot.*') {
        $header = [byte[]]::new(8)
        $stream = [IO.File]::OpenRead($artifactPath)
        try { $read = $stream.Read($header, 0, $header.Length) } finally { $stream.Dispose() }
        if ($read -ne 8 -or [Convert]::ToHexString($header) -ne '89504E470D0A1A0A') {
            throw "Real-provider screenshot artifact is not a PNG: $artifactPath"
        }
    }
}
$providerVisualPaths = @($provider.visualEvidence | Where-Object status -eq 'verified' | ForEach-Object {
    [IO.Path]::GetFullPath([string]$_.screenshot)
} | Sort-Object)
$providerScreenshotPaths = @($provider.artifacts | Where-Object { $_.role -like 'screenshot.*' } | ForEach-Object {
    [IO.Path]::GetFullPath([string]$_.path)
} | Sort-Object)
if ($providerVisualPaths.Count -ne 3 -or
    ($providerVisualPaths -join "`n") -cne ($providerScreenshotPaths -join "`n")) {
    throw 'Real-provider screenshots are not bound to the three verified visual evidence entries.'
}
$providerSessionArtifact = @($provider.artifacts | Where-Object role -eq 'session')[0]
$providerSessionPath = [IO.Path]::GetFullPath([string]$providerSessionArtifact.path)
if (![string]::Equals($providerSessionPath, [IO.Path]::GetFullPath([string]$provider.sessionFile), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Real-provider session artifact does not match sessionFile.'
}
$providerSession = Read-JsonFile $providerSessionPath 'Real-provider persisted session'
$actualProviderOutputs = @($providerSession.messages | Where-Object { $_.kind -eq 'role' -and $_.state -eq 'completed' } | ForEach-Object {
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]$_.text)
    try {
        '{0}|{1}|{2}|{3}' -f $_.speakerId, $_.speakerName, ([string]$_.text).Length,
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    } finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
} | Sort-Object)
$reportedProviderOutputs = @($provider.outputEvidence | ForEach-Object {
    '{0}|{1}|{2}|{3}' -f $_.speakerId, $_.speakerName, [int]$_.characterCount, [string]$_.sha256
} | Sort-Object)
if (($actualProviderOutputs -join "`n") -cne ($reportedProviderOutputs -join "`n")) {
    throw 'Real-provider output hashes no longer match the persisted session.'
}

$visualMatrix = $visualMatrixEvidence.report
if ($visualMatrix.schemaVersion -ne 2 -or
    $visualMatrix.evidenceClass -ne 'real-windows-dpi-visual-matrix' -or
    $visualMatrix.productVersion -ne $Version -or
    $visualMatrix.sourceCommit -ne $sourceCommit -or
    $visualMatrix.appExecutableSha256 -ne $candidateAppHash -or
    [int]$visualMatrix.maximumSourceAgeHours -ne 24 -or
    (@($visualMatrix.requiredDpis) -join ',') -ne '96,144,192' -or
    (@($visualMatrix.requiredThemes) -join ',') -ne 'light,dark,high-contrast' -or
    (@($visualMatrix.requiredViewportWidthsDip) -join ',') -ne '720,900,1280,1520' -or
    @($visualMatrix.runs).Count -ne 3) {
    throw 'Visual matrix no longer satisfies the signed-candidate contract.'
}
$seenDpis = [System.Collections.Generic.List[int]]::new()
foreach ($run in @($visualMatrix.runs)) {
    $runPath = Resolve-ExistingFile ([string]$run.path) 'Visual matrix source report'
    if ((Get-FileHash -LiteralPath $runPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$run.sha256) {
        throw "Visual matrix source report changed after aggregation: $runPath"
    }
    $dpiReport = Read-JsonFile $runPath 'Visual matrix source report'
    $dpi = [int]$dpiReport.dpi
    $verifiedAt = [DateTimeOffset]::MinValue
    if ($dpiReport.schemaVersion -ne 2 -or
        $dpiReport.status -ne 'verified' -or
        $dpiReport.evidenceClass -ne 'real-windows-theme-dpi-visual-qa' -or
        !$dpiReport.systemStateRestored -or
        $dpiReport.productVersion -ne $Version -or
        $dpiReport.sourceCommit -ne $sourceCommit -or
        $dpiReport.appExecutableSha256 -ne $candidateAppHash -or
        $dpi -notin @(96, 144, 192) -or [int]$dpiReport.expectedDpi -ne $dpi -or
        ![DateTimeOffset]::TryParse([string]$dpiReport.verifiedAt, [ref]$verifiedAt) -or
        $verifiedAt -lt [DateTimeOffset]::UtcNow.AddHours(-24) -or
        $verifiedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "Visual source report is stale or not bound to the candidate: $runPath"
    }
    $seenDpis.Add($dpi)
    $themeKinds = @($dpiReport.themes | ForEach-Object {
        if ($_.theme -eq 'system' -and $_.highContrast -eq $true) { 'high-contrast' }
        elseif ($_.theme -in @('light', 'dark') -and $_.highContrast -eq $false) { [string]$_.theme }
        else { 'invalid' }
    } | Sort-Object -Unique)
    if (($themeKinds -join ',') -ne 'dark,high-contrast,light') {
        throw "Visual source report is missing a required theme: $runPath"
    }
    foreach ($theme in @($dpiReport.themes)) {
        $widths = @($theme.report.measurements | ForEach-Object { [int]$_.viewportWidthDip } | Sort-Object -Unique)
        if ($theme.report.visualStatus -ne 'verified' -or
            @((720, 900, 1280, 1520) | Where-Object { $_ -notin $widths }).Count -ne 0) {
            throw "Visual source report has an incomplete responsive matrix: $runPath"
        }
    }
}
if ((@($seenDpis | Sort-Object -Unique) -join ',') -ne '96,144,192') {
    throw 'Visual matrix does not contain exactly one real 96, 144, and 192 DPI source report.'
}

$tag = "v$Version"
$verifierPath = Join-Path $repoRoot 'scripts\verify-windows-release-candidate.mjs'
$committedStableManifestPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
if (![string]::Equals(
        [IO.Path]::GetFullPath($stableManifestPath),
        [IO.Path]::GetFullPath($committedStableManifestPath),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ReleaseCandidate stable baseline is not bound to the committed stable manifest.'
}
$verifyArguments = @(
    $verifierPath,
    '--metadata', $metadataPath,
    '--msi', $msiPath,
    '--materials-directory', $releaseRoot,
    '--stable-manifest', $committedStableManifestPath,
    '--version', $Version,
    '--source-commit', $sourceCommit,
    '--release-tag', $tag
)
Invoke-Checked 'node' $verifyArguments 'Local release asset verification failed'

Invoke-Checked 'git' @('-C', $repoRoot, 'fetch', 'origin', 'main', '--tags') 'Unable to refresh protected main immediately before tagging'
if ((& git -C $repoRoot rev-parse origin/main).Trim() -ne $sourceCommit) {
    throw 'Protected main advanced while release evidence was being verified; rebuild evidence for the new main commit.'
}
$latestExpectedSigner = ([string](& gh variable get WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT --repo $Repository 2>$null)).Trim().ToUpperInvariant()
if ($LASTEXITCODE -ne 0 -or $latestExpectedSigner -ne $expectedSigner) {
    throw 'The configured production signing identity changed while release evidence was being verified.'
}
& git -C $repoRoot show-ref --verify --quiet "refs/tags/$tag"
if ($LASTEXITCODE -eq 0) {
    $tagCommit = (& git -C $repoRoot rev-parse "$tag^{commit}").Trim()
    if ($tagCommit -ne $sourceCommit) { throw "Tag $tag does not point to $sourceCommit." }
} else {
    Invoke-Checked 'git' @('-C', $repoRoot, 'tag', '--annotate', $tag, '--message', "Pi Roundtable $Version") "Unable to create tag $tag"
    Invoke-Checked 'git' @('-C', $repoRoot, 'push', 'origin', "refs/tags/$tag") "Unable to push tag $tag"
}

$releaseJson = & gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'gh' @('release', 'create', $tag, '--repo', $Repository, '--verify-tag', '--draft', '--title', "Pi Roundtable $Version", '--notes-file', $notesPath) "Unable to create draft release $tag"
    $releaseJson = & gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect draft release $tag." }
}
$release = $releaseJson | ConvertFrom-Json
if (!$release.isDraft -and !$Publish) {
    throw "Release $tag is already public; rerun with -Publish only for idempotent verification."
}
$unexpectedAssets = @($release.assets | Where-Object { $_.name -notin $assetNames })
if ($unexpectedAssets.Count -ne 0) {
    throw "Release $tag contains assets outside the exact production set: $($unexpectedAssets.name -join ', ')"
}

foreach ($assetPath in $assetPaths) {
    $assetName = [IO.Path]::GetFileName($assetPath)
    $release = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
    $matches = @($release.assets | Where-Object name -eq $assetName)
    if ($matches.Count -gt 1) { throw "Release $tag contains duplicate asset $assetName." }
    if ($matches.Count -eq 0) {
        if (!$release.isDraft) { throw "Published release $tag is missing required asset $assetName." }
        Invoke-Checked 'gh' @('release', 'upload', $tag, '--repo', $Repository, $assetPath) "Unable to upload $assetName"
    }
}

$auditRoot = if ([string]::IsNullOrWhiteSpace($PublicationReportPath)) {
    Join-Path $repoRoot ("out\e2e\release-publication\{0}-{1}" -f $tag, [Guid]::NewGuid().ToString('N').Substring(0, 8))
} else {
    Split-Path -Parent ([IO.Path]::GetFullPath($PublicationReportPath))
}
$approvedOutputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
$auditRoot = [IO.Path]::GetFullPath($auditRoot)
if (!$auditRoot.StartsWith(
        $approvedOutputRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "PublicationReportPath must remain inside $approvedOutputRoot."
}
New-Item -ItemType Directory -Force -Path $auditRoot | Out-Null
$downloadRoot = Join-Path $auditRoot 'downloaded-assets'
New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null
foreach ($assetName in $assetNames) {
    Invoke-Checked 'gh' @('release', 'download', $tag, '--repo', $Repository, '--pattern', $assetName, '--dir', $downloadRoot) "Unable to re-download $assetName"
    $localHash = (Get-FileHash -LiteralPath (Join-Path $releaseRoot $assetName) -Algorithm SHA256).Hash.ToUpperInvariant()
    $remoteHash = (Get-FileHash -LiteralPath (Join-Path $downloadRoot $assetName) -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($localHash -ne $remoteHash) { throw "Re-downloaded release asset differs: $assetName" }
}
$downloadedVerifyArguments = @($verifyArguments)
$downloadedVerifyArguments[2] = Join-Path $downloadRoot "$prefix.release.json"
$downloadedVerifyArguments[4] = Join-Path $downloadRoot "$prefix.msi"
$downloadedVerifyArguments[6] = $downloadRoot
Invoke-Checked 'node' $downloadedVerifyArguments 'Re-downloaded release verification failed'
$downloadedSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $downloadRoot "$prefix.msi")
if ($downloadedSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $downloadedSignature.TimeStamperCertificate -or
    $downloadedSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne [string]$installerArtifact[0].signerThumbprint) {
    throw 'Re-downloaded release MSI lost its trusted production signature or timestamp.'
}

$prePublishRelease = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
$prePublishAssets = @($prePublishRelease.assets)
if ($prePublishAssets.Count -ne $assetNames.Count -or
    @($prePublishAssets | Where-Object { $_.name -notin $assetNames }).Count -ne 0) {
    throw "Release $tag changed before the publication decision."
}
$remoteMainLine = ([string](& git -C $repoRoot ls-remote origin 'refs/heads/main')).Trim()
$remoteTagLine = ([string](& git -C $repoRoot ls-remote origin "refs/tags/$tag^{}" )).Trim()
if ($LASTEXITCODE -ne 0 -or
    ($remoteMainLine -split '\s+')[0] -ne $sourceCommit -or
    ($remoteTagLine -split '\s+')[0] -ne $sourceCommit) {
    throw 'Remote main or the dereferenced annotated release tag changed before publication.'
}
$finalExpectedSigner = ([string](& gh variable get WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT --repo $Repository 2>$null)).Trim().ToUpperInvariant()
if ($LASTEXITCODE -ne 0 -or $finalExpectedSigner -ne $expectedSigner) {
    throw 'The configured production signing identity changed before publication.'
}
if ($Publish) {
    $prePublicationRoot = Join-Path $auditRoot 'pre-publication-assets'
    New-Item -ItemType Directory -Force -Path $prePublicationRoot | Out-Null
    foreach ($assetName in $assetNames) {
        Invoke-Checked 'gh' @('release', 'download', $tag, '--repo', $Repository, '--pattern', $assetName, '--dir', $prePublicationRoot) "Unable to make the final pre-publication download of $assetName"
        if ((Get-FileHash -LiteralPath (Join-Path $prePublicationRoot $assetName) -Algorithm SHA256).Hash.ToUpperInvariant() -ne
            (Get-FileHash -LiteralPath (Join-Path $releaseRoot $assetName) -Algorithm SHA256).Hash.ToUpperInvariant()) {
            throw "Draft asset changed immediately before publication: $assetName"
        }
    }
    $prePublicationVerifyArguments = @($verifyArguments)
    $prePublicationVerifyArguments[2] = Join-Path $prePublicationRoot "$prefix.release.json"
    $prePublicationVerifyArguments[4] = Join-Path $prePublicationRoot "$prefix.msi"
    $prePublicationVerifyArguments[6] = $prePublicationRoot
    Invoke-Checked 'node' $prePublicationVerifyArguments 'Final pre-publication asset verification failed'
    $prePublicationSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $prePublicationRoot "$prefix.msi")
    if ($prePublicationSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $prePublicationSignature.SignerCertificate -or
        $null -eq $prePublicationSignature.TimeStamperCertificate -or
        $prePublicationSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expectedSigner) {
        throw 'Final pre-publication MSI signature validation failed.'
    }
    Invoke-Checked 'gh' @('release', 'edit', $tag, '--repo', $Repository, '--draft=false', '--prerelease=false', '--latest') "Unable to publish release $tag"
}
$finalRelease = (& gh release view $tag --repo $Repository --json isDraft,isPrerelease,url,assets | ConvertFrom-Json)
if ($Publish -and ($finalRelease.isDraft -or $finalRelease.isPrerelease)) {
    throw "Release $tag did not become a public stable release."
}
$finalAssets = @($finalRelease.assets)
if ($finalAssets.Count -ne $assetNames.Count -or
    @($finalAssets | Where-Object { $_.name -notin $assetNames }).Count -ne 0) {
    throw "Release $tag does not contain the exact required asset set."
}
$verifiedAssetRoot = $downloadRoot
if ($Publish) {
    $publicDownloadRoot = Join-Path $auditRoot 'public-assets'
    New-Item -ItemType Directory -Force -Path $publicDownloadRoot | Out-Null
    foreach ($assetName in $assetNames) {
        Invoke-Checked 'gh' @('release', 'download', $tag, '--repo', $Repository, '--pattern', $assetName, '--dir', $publicDownloadRoot) "Unable to verify public asset $assetName"
        $localHash = (Get-FileHash -LiteralPath (Join-Path $releaseRoot $assetName) -Algorithm SHA256).Hash.ToUpperInvariant()
        $publicHash = (Get-FileHash -LiteralPath (Join-Path $publicDownloadRoot $assetName) -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($localHash -ne $publicHash) { throw "Public release asset differs from the verified candidate: $assetName" }
    }
    $publicVerifyArguments = @($verifyArguments)
    $publicVerifyArguments[2] = Join-Path $publicDownloadRoot "$prefix.release.json"
    $publicVerifyArguments[4] = Join-Path $publicDownloadRoot "$prefix.msi"
    $publicVerifyArguments[6] = $publicDownloadRoot
    Invoke-Checked 'node' $publicVerifyArguments 'Public release asset verification failed'
    $publicSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $publicDownloadRoot "$prefix.msi")
    if ($publicSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $publicSignature.SignerCertificate -or
        $null -eq $publicSignature.TimeStamperCertificate -or
        $publicSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expectedSigner) {
        throw 'Public release MSI is not trusted, timestamped, and signed by the configured production certificate.'
    }
    $publicMainLine = ([string](& git -C $repoRoot ls-remote origin 'refs/heads/main')).Trim()
    $publicTagLine = ([string](& git -C $repoRoot ls-remote origin "refs/tags/$tag^{}" )).Trim()
    $publicExpectedSigner = ([string](& gh variable get WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT --repo $Repository 2>$null)).Trim().ToUpperInvariant()
    if ($LASTEXITCODE -ne 0 -or
        ($publicMainLine -split '\s+')[0] -ne $sourceCommit -or
        ($publicTagLine -split '\s+')[0] -ne $sourceCommit -or
        $publicExpectedSigner -ne $expectedSigner) {
        throw 'Public release verification observed a changed main commit, tag, or production signer.'
    }
    $verifiedAssetRoot = $publicDownloadRoot
}

$publicationReport = [ordered]@{
    schemaVersion = 1
    evidenceId = [Guid]::NewGuid().ToString()
    status = $(if ($Publish) { 'verified' } else { 'prepared' })
    evidenceClass = $(if ($Publish) { 'github-release-publication' } else { 'github-release-draft-preparation' })
    productVersion = $Version
    sourceCommit = $sourceCommit
    releaseTag = $tag
    releaseUrl = [string]$finalRelease.url
    published = !$finalRelease.isDraft
    releaseCandidateReportSha256 = (Get-FileHash -LiteralPath $rcPath -Algorithm SHA256).Hash.ToUpperInvariant()
    signedBuildReportSha256 = $signedHash
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
$reportPath = if ([string]::IsNullOrWhiteSpace($PublicationReportPath)) {
    Join-Path $auditRoot 'release-publication-report.json'
} else {
    [IO.Path]::GetFullPath($PublicationReportPath)
}
[IO.File]::WriteAllText($reportPath, ($publicationReport | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Host "Release publication evidence: $reportPath"
Write-Host "Release URL: $($finalRelease.url)"
Write-Host "Published: $(!$finalRelease.isDraft)"
