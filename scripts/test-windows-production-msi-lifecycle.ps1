param(
    [Parameter(Mandatory = $true)]
    [string]$StableMsiPath,

    [Parameter(Mandatory = $true)]
    [string]$CandidateMsiPath,

    [Parameter(Mandatory = $true)]
    [string]$SignedBuildReportPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$ExpectedSignerThumbprint,

    [string]$StableManifestPath = (Join-Path $PSScriptRoot '..\packaging\windows-x64\update-manifest.json'),

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$VmImage,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotId,

    [string]$OutputRoot,

    [ValidateRange(5, 120)]
    [int]$MsiTimeoutMinutes = 45,

    [Parameter(Mandatory = $true)]
    [switch]$DisposableCleanVm
)

$ErrorActionPreference = 'Stop'
$startedAt = [DateTimeOffset]::UtcNow
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$approvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
$runId = '{0}-{1}' -f $startedAt.ToString('yyyyMMdd-HHmmss-fff'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$resolvedOutput = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $approvedOutputRoot "e2e\production-msi-lifecycle-$runId"
} elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
if (!$resolvedOutput.StartsWith(
        $approvedOutputRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside $approvedOutputRoot."
}
if ((Test-Path -LiteralPath $resolvedOutput) -and
    @(Get-ChildItem -LiteralPath $resolvedOutput -Force).Count -ne 0) {
    throw 'OutputRoot must be new or empty so prior run state cannot contaminate production evidence.'
}

$logOutput = Join-Path $resolvedOutput 'logs'
$dataRoot = Join-Path $resolvedOutput 'app-data'
New-Item -ItemType Directory -Force -Path $resolvedOutput, $logOutput, $dataRoot | Out-Null
$reportPath = Join-Path $resolvedOutput 'production-msi-lifecycle-report.json'

. (Join-Path $PSScriptRoot 'windows-packaging.ps1')

$productionProductName = 'Pi Roundtable'
$productionUpgradeCode = [Guid]'8F84BF2C-3DBB-4F28-8B97-78D8B384365A'
$installDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'Pi Roundtable'
$registryPath = 'HKLM:\Software\PiRoundtable'
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)) 'Pi Roundtable'
$defaultUserDataDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'PiRoundtable'

$checks = [ordered]@{
    installBaseline = $false
    launchBaseline = $false
    upgradeCandidate = $false
    launchCandidate = $false
    repairCandidate = $false
    downgradeBlocked = $false
    uninstallCandidate = $false
    noProductsRemaining = $false
}
$steps = [System.Collections.Generic.List[object]]::new()
$cleanup = [System.Collections.Generic.List[object]]::new()
$failure = $null
$baseline = $null
$candidate = $null
$environment = $null
$baselineAttempted = $false
$candidateAttempted = $false
$rebootRequired = $false
$signedReport = $null
$sourceCommit = $null

if ($null -eq ('PiRoundtableProductionMsiNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PiRoundtableProductionMsiNative {
  [DllImport("msi.dll", CharSet = CharSet.Unicode)]
  private static extern uint MsiEnumRelatedProductsW(
    string upgradeCode,
    uint reserved,
    uint index,
    StringBuilder productCode);

  public static string[] RelatedProducts(string upgradeCode) {
    const uint ERROR_SUCCESS = 0;
    const uint ERROR_NO_MORE_ITEMS = 259;
    var products = new System.Collections.Generic.List<string>();
    for (uint index = 0; ; index++) {
      var buffer = new StringBuilder(39);
      var result = MsiEnumRelatedProductsW(upgradeCode, 0, index, buffer);
      if (result == ERROR_NO_MORE_ITEMS) break;
      if (result != ERROR_SUCCESS) {
        throw new ExternalException("MsiEnumRelatedProducts failed.", unchecked((int)result));
      }
      products.Add(buffer.ToString());
    }
    return products.ToArray();
  }
}
'@
}

function Get-RelatedProducts {
    return @([PiRoundtableProductionMsiNative]::RelatedProducts(
        "{$($productionUpgradeCode.ToString().ToUpperInvariant())}"))
}

function Get-InstalledProduct {
    param([Parameter(Mandatory = $true)][string]$ProductCode)
    foreach ($root in @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        $path = Join-Path $root $ProductCode
        if (Test-Path -LiteralPath $path) {
            return Get-ItemProperty -LiteralPath $path
        }
    }
    return $null
}

function Get-NamedInstalledProducts {
    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($root in @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (!(Test-Path -LiteralPath $root)) { continue }
        foreach ($item in @(Get-ChildItem -LiteralPath $root)) {
            $record = Get-ItemProperty -LiteralPath $item.PSPath
            if ([string]$record.DisplayName -eq $productionProductName) {
                $matches.Add([ordered]@{
                    productCode = $item.PSChildName
                    displayVersion = [string]$record.DisplayVersion
                })
            }
        }
    }
    return @($matches)
}

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('install', 'repair', 'uninstall')][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$LogName,
        [int[]]$AcceptedExitCodes = @(0)
    )

    $logPath = Join-Path $logOutput $LogName
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\msiexec.exe'))
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add($(switch ($Operation) {
        'install' { '/i' }
        'repair' { '/fomus' }
        'uninstall' { '/x' }
    }))
    $startInfo.ArgumentList.Add($Target)
    $startInfo.ArgumentList.Add('/qn')
    $startInfo.ArgumentList.Add('/norestart')
    $startInfo.ArgumentList.Add('/l*v')
    $startInfo.ArgumentList.Add($logPath)
    $process = [Diagnostics.Process]::Start($startInfo)
    $operationStartedAt = [DateTimeOffset]::UtcNow
    $exitCode = $null
    try {
        if (!$process.WaitForExit([int][TimeSpan]::FromMinutes($MsiTimeoutMinutes).TotalMilliseconds)) {
            $timeoutMessage = "msiexec exceeded the $MsiTimeoutMinutes minute limit for $Operation $Target."
            Write-Warning "$timeoutMessage Waiting up to 15 additional minutes before cancelling the exact client."
            if (!$process.WaitForExit([int][TimeSpan]::FromMinutes(15).TotalMilliseconds)) {
                if (!$process.HasExited) {
                    try { $process.Kill($true) } catch [InvalidOperationException] {}
                }
                $process.WaitForExit()
            }
            throw $timeoutMessage
        }
        $exitCode = $process.ExitCode
    } finally {
        $process.Dispose()
    }

    $result = [ordered]@{
        operation = $Operation
        target = $Target
        exitCode = $exitCode
        rebootRequired = $exitCode -eq 3010
        accepted = $exitCode -in $AcceptedExitCodes
        logPath = $logPath
        durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $operationStartedAt).TotalSeconds, 1)
    }
    $steps.Add($result)
    if ($result.rebootRequired) { $script:rebootRequired = $true }
    if (!$result.accepted) {
        throw "msiexec $Operation failed with exit code $exitCode. Log: $logPath"
    }
    return $result
}

function Assert-ProductInstalled {
    param(
        [Parameter(Mandatory = $true)][string]$ProductCode,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $record = Get-InstalledProduct $ProductCode
    if ($null -eq $record -or
        [string]$record.DisplayName -ne $productionProductName -or
        [string]$record.DisplayVersion -ne $Version) {
        throw "Expected $productionProductName $Version to be registered as $ProductCode."
    }
    if (!(Test-Path -LiteralPath (Join-Path $installDirectory 'PiRoundtable.Windows.exe') -PathType Leaf)) {
        throw "Installed application executable is missing from $installDirectory."
    }
}

function Invoke-InstalledAppSmoke {
    param([Parameter(Mandatory = $true)][string]$Label)

    $executable = Join-Path $installDirectory 'PiRoundtable.Windows.exe'
    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.WorkingDirectory = $installDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['PI_ROUNDTABLE_DATA_ROOT'] = $dataRoot
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 250
            $process.Refresh()
            if ($process.HasExited) {
                throw "Installed application exited early with code $($process.ExitCode)."
            }
        } while (($process.MainWindowHandle -eq 0 -or !$process.Responding) -and [DateTimeOffset]::UtcNow -lt $deadline)
        if ($process.MainWindowHandle -eq 0 -or !$process.Responding) {
            throw 'Installed application did not expose a responsive window within 30 seconds.'
        }
        $steps.Add([ordered]@{ operation = 'launch'; label = $Label; responsiveWindow = $true })
    } finally {
        if (!$process.HasExited) {
            [void]$process.CloseMainWindow()
            if (!$process.WaitForExit(10000)) {
                $process.Kill($true)
                $process.WaitForExit()
            }
        }
        $process.Dispose()
    }
}

function Get-PackageIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $file.FullName
        fileName = $file.Name
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        productCode = Get-MsiProperty -Path $file.FullName -Name 'ProductCode'
        version = Get-MsiProperty -Path $file.FullName -Name 'ProductVersion'
        productName = Get-MsiProperty -Path $file.FullName -Name 'ProductName'
        upgradeCode = Get-MsiProperty -Path $file.FullName -Name 'UpgradeCode'
    }
}

try {
    if (!$DisposableCleanVm) {
        throw 'Production lifecycle evidence requires the explicit -DisposableCleanVm attestation.'
    }
    if (![Environment]::Is64BitOperatingSystem) {
        throw 'Production lifecycle evidence requires a 64-bit Windows environment.'
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Production lifecycle evidence requires an elevated administrator PowerShell session.'
    }

    $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem
    $virtualizationIdentity = "{0} {1}" -f $computerSystem.Manufacturer, $computerSystem.Model
    $virtualMachineDetected = $virtualizationIdentity -match '(?i)virtual|vmware|virtualbox|kvm|hvm|parallels|qemu|xen|google compute engine|amazon ec2|openstack'
    if (!$virtualMachineDetected) {
        throw 'Production lifecycle evidence must run inside a detected virtual machine, not on the host workstation.'
    }
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $osBuild = "{0}.{1}" -f $os.BuildNumber, (Get-ItemPropertyValue `
        -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' `
        -Name UBR)
    $environment = [ordered]@{
        cleanVm = $true
        disposable = $true
        architecture = 'x64'
        vmImage = $VmImage
        snapshotId = $SnapshotId
        osBuild = $osBuild
        osCaption = [string]$os.Caption
        machineManufacturer = [string]$computerSystem.Manufacturer
        machineModel = [string]$computerSystem.Model
        hypervisorPresent = [bool]$computerSystem.HypervisorPresent
        virtualMachineDetected = $virtualMachineDetected
    }

    $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to bind production lifecycle evidence to the current Git commit.'
    }
    $gitStatus = @(& git -C $repoRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) {
        throw 'Production lifecycle evidence could not inspect the repository state.'
    }
    if ($gitStatus.Count -ne 0) {
        throw 'Production lifecycle evidence requires a clean repository.'
    }

    $stableMsi = [System.IO.Path]::GetFullPath($StableMsiPath)
    $candidateMsi = [System.IO.Path]::GetFullPath($CandidateMsiPath)
    $stableManifestResolved = [System.IO.Path]::GetFullPath($StableManifestPath)
    $signedReportResolved = [System.IO.Path]::GetFullPath($SignedBuildReportPath)
    foreach ($requiredFile in @($stableMsi, $candidateMsi, $stableManifestResolved, $signedReportResolved)) {
        if (!(Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Required lifecycle input does not exist: $requiredFile"
        }
    }

    $stableManifest = Get-Content -LiteralPath $stableManifestResolved -Raw | ConvertFrom-Json
    $signedReport = Get-Content -LiteralPath $signedReportResolved -Raw | ConvertFrom-Json
    $repositoryVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
    $baseline = Get-PackageIdentity $stableMsi
    $candidate = Get-PackageIdentity $candidateMsi
    $expectedUpgradeCode = "{$($productionUpgradeCode.ToString().ToUpperInvariant())}"
    $candidateArtifact = @($signedReport.artifacts | Where-Object role -eq 'installer')

    if ($stableManifest.manifestVersion -ne 1 -or
        $stableManifest.channel -ne 'stable' -or
        $stableManifest.architecture -ne 'x64' -or
        $baseline.fileName -ne [string]$stableManifest.asset.fileName -or
        $baseline.version -ne [string]$stableManifest.version -or
        $baseline.size -ne [long]$stableManifest.asset.size -or
        $baseline.sha256 -ne [string]$stableManifest.asset.sha256) {
        throw 'Stable MSI identity does not match the committed signed stable manifest.'
    }
    if ($baseline.productName -ne $productionProductName -or
        $baseline.upgradeCode.ToUpperInvariant() -ne $expectedUpgradeCode) {
        throw 'Stable MSI is not the production Pi Roundtable installer family.'
    }
    if ($stableManifest.asset.authenticodeRequired) {
        $stableSignature = Get-AuthenticodeSignature -LiteralPath $stableMsi
        if ($stableSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw 'Stable manifest requires Authenticode, but the stable MSI signature is not valid.'
        }
    }

    if ($signedReport.schemaVersion -ne 1 -or
        $signedReport.status -ne 'verified' -or
        $signedReport.evidenceClass -ne 'production-signed-windows-build' -or
        $signedReport.sourceCommit -ne $sourceCommit -or
        $signedReport.productVersion -ne $repositoryVersion -or
        $signedReport.repositoryDirty -or
        $signedReport.architecture -ne 'x64' -or
        !$signedReport.buildVerificationExecuted -or
        !$signedReport.msiValidationExecuted -or
        !$signedReport.trustedSignatureRequired -or
        !$signedReport.rfc3161TimestampRequired -or
        $candidateArtifact.Count -ne 1) {
        throw 'Signed build report is not production-grade or is not bound to this clean commit.'
    }
    $expectedCandidateName = "PiRoundtable-$($signedReport.productVersion)-win-x64.msi"
    if ($candidate.fileName -ne $expectedCandidateName -or
        $candidate.version -ne [string]$signedReport.productVersion -or
        $candidate.productName -ne $productionProductName -or
        $candidate.upgradeCode.ToUpperInvariant() -ne $expectedUpgradeCode -or
        $candidate.size -ne [long]$candidateArtifact[0].size -or
        $candidate.sha256 -ne [string]$candidateArtifact[0].sha256 -or
        ([version]$candidate.version -le [version]$baseline.version)) {
        throw 'Candidate MSI identity is invalid, is not newer, or does not match the signed build report.'
    }
    $candidateSignature = Get-AuthenticodeSignature -LiteralPath $candidateMsi
    $expectedSigner = ($ExpectedSignerThumbprint -replace '\s', '').ToUpperInvariant()
    if ($candidateSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $candidateSignature.SignerCertificate -or
        $null -eq $candidateSignature.TimeStamperCertificate -or
        $candidateSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expectedSigner -or
        [string]$candidateArtifact[0].signerThumbprint -ne $expectedSigner -or
        !$candidateArtifact[0].timestamped) {
        throw 'Candidate MSI is not trusted, timestamped, and bound to the production signing report.'
    }
    if ($baseline.productCode -eq $candidate.productCode) {
        throw 'Stable and candidate MSI packages must have distinct ProductCodes.'
    }

    $existingRelated = @(Get-RelatedProducts)
    $existingNamed = @(Get-NamedInstalledProducts)
    $existingExactProducts = @(
        Get-InstalledProduct ([string]$baseline.productCode)
        Get-InstalledProduct ([string]$candidate.productCode)
    ) | Where-Object { $null -ne $_ }
    $existingProcesses = @(Get-Process -Name 'PiRoundtable.Windows' -ErrorAction SilentlyContinue)
    $dirtyPaths = @($installDirectory, $registryPath, $startMenuDirectory, $defaultUserDataDirectory) |
        Where-Object { Test-Path -LiteralPath $_ }
    if ($existingRelated.Count -ne 0 -or $existingNamed.Count -ne 0 -or $existingExactProducts.Count -ne 0 -or
        $existingProcesses.Count -ne 0 -or $dirtyPaths.Count -ne 0) {
        throw 'Clean-VM preflight failed: Pi Roundtable is installed, running, or has pre-existing machine/user resources.'
    }

    $baselineAttempted = $true
    Invoke-MsiExec -Operation install -Target $stableMsi -LogName '01-install-stable.log' | Out-Null
    Assert-ProductInstalled -ProductCode $baseline.productCode -Version $baseline.version
    $checks.installBaseline = $true
    Invoke-InstalledAppSmoke -Label 'stable-baseline'
    $checks.launchBaseline = $true

    $candidateAttempted = $true
    Invoke-MsiExec -Operation install -Target $candidateMsi -LogName '02-upgrade-candidate.log' | Out-Null
    if ($null -ne (Get-InstalledProduct $baseline.productCode)) {
        throw 'Production major upgrade left the stable ProductCode registered.'
    }
    Assert-ProductInstalled -ProductCode $candidate.productCode -Version $candidate.version
    $checks.upgradeCandidate = $true
    Invoke-InstalledAppSmoke -Label 'candidate-after-upgrade'
    $checks.launchCandidate = $true

    $candidateCore = Join-Path $installDirectory 'pi_roundtable_core.dll'
    $candidateCoreHash = (Get-FileHash -LiteralPath $candidateCore -Algorithm SHA256).Hash.ToUpperInvariant()
    Remove-Item -LiteralPath $candidateCore -Force
    Invoke-MsiExec -Operation repair -Target $candidate.productCode -LogName '03-repair-candidate.log' | Out-Null
    $repairedCandidateCoreHash = (Get-FileHash -LiteralPath $candidateCore -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($repairedCandidateCoreHash -ne $candidateCoreHash) {
        throw 'Candidate repair did not restore the original native core.'
    }
    $steps.Add([ordered]@{
        operation = 'repair-verification'
        restoredFile = 'pi_roundtable_core.dll'
        restoredSha256 = $repairedCandidateCoreHash
    })
    $checks.repairCandidate = $true

    $candidateExecutable = Join-Path $installDirectory 'PiRoundtable.Windows.exe'
    $candidateHashBeforeDowngrade = (Get-FileHash -LiteralPath $candidateExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    $downgrade = Invoke-MsiExec `
        -Operation install `
        -Target $stableMsi `
        -LogName '04-downgrade-block.log' `
        -AcceptedExitCodes @(1603, 1638)
    Assert-ProductInstalled -ProductCode $candidate.productCode -Version $candidate.version
    $candidateHashAfterDowngrade = (Get-FileHash -LiteralPath $candidateExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($candidateHashAfterDowngrade -ne $candidateHashBeforeDowngrade) {
        throw 'Blocked downgrade changed the installed candidate payload.'
    }
    $steps.Add([ordered]@{
        operation = 'downgrade-verification'
        blocked = $true
        exitCode = $downgrade.exitCode
        installedSha256 = $candidateHashAfterDowngrade
    })
    $checks.downgradeBlocked = $true

    Invoke-MsiExec -Operation uninstall -Target $candidate.productCode -LogName '05-uninstall-candidate.log' | Out-Null
    $checks.uninstallCandidate = $true
    $remainingRelated = @(Get-RelatedProducts)
    $remainingNamed = @(Get-NamedInstalledProducts)
    $remainingExactProducts = @(
        Get-InstalledProduct ([string]$baseline.productCode)
        Get-InstalledProduct ([string]$candidate.productCode)
    ) | Where-Object { $null -ne $_ }
    if ($remainingRelated.Count -ne 0 -or $remainingNamed.Count -ne 0 -or $remainingExactProducts.Count -ne 0 -or
        (Test-Path -LiteralPath $installDirectory) -or
        (Test-Path -LiteralPath $registryPath) -or
        (Test-Path -LiteralPath $startMenuDirectory) -or
        (Test-Path -LiteralPath $defaultUserDataDirectory)) {
        throw 'Production uninstall left product registration or machine-level product resources behind.'
    }
    $checks.noProductsRemaining = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    foreach ($owned in @(
        [ordered]@{ attempted = $candidateAttempted; productCode = $candidate.productCode; label = 'candidate' },
        [ordered]@{ attempted = $baselineAttempted; productCode = $baseline.productCode; label = 'baseline' })) {
        if (!$owned.attempted -or [string]::IsNullOrWhiteSpace([string]$owned.productCode)) { continue }
        if ($null -eq (Get-InstalledProduct ([string]$owned.productCode))) { continue }
        try {
            $cleanupResult = Invoke-MsiExec `
                -Operation uninstall `
                -Target ([string]$owned.productCode) `
                -LogName "cleanup-$($owned.label).log"
            $cleanup.Add($cleanupResult)
        } catch {
            $cleanupError = "Cleanup failed for the exact $($owned.label) ProductCode $($owned.productCode): $($_.Exception.Message)"
            $cleanup.Add([ordered]@{ productCode = $owned.productCode; error = $cleanupError })
            if ($null -eq $failure) { $failure = $cleanupError }
        }
    }

    $completedAt = [DateTimeOffset]::UtcNow
    $report = [ordered]@{
        schemaVersion = 1
        evidenceId = [Guid]::NewGuid().ToString()
        status = $(if ($null -eq $failure -and !($checks.Values -contains $false) -and !$rebootRequired) { 'verified' } else { 'failed' })
        evidenceClass = 'production-clean-vm-stable-to-candidate'
        sourceCommit = $sourceCommit
        environment = $environment
        baseline = $baseline
        candidate = $candidate
        checks = $checks
        rebootRequired = $rebootRequired
        steps = $steps
        cleanup = $cleanup
        failure = $failure
        startedAt = $startedAt.ToString('O')
        completedAt = $completedAt.ToString('O')
        verifiedAt = $completedAt.ToString('O')
    }
    [System.IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 10),
        [System.Text.UTF8Encoding]::new($false))
    $report | ConvertTo-Json -Depth 10 | Out-Host
}

if ($null -ne $failure) {
    throw $failure
}

Write-Host "Production clean-VM lifecycle evidence: $reportPath"
