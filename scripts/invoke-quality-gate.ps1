param(
    [ValidateSet('Fast', 'Windows', 'ReleaseCandidate')]
    [string]$Scope = 'Fast',

    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Raw).Trim(),

    [string]$SignedBuildReportPath,

    [string]$StableMsiPath,

    [string]$ProductionLifecycleReportPath,

    [string[]]$VisualReportPath,

    [string]$VisualReportPath96,

    [string]$VisualReportPath144,

    [string]$VisualReportPath192
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$reportRoot = Join-Path $repoRoot 'out\e2e\quality-gates\runs'
$runId = '{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $reportRoot $runId
$artifactRoot = Join-Path $repoRoot ('out\q\{0}' -f $runId.Substring($runId.Length - 8))
$reportPath = Join-Path $runRoot 'quality-gate-report.json'
$startedAt = [DateTimeOffset]::UtcNow
$steps = [System.Collections.Generic.List[object]]::new()
$failure = $null
$sourceCommit = $null
$repositoryDirty = $null
$evidence = [ordered]@{}
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

function Assert-CommandAvailable {
    param([Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command is unavailable: $Name"
    }
}

function Resolve-InputPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Get-RedactedArguments {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ArgumentList)
    $sensitiveNames = @(
        '-CertificateThumbprint',
        '-PfxPath',
        '-PfxPasswordEnvironmentVariable',
        '-TimestampUrl')
    $redactNext = $false
    $display = foreach ($argument in $ArgumentList) {
        if ($redactNext) {
            '[redacted]'
            $redactNext = $false
        } else {
            $argument
            if ($argument -in $sensitiveNames) {
                $redactNext = $true
            }
        }
    }
    return $display
}

function Invoke-QualityCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ArgumentList
    )
    $stepStartedAt = [DateTimeOffset]::UtcNow
    $displayArguments = Get-RedactedArguments $ArgumentList
    Write-Host "[$Name] $FilePath $($displayArguments -join ' ')"
    try {
        Push-Location -LiteralPath $repoRoot
        try {
            & $FilePath @ArgumentList
            if ($LASTEXITCODE -ne 0) {
                throw "$FilePath failed with exit code $LASTEXITCODE."
            }
        } finally {
            Pop-Location
        }
        $steps.Add([ordered]@{
            name = $Name
            kind = 'command'
            status = 'passed'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
        })
    } catch {
        $steps.Add([ordered]@{
            name = $Name
            kind = 'command'
            status = 'failed'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
            errorType = $_.Exception.GetType().Name
            error = $_.Exception.Message
        })
        throw
    }
}

function Invoke-PowerShellParseGate {
    $stepStartedAt = [DateTimeOffset]::UtcNow
    try {
        $scriptCount = 0
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'scripts') -Filter '*.ps1' -File | ForEach-Object {
            $tokens = $null
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile(
                $_.FullName,
                [ref]$tokens,
                [ref]$errors)
            if ($errors.Count -gt 0) {
                throw "$($_.Name): $($errors[0].Message)"
            }
            $scriptCount++
        }
        $steps.Add([ordered]@{
            name = 'powershell-parse'
            kind = 'evidence-validation'
            status = 'verified'
            scriptCount = $scriptCount
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
        })
    } catch {
        $steps.Add([ordered]@{
            name = 'powershell-parse'
            kind = 'evidence-validation'
            status = 'failed'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
            errorType = $_.Exception.GetType().Name
            error = $_.Exception.Message
        })
        throw
    }
}

function Invoke-DirectEvidenceValidation {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Validation
    )
    $stepStartedAt = [DateTimeOffset]::UtcNow
    try {
        $details = & $Validation
        $steps.Add([ordered]@{
            name = $Name
            kind = 'evidence-validation'
            status = 'verified'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
        })
        return $details
    } catch {
        $steps.Add([ordered]@{
            name = $Name
            kind = 'evidence-validation'
            status = 'failed'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
            errorType = $_.Exception.GetType().Name
            error = $_.Exception.Message
        })
        throw
    }
}

function Read-VerifiedEvidenceReport {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][scriptblock]$Validation,
        [DateTimeOffset]$NotBefore = [DateTimeOffset]::MinValue,
        [TimeSpan]$MaximumAge = ([TimeSpan]::FromHours(24))
    )
    $stepStartedAt = [DateTimeOffset]::UtcNow
    try {
        $resolved = Resolve-InputPath $Path
        if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Evidence report does not exist: $resolved"
        }
        try {
            $report = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
        } catch {
            throw "Evidence report is not valid JSON: $resolved ($($_.Exception.Message))"
        }
        if ($report.status -ne 'verified') {
            throw "Evidence report is not verified: $resolved"
        }
        $evidenceId = [Guid]::Empty
        if (![Guid]::TryParse([string]$report.evidenceId, [ref]$evidenceId) -or $evidenceId -eq [Guid]::Empty) {
            throw "Evidence report has no valid non-empty evidenceId: $resolved"
        }
        $verifiedAt = [DateTimeOffset]::MinValue
        if (![DateTimeOffset]::TryParse([string]$report.verifiedAt, [ref]$verifiedAt)) {
            throw "Evidence report has no valid verifiedAt timestamp: $resolved"
        }
        $now = [DateTimeOffset]::UtcNow
        if ($verifiedAt -gt $now.AddMinutes(5) -or
            $verifiedAt -lt $NotBefore -or
            $now - $verifiedAt -gt $MaximumAge) {
            throw "Evidence report is stale, predates this run, or comes from the future: $resolved"
        }
        & $Validation $report $resolved
        $descriptor = [ordered]@{
            evidenceId = [string]$report.evidenceId
            path = $resolved
            sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToUpperInvariant()
            verifiedAt = $verifiedAt.ToString('O')
        }
        $steps.Add([ordered]@{
            name = $Name
            kind = 'evidence-validation'
            status = 'verified'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
        })
        return $descriptor
    } catch {
        $steps.Add([ordered]@{
            name = $Name
            kind = 'evidence-validation'
            status = 'failed'
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $stepStartedAt).TotalSeconds, 3)
            errorType = $_.Exception.GetType().Name
            error = $_.Exception.Message
        })
        throw
    }
}

function Assert-Elevated {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "$Scope quality requires an elevated administrator PowerShell session."
    }
}

function Assert-IsolatedLifecycleReport {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][string]$ExpectedCandidateVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedPayloadScope
    )
    if ($Report.schemaVersion -ne 2 -or
        $Report.evidenceClass -ne 'isolated-qa-msi-lifecycle' -or
        $Report.sourceCommit -ne $ExpectedCommit -or
        $Report.repositoryVersion -ne $ExpectedCandidateVersion -or
        $Report.candidate.productVersion -ne $ExpectedCandidateVersion -or
        $Report.payloadScope -ne $ExpectedPayloadScope -or
        $null -ne $Report.failure -or
        !$Report.isolation.productionRegistrationUnchanged -or
        @($Report.isolation.qaProductsRemaining).Count -ne 0) {
        throw 'The isolated MSI lifecycle report is not bound to this build or did not preserve machine isolation.'
    }
    $operations = @($Report.steps | ForEach-Object { [string]$_.operation })
    foreach ($requiredOperation in @('install', 'repair', 'repair-verification', 'launch', 'downgrade-verification', 'uninstall')) {
        if ($requiredOperation -notin $operations) {
            throw "The isolated MSI lifecycle report is missing operation: $requiredOperation"
        }
    }
    if (@($Report.steps | Where-Object { $_.operation -eq 'launch' -and $_.skipped }).Count -ne 0 -or
        @($Report.steps | Where-Object { $_.operation -eq 'repair' }).Count -lt 2 -or
        @($Report.steps | Where-Object { $_.operation -eq 'repair-verification' }).Count -lt 2 -or
        @($Report.steps | Where-Object { $_.operation -eq 'launch' }).Count -lt 2) {
        throw 'The isolated MSI lifecycle report skipped launch or did not exercise both repairs and launches.'
    }
}

function Get-ArtifactFromSignedReport {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][string]$Role
    )
    $matches = @($Report.artifacts | Where-Object { $_.role -eq $Role })
    if ($matches.Count -ne 1) {
        throw "Signed build report must contain exactly one artifact with role $Role."
    }
    return $matches[0]
}

try {
    $versionParts = @($Version.Split('.') | ForEach-Object { [uint32]$_ })
    if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
        throw 'Version exceeds Windows Installer limits (major/minor <= 255 and patch <= 65535).'
    }
    foreach ($command in @('git', 'node', 'npm', 'cmake', 'ctest')) {
        Assert-CommandAvailable $command
    }
    if ($Scope -ne 'Fast') {
        foreach ($command in @('dotnet', 'pwsh')) {
            Assert-CommandAvailable $command
        }
        Assert-Elevated
    }

    $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the current Git commit.'
    }
    $repositoryDirty = @(& git -C $repoRoot status --porcelain --untracked-files=normal).Count -ne 0
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect the repository state.'
    }
    $repositoryVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
    if ($Version -ne $repositoryVersion) {
        throw "Requested Version $Version does not match repository VERSION $repositoryVersion."
    }

    Invoke-QualityCommand 'repository-version' 'node' @('scripts/check-repository-version.mjs')
    Invoke-QualityCommand 'protocol-schema' 'node' @('scripts/check-protocol-schemas.mjs')

    if ($Scope -eq 'Fast') {
        Invoke-QualityCommand 'npm-ci' 'npm' @('ci')
        Invoke-QualityCommand 'typescript-tests' 'npm' @('test')
        Invoke-QualityCommand 'cpp-configure' 'cmake' @('--preset', 'dev')
        Invoke-QualityCommand 'cpp-build' 'cmake' @('--build', '--preset', 'dev')
        Invoke-QualityCommand 'cpp-tests' 'ctest' @('--preset', 'dev')
    } else {
        Invoke-PowerShellParseGate
        if ($Scope -eq 'Windows') {
            # WiX ICE validation uses legacy subprocess plumbing that fails with
            # an opaque broken-pipe error when harvested source paths are deep.
            # Keep durable reports descriptive, but stage heavy payloads under
            # one short, unique, ignored path.
            $packageRoot = Join-Path $artifactRoot 'p'
            $installerRoot = Join-Path $artifactRoot 'i'
            $candidateMsi = Join-Path $installerRoot "PiRoundtable-$Version-win-x64.msi"
            $signingRoot = Join-Path $artifactRoot 's'
            $lifecycleRoot = Join-Path $artifactRoot 'l'

            Invoke-QualityCommand 'windows-package' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/build-windows-x64.ps1',
                '-Version', $Version,
                '-OutputRoot', $packageRoot,
                '-InstallerOutputRoot', $installerRoot)
            Invoke-QualityCommand 'signing-pipeline-smoke' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/test-windows-signing-pipeline.ps1',
                '-AppRoot', (Join-Path $packageRoot 'app'),
                '-MsiPath', $candidateMsi,
                '-OutputRoot', $signingRoot)
            $signingReportPath = Join-Path $signingRoot 'signing-pipeline-report.json'
            $evidence.signingPipeline = Read-VerifiedEvidenceReport `
                -Name 'signing-pipeline-evidence' `
                -Path $signingReportPath `
                -NotBefore $startedAt `
                -Validation {
                    param($report)
                    if ($report.schemaVersion -ne 2 -or
                        $report.evidenceClass -ne 'ephemeral-signing-pipeline-smoke' -or
                        $report.productVersion -ne $Version -or
                        $report.sourceCommit -ne $sourceCommit -or
                        !$report.originalsUnchanged -or
                        $report.certificatePersisted -or
                        [string]$report.trustScope -notmatch '^ephemeral self-signed') {
                        throw 'The signing pipeline smoke report is inconsistent or is being misrepresented as production trust.'
                    }
                }

            Invoke-QualityCommand 'isolated-msi-lifecycle' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/test-windows-msi-lifecycle.ps1',
                '-PackageRoot', $packageRoot,
                '-OutputRoot', $lifecycleRoot,
                '-CandidateVersion', $Version)
            $lifecycleReportPath = Join-Path $lifecycleRoot 'msi-lifecycle-report.json'
            $evidence.isolatedLifecycle = Read-VerifiedEvidenceReport `
                -Name 'isolated-msi-lifecycle-evidence' `
                -Path $lifecycleReportPath `
                -NotBefore $startedAt `
                -Validation {
                    param($report)
                    Assert-IsolatedLifecycleReport `
                        -Report $report `
                        -ExpectedCandidateVersion $Version `
                        -ExpectedCommit $sourceCommit `
                        -ExpectedPayloadScope 'real self-contained WinUI application shell; runtime-host and Node excluded'
                }
        } else {
            if ($repositoryDirty) {
                throw 'ReleaseCandidate requires a clean repository so evidence can bind to one immutable commit.'
            }
            if ([string]::IsNullOrWhiteSpace($SignedBuildReportPath) -or
                [string]::IsNullOrWhiteSpace($StableMsiPath) -or
                [string]::IsNullOrWhiteSpace($ProductionLifecycleReportPath)) {
                throw 'ReleaseCandidate requires SignedBuildReportPath, StableMsiPath, and ProductionLifecycleReportPath.'
            }
            $explicitVisualPaths = @($VisualReportPath96, $VisualReportPath144, $VisualReportPath192)
            $hasVisualArray = $null -ne $VisualReportPath -and $VisualReportPath.Count -ne 0
            $hasExplicitVisualPaths = @($explicitVisualPaths | Where-Object {
                ![string]::IsNullOrWhiteSpace($_)
            }).Count -ne 0
            if ($hasVisualArray -eq $hasExplicitVisualPaths) {
                throw 'ReleaseCandidate requires either VisualReportPath with three values or all VisualReportPath96/144/192 values.'
            }
            $visualReportPaths = if ($hasVisualArray) { @($VisualReportPath) } else { $explicitVisualPaths }
            if ($visualReportPaths.Count -ne 3 -or
                @($visualReportPaths | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
                throw 'ReleaseCandidate requires exactly three non-empty real 96/144/192 DPI visual reports.'
            }

            $signedReportResolved = Resolve-InputPath $SignedBuildReportPath
            if (!(Test-Path -LiteralPath $signedReportResolved -PathType Leaf)) {
                throw "Signed build report does not exist: $signedReportResolved"
            }
            $signedReport = Get-Content -LiteralPath $signedReportResolved -Raw | ConvertFrom-Json
            $candidateMsiArtifact = Get-ArtifactFromSignedReport $signedReport 'installer'
            $candidateAppArtifact = Get-ArtifactFromSignedReport $signedReport 'windowsExecutable'
            $candidateMsi = Resolve-InputPath ([string]$candidateMsiArtifact.path)
            $candidateApp = Resolve-InputPath ([string]$candidateAppArtifact.path)
            $candidatePackageRoot = Resolve-InputPath ([string]$signedReport.packageRoot)

            $evidence.signedBuild = Read-VerifiedEvidenceReport `
                -Name 'production-signed-build-evidence' `
                -Path $signedReportResolved `
                -Validation {
                    param($report)
                    if ($report.schemaVersion -ne 1 -or
                        $report.evidenceClass -ne 'production-signed-windows-build' -or
                        $report.productVersion -ne $Version -or
                        $report.sourceCommit -ne $sourceCommit -or
                        $report.repositoryDirty -or
                        !$report.buildVerificationExecuted -or
                        !$report.msiValidationExecuted -or
                        !$report.trustedSignatureRequired -or
                        !$report.rfc3161TimestampRequired -or
                        $report.architecture -ne 'x64') {
                        throw 'Signed build evidence is not release-grade or is not bound to the current clean commit.'
                    }
                    $requiredRoles = @('windowsExecutable', 'windowsAssembly', 'updaterExecutable', 'nativeCore', 'installer')
                    foreach ($role in $requiredRoles) {
                        $artifact = Get-ArtifactFromSignedReport $report $role
                        $artifactPath = Resolve-InputPath ([string]$artifact.path)
                        if (!(Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
                            throw "Signed artifact is missing: $artifactPath"
                        }
                        $file = Get-Item -LiteralPath $artifactPath
                        $hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToUpperInvariant()
                        $signature = Get-AuthenticodeSignature -LiteralPath $artifactPath
                        if ($file.Length -ne [long]$artifact.size -or
                            $hash -ne [string]$artifact.sha256 -or
                            $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
                            $null -eq $signature.SignerCertificate -or
                            $null -eq $signature.TimeStamperCertificate -or
                            $signature.SignerCertificate.Thumbprint -ne [string]$artifact.signerThumbprint -or
                            !$artifact.timestamped) {
                            throw "Signed artifact identity, trust, or timestamp validation failed: $artifactPath"
                        }
                    }
                }

            $stableManifestPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
            $stableManifest = Get-Content -LiteralPath $stableManifestPath -Raw | ConvertFrom-Json
            $stableMsi = Resolve-InputPath $StableMsiPath
            $evidence.stableBaseline = Invoke-DirectEvidenceValidation 'stable-baseline-evidence' {
                if (!(Test-Path -LiteralPath $stableMsi -PathType Leaf)) {
                    throw "Stable MSI does not exist: $stableMsi"
                }
                if ($stableManifest.channel -ne 'stable' -or
                    $stableManifest.architecture -ne 'x64' -or
                    [Version]::Parse([string]$stableManifest.version) -ge [Version]::Parse($Version)) {
                    throw 'The committed stable manifest does not describe an older x64 stable baseline.'
                }
                $stableFile = Get-Item -LiteralPath $stableMsi
                $stableHash = (Get-FileHash -LiteralPath $stableMsi -Algorithm SHA256).Hash.ToUpperInvariant()
                if ($stableFile.Name -ne [string]$stableManifest.asset.fileName -or
                    $stableFile.Length -ne [long]$stableManifest.asset.size -or
                    $stableHash -ne [string]$stableManifest.asset.sha256) {
                    throw 'Stable MSI bytes do not match the separately signed stable update manifest.'
                }
                return [ordered]@{
                    manifestPath = $stableManifestPath
                    manifestSha256 = (Get-FileHash -LiteralPath $stableManifestPath -Algorithm SHA256).Hash.ToUpperInvariant()
                    version = [string]$stableManifest.version
                    msiPath = $stableMsi
                    msiSize = $stableFile.Length
                    msiSha256 = $stableHash
                }
            }

            $isolatedLifecycleRoot = Join-Path $artifactRoot 'l'
            Invoke-QualityCommand 'full-payload-isolated-msi-lifecycle' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/test-windows-msi-lifecycle.ps1',
                '-PackageRoot', $candidatePackageRoot,
                '-OutputRoot', $isolatedLifecycleRoot,
                '-UseFullPayload',
                '-BaselineVersion', [string]$stableManifest.version,
                '-CandidateVersion', $Version)
            $isolatedLifecycleReportPath = Join-Path $isolatedLifecycleRoot 'msi-lifecycle-report.json'
            $evidence.fullPayloadIsolatedLifecycle = Read-VerifiedEvidenceReport `
                -Name 'full-payload-isolated-lifecycle-evidence' `
                -Path $isolatedLifecycleReportPath `
                -NotBefore $startedAt `
                -Validation {
                    param($report)
                    Assert-IsolatedLifecycleReport `
                        -Report $report `
                        -ExpectedCandidateVersion $Version `
                        -ExpectedCommit $sourceCommit `
                        -ExpectedPayloadScope 'full-production-payload'
                }

            $candidateMsiHash = (Get-FileHash -LiteralPath $candidateMsi -Algorithm SHA256).Hash.ToUpperInvariant()
            $candidateMsiSize = (Get-Item -LiteralPath $candidateMsi).Length
            $evidence.productionLifecycle = Read-VerifiedEvidenceReport `
                -Name 'production-clean-vm-lifecycle-evidence' `
                -Path $ProductionLifecycleReportPath `
                -Validation {
                    param($report)
                    if ($report.schemaVersion -ne 1 -or
                        $report.evidenceClass -ne 'production-clean-vm-stable-to-candidate' -or
                        $report.sourceCommit -ne $sourceCommit -or
                        !$report.environment.cleanVm -or
                        $report.environment.architecture -ne 'x64' -or
                        [string]::IsNullOrWhiteSpace([string]$report.environment.vmImage) -or
                        [string]::IsNullOrWhiteSpace([string]$report.environment.snapshotId) -or
                        [string]::IsNullOrWhiteSpace([string]$report.environment.osBuild) -or
                        $report.baseline.version -ne [string]$stableManifest.version -or
                        $report.baseline.sha256 -ne [string]$stableManifest.asset.sha256 -or
                        [long]$report.baseline.size -ne [long]$stableManifest.asset.size -or
                        $report.baseline.fileName -ne [string]$stableManifest.asset.fileName -or
                        $report.candidate.version -ne $Version -or
                        $report.candidate.sha256 -ne $candidateMsiHash -or
                        [long]$report.candidate.size -ne $candidateMsiSize -or
                        $report.candidate.fileName -ne (Split-Path -Leaf $candidateMsi) -or
                        $report.rebootRequired -ne $false) {
                        throw 'Production lifecycle evidence is not bound to the stable and candidate artifacts under review.'
                    }
                    $lifecycleStartedAt = [DateTimeOffset]::MinValue
                    $lifecycleCompletedAt = [DateTimeOffset]::MinValue
                    if (![DateTimeOffset]::TryParse([string]$report.startedAt, [ref]$lifecycleStartedAt) -or
                        ![DateTimeOffset]::TryParse([string]$report.completedAt, [ref]$lifecycleCompletedAt) -or
                        $lifecycleCompletedAt -lt $lifecycleStartedAt) {
                        throw 'Production lifecycle evidence has an invalid execution interval.'
                    }
                    foreach ($check in @(
                        'installBaseline',
                        'launchBaseline',
                        'upgradeCandidate',
                        'launchCandidate',
                        'repairCandidate',
                        'downgradeBlocked',
                        'uninstallCandidate',
                        'noProductsRemaining')) {
                        if ($report.checks.$check -ne $true) {
                            throw "Production lifecycle evidence did not verify: $check"
                        }
                    }
                }

            $visualMatrixPath = Join-Path $runRoot 'windows-visual-matrix.json'
            $visualArguments = @(
                '-NoProfile',
                '-File', 'scripts/merge-windows-visual-matrix.ps1',
                '-ReportPath96', $visualReportPaths[0],
                '-ReportPath144', $visualReportPaths[1],
                '-ReportPath192', $visualReportPaths[2],
                    '-OutputPath', $visualMatrixPath,
                    '-MaximumAgeHours', '24')
            Invoke-QualityCommand 'real-dpi-visual-matrix' 'pwsh' $visualArguments
            $candidateAppHash = (Get-FileHash -LiteralPath $candidateApp -Algorithm SHA256).Hash.ToUpperInvariant()
            $evidence.visualMatrix = Read-VerifiedEvidenceReport `
                -Name 'real-dpi-visual-matrix-evidence' `
                -Path $visualMatrixPath `
                -NotBefore $startedAt `
                -Validation {
                    param($report)
                    if ($report.schemaVersion -ne 2 -or
                        $report.evidenceClass -ne 'real-windows-dpi-visual-matrix' -or
                        $report.productVersion -ne $Version -or
                        $report.sourceCommit -ne $sourceCommit -or
                        $report.appExecutableSha256 -ne $candidateAppHash -or
                        [int]$report.maximumSourceAgeHours -ne 24 -or
                        [DateTimeOffset]::Parse([string]$report.oldestSourceVerifiedAt) -lt [DateTimeOffset]::UtcNow.AddHours(-24) -or
                        (@($report.requiredDpis) -join ',') -ne '96,144,192' -or
                        (@($report.requiredThemes) -join ',') -ne 'light,dark,high-contrast' -or
                        (@($report.requiredViewportWidthsDip) -join ',') -ne '720,900,1280,1520') {
                        throw 'Visual matrix is not bound to the signed candidate executable or is incomplete.'
                    }
                }
        }
    }
} catch {
    $failure = $_.Exception.Message
    throw
} finally {
    $completedAt = [DateTimeOffset]::UtcNow
    $executionStatus = if ($null -eq $failure) { 'passed' } else { 'failed' }
    $evidenceStatus = if ($null -ne $failure) {
        'failed'
    } elseif ($Scope -eq 'Fast') {
        'not-applicable'
    } else {
        'verified'
    }
    $report = [ordered]@{
        schemaVersion = 2
        runId = $runId
        scope = $Scope
        status = $executionStatus
        evidenceStatus = $evidenceStatus
        releaseEligible = $Scope -eq 'ReleaseCandidate' -and $executionStatus -eq 'passed' -and $evidenceStatus -eq 'verified'
        productVersion = $Version
        sourceCommit = $sourceCommit
        repositoryDirty = $repositoryDirty
        artifactRoot = $artifactRoot
        startedAt = $startedAt.ToString('O')
        completedAt = $completedAt.ToString('O')
        failure = $failure
        steps = $steps
        evidence = $evidence
    }
    try {
        [System.IO.File]::WriteAllText(
            $reportPath,
            ($report | ConvertTo-Json -Depth 12),
            [System.Text.UTF8Encoding]::new($false))
        Write-Host "Quality gate report: $reportPath"
    } catch {
        if ($null -eq $failure) {
            throw
        }
        Write-Warning "Unable to write the failed quality report without masking the original error: $($_.Exception.Message)"
    }
}
