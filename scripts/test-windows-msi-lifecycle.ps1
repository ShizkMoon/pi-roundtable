param(
    [string]$PackageRoot = 'out\package\windows-x64',

    [string]$OutputRoot,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$BaselineVersion = '0.2.1',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$CandidateVersion = '0.2.2',

    [string]$BaselineMsiPath,

    [string]$CandidateMsiPath,

    [switch]$UseFullPayload,

    [switch]$SkipLaunch,

    [ValidateRange(0, 120)]
    [int]$MsiTimeoutMinutes = 0
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$approvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
. (Join-Path $PSScriptRoot 'windows-packaging.ps1')
$effectiveMsiTimeoutMinutes = if ($MsiTimeoutMinutes -gt 0) {
    $MsiTimeoutMinutes
} elseif ($UseFullPayload) {
    # A major upgrade of 22k+ one-file components can legitimately spend more
    # than twelve minutes removing and reinstalling the complete Node payload.
    45
} else {
    12
}
$resolvedPackageRoot = if ([System.IO.Path]::IsPathRooted($PackageRoot)) {
    [System.IO.Path]::GetFullPath($PackageRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PackageRoot))
}
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$resolvedOutput = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $approvedOutputRoot "e2e\msi-lifecycle-$runId"
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

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The real per-machine MSI lifecycle test requires an elevated administrator PowerShell session.'
}

$productionAppStage = Join-Path $resolvedPackageRoot 'app'
$productionGeneratedWxs = Join-Path $resolvedPackageRoot 'GeneratedFiles.wxs'
$wixProject = Join-Path $repoRoot 'packaging\windows-x64\PiRoundtable.Installer.wixproj'
foreach ($required in @(
    (Join-Path $productionAppStage 'PiRoundtable.Windows.exe'),
    $productionGeneratedWxs,
    $wixProject)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Build the staged Windows package before lifecycle testing: $required"
    }
}

$qaProductName = 'Pi Roundtable Installer QA'
$qaInstallFolderName = 'Pi Roundtable Installer QA'
$qaRegistryKeyPath = 'Software\PiRoundtable\InstallerQa'
$qaUpgradeCode = [Guid]'7A12A70C-3668-4A3F-A5C0-E8C22F7B6D21'
$productionUpgradeCode = [Guid]'8F84BF2C-3DBB-4F28-8B97-78D8B384365A'
$qaInstallDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) $qaInstallFolderName
$qaRegistryPath = 'HKLM:\Software\PiRoundtable\InstallerQa'
$qaStartMenuDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)) $qaProductName
$packageOutput = Join-Path $resolvedOutput 'packages'
$logOutput = Join-Path $resolvedOutput 'logs'
$dataRoot = Join-Path $resolvedOutput 'app-data'
New-Item -ItemType Directory -Force -Path $packageOutput, $logOutput, $dataRoot | Out-Null

$appStage = $productionAppStage
$generatedWxs = $productionGeneratedWxs
$payloadScope = 'full-production-payload'
if (!$UseFullPayload -and [string]::IsNullOrWhiteSpace($BaselineMsiPath)) {
    $fixtureRoot = Join-Path $resolvedOutput 'application-shell-fixture'
    $appStage = Join-Path $fixtureRoot 'app'
    $generatedWxs = Join-Path $fixtureRoot 'GeneratedFiles.wxs'
    New-Item -ItemType Directory -Force -Path $appStage | Out-Null
    Get-ChildItem -LiteralPath $productionAppStage -Force |
        Where-Object Name -notin @('runtime', 'runtime-host') |
        Copy-Item -Destination $appStage -Recurse -Force
    Write-WixFileManifest -SourceRoot $appStage -OutputPath $generatedWxs
    $payloadScope = 'real self-contained WinUI application shell; runtime-host and Node excluded'
}

if ($null -eq ('PiRoundtableMsiNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PiRoundtableMsiNative {
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
    param([Parameter(Mandatory = $true)][Guid]$UpgradeCode)
    return @([PiRoundtableMsiNative]::RelatedProducts("{$($UpgradeCode.ToString().ToUpperInvariant())}"))
}

function Get-MsiProperty {
    param(
        [Parameter(Mandatory = $true)][string]$MsiPath,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.OpenDatabase($MsiPath, 0)
        $escapedName = $Name.Replace("'", "''")
        $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$escapedName'")
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "MSI property is missing: $Name"
        }
        $value = [string]($record.StringData(1))
        return $value
    } finally {
        if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($null -ne $installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
}

function Build-QaMsi {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $destination = Join-Path $packageOutput $Label
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $arguments = @(
        'build', $wixProject,
        '--configuration', 'Release',
        '--no-incremental',
        "-p:ProductVersion=$Version",
        "-p:ProductName=$qaProductName",
        '-p:Manufacturer=Pi Roundtable',
        "-p:UpgradeCode=$($qaUpgradeCode.ToString().ToUpperInvariant())",
        "-p:InstallFolderName=$qaInstallFolderName",
        "-p:RegistryKeyPath=$qaRegistryKeyPath",
        '-p:OutputNamePrefix=PiRoundtable-Installer-QA',
        "-p:PublishDir=$appStage",
        "-p:GeneratedWxs=$generatedWxs",
        "-p:OutputPath=$destination",
        '-p:SuppressValidation=true'
    )
    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to build the $Label lifecycle MSI."
    }
    $msi = Get-ChildItem -LiteralPath $destination -Filter '*.msi' -File | Select-Object -First 1
    if ($null -eq $msi) {
        throw "The $Label lifecycle MSI was not produced."
    }
    return $msi.FullName
}

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('install', 'repair', 'uninstall')][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$LogName,
        [int[]]$AcceptedExitCodes = @(0, 3010)
    )

    $logPath = Join-Path $logOutput $LogName
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\msiexec.exe'))
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add($(switch ($Operation) {
        'install' { '/i' }
        # Repair only missing/older payload plus machine/user registry and shortcuts.
        # `/fa` rewrites all 22k+ packaged files and turns a focused repair gate into
        # a long full reinstall, obscuring whether the deliberately removed file heals.
        'repair' { '/fomus' }
        'uninstall' { '/x' }
    }))
    $startInfo.ArgumentList.Add($Target)
    $startInfo.ArgumentList.Add('/qn')
    $startInfo.ArgumentList.Add('/norestart')
    $startInfo.ArgumentList.Add('/l*v')
    $startInfo.ArgumentList.Add($logPath)
    $process = [Diagnostics.Process]::Start($startInfo)
    $startedAt = [DateTimeOffset]::UtcNow
    try {
        if (!$process.WaitForExit([int][TimeSpan]::FromMinutes($effectiveMsiTimeoutMinutes).TotalMilliseconds)) {
            $timeoutMessage = "msiexec exceeded the $effectiveMsiTimeoutMinutes minute limit for $Operation $Target."
            Write-Warning "$timeoutMessage Waiting up to 15 additional minutes for transaction consistency before cleanup."
            if (!$process.WaitForExit([int][TimeSpan]::FromMinutes(15).TotalMilliseconds)) {
                # Never dispose a live process and immediately pretend cleanup is safe. If both
                # bounded waits expire, cancel the exact client and wait for it to exit first.
                if (!$process.HasExited) {
                    try {
                        $process.Kill($true)
                    } catch [InvalidOperationException] {
                        # The process exited between HasExited and Kill; waiting below is still safe.
                    }
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
        logPath = $logPath
        durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds, 1)
        accepted = $exitCode -in $AcceptedExitCodes
    }
    $script:steps.Add($result)
    if (!$result.accepted) {
        throw "msiexec $Operation failed with exit code $exitCode. Log: $logPath"
    }
    return $result
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

function Assert-ProductInstalled {
    param(
        [Parameter(Mandatory = $true)][string]$ProductCode,
        [Parameter(Mandatory = $true)][string]$Version
    )
    $record = Get-InstalledProduct $ProductCode
    if ($null -eq $record -or $record.DisplayName -ne $qaProductName -or $record.DisplayVersion -ne $Version) {
        throw "Expected $qaProductName $Version to be registered as $ProductCode."
    }
    if (!(Test-Path -LiteralPath (Join-Path $qaInstallDirectory 'PiRoundtable.Windows.exe') -PathType Leaf)) {
        throw "Installed application executable is missing from $qaInstallDirectory."
    }
}

function Invoke-InstalledAppSmoke {
    param([Parameter(Mandatory = $true)][string]$Label)
    if ($SkipLaunch) {
        $script:steps.Add([ordered]@{ operation = 'launch'; label = $Label; skipped = $true })
        return
    }
    $executable = Join-Path $qaInstallDirectory 'PiRoundtable.Windows.exe'
    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.WorkingDirectory = $qaInstallDirectory
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
        $script:steps.Add([ordered]@{ operation = 'launch'; label = $Label; responsiveWindow = $true })
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

$steps = [System.Collections.Generic.List[object]]::new()
$cleanupSteps = [System.Collections.Generic.List[object]]::new()
$productionBefore = @(Get-RelatedProducts $productionUpgradeCode | Sort-Object)
$baselineMsi = $null
$candidateMsi = $null
$baselineProperties = $null
$candidateProperties = $null
$failure = $null

try {
    foreach ($leftover in @(Get-RelatedProducts $qaUpgradeCode)) {
        Invoke-MsiExec -Operation uninstall -Target $leftover -LogName "cleanup-before-$($leftover.Trim('{}')).log" | Out-Null
    }

    if ([string]::IsNullOrWhiteSpace($BaselineMsiPath) -ne [string]::IsNullOrWhiteSpace($CandidateMsiPath)) {
        throw 'BaselineMsiPath and CandidateMsiPath must be supplied together.'
    }
    if (![string]::IsNullOrWhiteSpace($BaselineMsiPath)) {
        $baselineMsi = [System.IO.Path]::GetFullPath($BaselineMsiPath)
        $candidateMsi = [System.IO.Path]::GetFullPath($CandidateMsiPath)
        foreach ($providedMsi in @($baselineMsi, $candidateMsi)) {
            if (!(Test-Path -LiteralPath $providedMsi -PathType Leaf)) {
                throw "Provided lifecycle MSI does not exist: $providedMsi"
            }
        }
    } else {
        $baselineMsi = Build-QaMsi -Version $BaselineVersion -Label 'baseline'
        $candidateMsi = Build-QaMsi -Version $CandidateVersion -Label 'candidate'
    }
    $baselineProperties = [ordered]@{
        path = $baselineMsi
        productCode = Get-MsiProperty $baselineMsi 'ProductCode'
        productVersion = Get-MsiProperty $baselineMsi 'ProductVersion'
        productName = Get-MsiProperty $baselineMsi 'ProductName'
        upgradeCode = Get-MsiProperty $baselineMsi 'UpgradeCode'
    }
    $candidateProperties = [ordered]@{
        path = $candidateMsi
        productCode = Get-MsiProperty $candidateMsi 'ProductCode'
        productVersion = Get-MsiProperty $candidateMsi 'ProductVersion'
        productName = Get-MsiProperty $candidateMsi 'ProductName'
        upgradeCode = Get-MsiProperty $candidateMsi 'UpgradeCode'
    }
    if ($baselineProperties.productCode -eq $candidateProperties.productCode -or
        $baselineProperties.upgradeCode -ne $candidateProperties.upgradeCode -or
        $baselineProperties.upgradeCode.Trim('{}') -ne $qaUpgradeCode.ToString()) {
        throw 'Lifecycle packages do not have the required distinct ProductCodes and shared isolated UpgradeCode.'
    }

    Invoke-MsiExec -Operation install -Target $baselineMsi -LogName '01-install-baseline.log' | Out-Null
    Assert-ProductInstalled $baselineProperties.productCode $BaselineVersion
    Invoke-InstalledAppSmoke -Label 'baseline'

    $baselineExecutable = Join-Path $qaInstallDirectory 'PiRoundtable.Windows.exe'
    $baselineHash = (Get-FileHash -LiteralPath $baselineExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    Remove-Item -LiteralPath $baselineExecutable -Force
    Invoke-MsiExec -Operation repair -Target $baselineProperties.productCode -LogName '02-repair-baseline.log' | Out-Null
    $repairedBaselineHash = (Get-FileHash -LiteralPath $baselineExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($repairedBaselineHash -ne $baselineHash) {
        throw 'Baseline repair did not restore the original application executable.'
    }
    $steps.Add([ordered]@{ operation = 'repair-verification'; version = $BaselineVersion; restoredSha256 = $repairedBaselineHash })

    Invoke-MsiExec -Operation install -Target $candidateMsi -LogName '03-upgrade-candidate.log' | Out-Null
    if ($null -ne (Get-InstalledProduct $baselineProperties.productCode)) {
        throw 'Major upgrade left the baseline ProductCode registered.'
    }
    Assert-ProductInstalled $candidateProperties.productCode $CandidateVersion
    Invoke-InstalledAppSmoke -Label 'candidate-after-upgrade'

    $candidateExecutable = Join-Path $qaInstallDirectory 'PiRoundtable.Windows.exe'
    $candidateHashBeforeDowngrade = (Get-FileHash -LiteralPath $candidateExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    $downgrade = Invoke-MsiExec `
        -Operation install `
        -Target $baselineMsi `
        -LogName '04-downgrade-block.log' `
        -AcceptedExitCodes @(1603, 1638)
    if ($downgrade.exitCode -eq 0 -or $downgrade.exitCode -eq 3010) {
        throw 'Downgrade unexpectedly succeeded.'
    }
    Assert-ProductInstalled $candidateProperties.productCode $CandidateVersion
    $candidateHashAfterDowngrade = (Get-FileHash -LiteralPath $candidateExecutable -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($candidateHashAfterDowngrade -ne $candidateHashBeforeDowngrade) {
        throw 'Blocked downgrade changed the installed candidate payload.'
    }
    $steps.Add([ordered]@{ operation = 'downgrade-verification'; blocked = $true; installedSha256 = $candidateHashAfterDowngrade })

    $candidateCore = Join-Path $qaInstallDirectory 'pi_roundtable_core.dll'
    $candidateCoreHash = (Get-FileHash -LiteralPath $candidateCore -Algorithm SHA256).Hash.ToUpperInvariant()
    Remove-Item -LiteralPath $candidateCore -Force
    Invoke-MsiExec -Operation repair -Target $candidateProperties.productCode -LogName '05-repair-candidate.log' | Out-Null
    $repairedCandidateCoreHash = (Get-FileHash -LiteralPath $candidateCore -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($repairedCandidateCoreHash -ne $candidateCoreHash) {
        throw 'Candidate repair did not restore the original native core.'
    }
    $steps.Add([ordered]@{ operation = 'repair-verification'; version = $CandidateVersion; restoredSha256 = $repairedCandidateCoreHash })

    Invoke-MsiExec -Operation uninstall -Target $candidateProperties.productCode -LogName '06-uninstall-candidate.log' | Out-Null
    if ((Get-RelatedProducts $qaUpgradeCode).Count -ne 0 -or
        (Test-Path -LiteralPath $qaInstallDirectory) -or
        (Test-Path -LiteralPath $qaRegistryPath) -or
        (Test-Path -LiteralPath $qaStartMenuDirectory)) {
        throw 'Uninstall left the isolated QA product registered or left machine-level product resources behind.'
    }
} catch {
    $failure = $_.Exception.Message
    throw
} finally {
    foreach ($related in @(Get-RelatedProducts $qaUpgradeCode)) {
        try {
            $cleanupResult = Invoke-MsiExec `
                -Operation uninstall `
                -Target $related `
                -LogName "cleanup-after-$($related.Trim('{}')).log"
            $cleanupSteps.Add($cleanupResult)
        } catch {
            $cleanupSteps.Add([ordered]@{ operation = 'cleanup'; productCode = $related; error = $_.Exception.Message })
        }
    }
    $productionAfter = @(Get-RelatedProducts $productionUpgradeCode | Sort-Object)
    $productionUnchanged = ($productionBefore -join '|') -eq ($productionAfter -join '|')
    $qaProductsRemaining = @(Get-RelatedProducts $qaUpgradeCode)
    $report = [ordered]@{
        status = $(if ($null -eq $failure -and $qaProductsRemaining.Count -eq 0 -and $productionUnchanged) { 'verified' } else { 'failed' })
        failure = $failure
        isolation = [ordered]@{
            productName = $qaProductName
            upgradeCode = $qaUpgradeCode.ToString().ToUpperInvariant()
            productionUpgradeCode = $productionUpgradeCode.ToString().ToUpperInvariant()
            productionProductsBefore = $productionBefore
            productionProductsAfter = $productionAfter
            productionRegistrationUnchanged = $productionUnchanged
            qaProductsRemaining = $qaProductsRemaining
        }
        payloadScope = $payloadScope
        msiTimeoutMinutes = $effectiveMsiTimeoutMinutes
        baseline = $baselineProperties
        candidate = $candidateProperties
        installDirectory = $qaInstallDirectory
        steps = $steps
        cleanup = $cleanupSteps
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $reportPath = Join-Path $resolvedOutput 'msi-lifecycle-report.json'
    [System.IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 10),
        [System.Text.UTF8Encoding]::new($false))
    $report | ConvertTo-Json -Depth 10 | Out-Host
}
