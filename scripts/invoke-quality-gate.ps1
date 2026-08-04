param(
    [ValidateSet('Fast', 'Windows', 'ReleaseCandidate')]
    [string]$Scope = 'Fast',

    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Raw).Trim()
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

function Invoke-QualityCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ArgumentList
    )
    $stepStartedAt = [DateTimeOffset]::UtcNow
    Write-Host "[$Name] $FilePath $($ArgumentList -join ' ')"
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
        if ($verifiedAt -gt $now.AddMinutes(5) -or $verifiedAt -lt $NotBefore -or $now - $verifiedAt -gt $MaximumAge) {
            throw "Evidence report is stale, predates this run, or comes from the future: $resolved"
        }
        $startedProperty = $report.PSObject.Properties['startedAt']
        $completedProperty = $report.PSObject.Properties['completedAt']
        $reportStartedAt = [DateTimeOffset]::MinValue
        if ($null -ne $startedProperty -and
            (![DateTimeOffset]::TryParse([string]$startedProperty.Value, [ref]$reportStartedAt) -or
             $reportStartedAt -gt $verifiedAt)) {
            throw "Evidence report has an invalid execution start: $resolved"
        }
        if ($null -ne $completedProperty) {
            $reportCompletedAt = [DateTimeOffset]::MinValue
            if (![DateTimeOffset]::TryParse([string]$completedProperty.Value, [ref]$reportCompletedAt) -or
                $reportCompletedAt -ne $verifiedAt -or
                ($null -ne $startedProperty -and $reportCompletedAt -lt $reportStartedAt)) {
                throw "Evidence report has an invalid execution completion: $resolved"
            }
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
    if ($ExpectedPayloadScope -eq 'full-production-payload') {
        $payloadChecks = @($Report.steps | Where-Object operation -eq 'payload-verification')
        $runtimeChecks = @($Report.steps | Where-Object operation -eq 'runtime-smoke')
        if ($payloadChecks.Count -ne 1 -or
            !$payloadChecks[0].allManifestFilesPresent -or
            [int]$payloadChecks[0].fileCount -lt 1000 -or
            $runtimeChecks.Count -lt 2 -or
            @($runtimeChecks | Where-Object {
                !$_.syntax -or !$_.protocolImport -or !$_.runtimeHostImport
            }).Count -ne 0) {
            throw 'The full-payload lifecycle did not verify every installed file and both Runtime Host smoke runs.'
        }
    }
}

function Get-FileEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $resolved = Resolve-InputPath $Path
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Release artifact is missing: $resolved"
    }
    $file = Get-Item -LiteralPath $resolved
    return [ordered]@{
        role = $Role
        path = $resolved
        fileName = $file.Name
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToUpperInvariant()
    }
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
    if ($Scope -eq 'ReleaseCandidate') {
        if ($repositoryDirty) {
            throw 'ReleaseCandidate requires a clean repository so the release is bound to one immutable commit.'
        }
        if ((& git -C $repoRoot branch --show-current).Trim() -ne 'main') {
            throw 'ReleaseCandidate must run from main.'
        }
        Invoke-QualityCommand 'release-main-refresh' 'git' @('-C', $repoRoot, 'fetch', 'origin', 'main')
        if ((& git -C $repoRoot rev-parse origin/main).Trim() -ne $sourceCommit) {
            throw 'ReleaseCandidate must run from the current origin/main commit.'
        }
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
        $packageRoot = Join-Path $artifactRoot 'p'
        $installerRoot = Join-Path $artifactRoot 'i'
        $candidateMsi = Join-Path $installerRoot "PiRoundtable-$Version-win-x64.msi"
        $lifecycleRoot = Join-Path $artifactRoot 'l'

        Invoke-QualityCommand 'windows-package' 'pwsh' @(
            '-NoProfile',
            '-File', 'scripts/build-windows-x64.ps1',
            '-Version', $Version,
            '-OutputRoot', $packageRoot,
            '-InstallerOutputRoot', $installerRoot)

        if ($Scope -eq 'Windows') {
            $signingRoot = Join-Path $artifactRoot 's'
            Invoke-QualityCommand 'signing-pipeline-smoke' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/test-windows-signing-pipeline.ps1',
                '-AppRoot', (Join-Path $packageRoot 'app'),
                '-MsiPath', $candidateMsi,
                '-OutputRoot', $signingRoot)
            $evidence.signingPipeline = Read-VerifiedEvidenceReport `
                -Name 'signing-pipeline-evidence' `
                -Path (Join-Path $signingRoot 'signing-pipeline-report.json') `
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
                        throw 'The signing pipeline smoke report is inconsistent.'
                    }
                }

            Invoke-QualityCommand 'isolated-msi-lifecycle' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/test-windows-msi-lifecycle.ps1',
                '-PackageRoot', $packageRoot,
                '-OutputRoot', $lifecycleRoot,
                '-CandidateVersion', $Version)
            $evidence.isolatedLifecycle = Read-VerifiedEvidenceReport `
                -Name 'isolated-msi-lifecycle-evidence' `
                -Path (Join-Path $lifecycleRoot 'msi-lifecycle-report.json') `
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
            $prefix = "PiRoundtable-$Version-win-x64"
            $metadataPath = Join-Path $installerRoot "$prefix.release.json"
            $stableManifestPath = Join-Path $repoRoot 'packaging\windows-x64\update-manifest.json'
            Invoke-QualityCommand 'release-candidate-asset-integrity' 'node' @(
                'scripts/verify-windows-release-candidate.mjs',
                '--metadata', $metadataPath,
                '--msi', $candidateMsi,
                '--materials-directory', $installerRoot,
                '--stable-manifest', $stableManifestPath,
                '--version', $Version,
                '--source-commit', $sourceCommit,
                '--release-tag', "v$Version")

            $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
            $releaseFiles = [ordered]@{
                installer = Join-Path $installerRoot "$prefix.msi"
                releaseMetadata = $metadataPath
                dependencyInventory = Join-Path $installerRoot "$prefix.dependencies.json"
                sbom = Join-Path $installerRoot "$prefix.sbom.cdx.json"
                thirdPartyNotices = Join-Path $installerRoot "$prefix.third-party-notices.txt"
            }
            $releaseAssets = @($releaseFiles.GetEnumerator() | ForEach-Object {
                Get-FileEvidence -Role $_.Key -Path $_.Value
            })
            $applicationExecutable = Get-FileEvidence `
                -Role 'windowsExecutable' `
                -Path (Join-Path $packageRoot 'app\PiRoundtable.Windows.exe')
            if ($metadata.authenticodeRequired) {
                $signature = Get-AuthenticodeSignature -LiteralPath $candidateMsi
                if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
                    $null -eq $signature.SignerCertificate -or
                    $null -eq $signature.TimeStamperCertificate) {
                    throw 'Candidate metadata requires Authenticode, but the MSI is not trusted and timestamped.'
                }
            }

            $buildVerifiedAt = [DateTimeOffset]::UtcNow
            $buildReportPath = Join-Path $runRoot 'personal-release-build-report.json'
            $buildReport = [ordered]@{
                schemaVersion = 1
                evidenceId = [Guid]::NewGuid().ToString()
                status = 'verified'
                evidenceClass = 'personal-windows-release-build'
                productVersion = $Version
                sourceCommit = $sourceCommit
                repositoryDirty = $false
                architecture = 'x64'
                buildVerificationExecuted = $true
                msiValidationExecuted = $true
                authenticodeRequired = [bool]$metadata.authenticodeRequired
                packageRoot = [System.IO.Path]::GetFullPath($packageRoot)
                installerRoot = [System.IO.Path]::GetFullPath($installerRoot)
                applicationExecutable = $applicationExecutable
                releaseAssets = $releaseAssets
                startedAt = $startedAt.ToString('O')
                completedAt = $buildVerifiedAt.ToString('O')
                verifiedAt = $buildVerifiedAt.ToString('O')
            }
            [System.IO.File]::WriteAllText(
                $buildReportPath,
                ($buildReport | ConvertTo-Json -Depth 8),
                [System.Text.UTF8Encoding]::new($false))

            $evidence.releaseBuild = Read-VerifiedEvidenceReport `
                -Name 'personal-release-build-evidence' `
                -Path $buildReportPath `
                -NotBefore $startedAt `
                -Validation {
                    param($report)
                    $roles = @($report.releaseAssets.role | Sort-Object)
                    if ($report.schemaVersion -ne 1 -or
                        $report.evidenceClass -ne 'personal-windows-release-build' -or
                        $report.productVersion -ne $Version -or
                        $report.sourceCommit -ne $sourceCommit -or
                        $report.repositoryDirty -ne $false -or
                        !$report.buildVerificationExecuted -or
                        !$report.msiValidationExecuted -or
                        $report.architecture -ne 'x64' -or
                        ($roles -join ',') -ne 'dependencyInventory,installer,releaseMetadata,sbom,thirdPartyNotices') {
                        throw 'Personal release build evidence is incomplete or bound to another build.'
                    }
                    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                    foreach ($asset in @($report.releaseAssets)) {
                        if (!$releaseFiles.Contains([string]$asset.role)) {
                            throw "Release evidence contains an unknown role: $($asset.role)"
                        }
                        $path = Resolve-InputPath ([string]$asset.path)
                        $expectedPath = [System.IO.Path]::GetFullPath([string]$releaseFiles[[string]$asset.role])
                        if (![string]::Equals($path, $expectedPath, [StringComparison]::OrdinalIgnoreCase) -or
                            [string]$asset.fileName -ne [System.IO.Path]::GetFileName($expectedPath) -or
                            !$seenPaths.Add($path) -or
                            !(Test-Path -LiteralPath $path -PathType Leaf) -or
                            (Get-Item -LiteralPath $path).Length -ne [long]$asset.size -or
                            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$asset.sha256) {
                            throw "Release artifact changed after verification: $path"
                        }
                    }
                    $appPath = Resolve-InputPath ([string]$report.applicationExecutable.path)
                    $expectedAppPath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot 'app\PiRoundtable.Windows.exe'))
                    if (![string]::Equals($appPath, $expectedAppPath, [StringComparison]::OrdinalIgnoreCase) -or
                        !(Test-Path -LiteralPath $appPath -PathType Leaf) -or
                        (Get-Item -LiteralPath $appPath).Length -ne [long]$report.applicationExecutable.size -or
                        (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$report.applicationExecutable.sha256) {
                        throw 'Release application executable changed after verification.'
                    }
                }

            $stableManifest = Get-Content -LiteralPath $stableManifestPath -Raw | ConvertFrom-Json
            Invoke-QualityCommand 'full-payload-isolated-msi-lifecycle' 'pwsh' @(
                '-NoProfile',
                '-File', 'scripts/test-windows-msi-lifecycle.ps1',
                '-PackageRoot', $packageRoot,
                '-OutputRoot', $lifecycleRoot,
                '-UseFullPayload',
                '-BaselineVersion', [string]$stableManifest.version,
                '-CandidateVersion', $Version)
            $evidence.fullPayloadIsolatedLifecycle = Read-VerifiedEvidenceReport `
                -Name 'full-payload-isolated-lifecycle-evidence' `
                -Path (Join-Path $lifecycleRoot 'msi-lifecycle-report.json') `
                -NotBefore $startedAt `
                -Validation {
                    param($report)
                    Assert-IsolatedLifecycleReport `
                        -Report $report `
                        -ExpectedCandidateVersion $Version `
                        -ExpectedCommit $sourceCommit `
                        -ExpectedPayloadScope 'full-production-payload'
                }

            foreach ($asset in $releaseAssets + @($applicationExecutable)) {
                $assetPath = Resolve-InputPath ([string]$asset.path)
                if ((Get-Item -LiteralPath $assetPath).Length -ne [long]$asset.size -or
                    (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne [string]$asset.sha256) {
                    throw "Release artifact changed while the lifecycle gate ran: $assetPath"
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
