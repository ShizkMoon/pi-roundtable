param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.2',

    [string]$OutputRoot,

    [string]$InstallerOutputRoot,

    [string]$NuGetConfigFile,

    [switch]$SkipVerification,

    [switch]$SuppressMsiValidation,

    [ValidatePattern('^[^<>:"/\\|?*;=]{1,60}$')]
    [string]$ProductName = 'Pi Roundtable',

    [ValidatePattern('^[^<>:"/\\|?*;=]{1,60}$')]
    [string]$Manufacturer = 'Pi Roundtable',

    [Guid]$UpgradeCode = [Guid]'8F84BF2C-3DBB-4F28-8B97-78D8B384365A',

    [ValidatePattern('^[^<>:"/\\|?*;=]{1,60}$')]
    [string]$InstallFolderName = 'Pi Roundtable',

    [ValidatePattern('^[A-Za-z0-9_.\\-]{1,120}$')]
    [string]$RegistryKeyPath = 'Software\PiRoundtable',

    [ValidatePattern('^[A-Za-z0-9._-]{1,60}$')]
    [string]$OutputNamePrefix = 'PiRoundtable',

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$SigningCertificateThumbprint,

    [ValidatePattern('^https?://')]
    [string]$TimestampUrl,

    [switch]$RequireTrustedSignature
)

$ErrorActionPreference = 'Stop'
$dependencyStage = $null
$temporaryWixNuGetConfig = $null
. (Join-Path $PSScriptRoot 'windows-packaging.ps1')
. (Join-Path $PSScriptRoot 'windows-signing.ps1')

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    Write-Host "[$FilePath] $($ArgumentList -join ' ')"
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
}

function Invoke-CheckedWixBuild {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedIce03FileIds
    )

    Write-Host "[dotnet] $($ArgumentList -join ' ')"
    $lines = [System.Collections.Generic.List[string]]::new()
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & dotnet @ArgumentList 2>&1 | ForEach-Object {
            $line = $_.ToString()
            $lines.Add($line)
            Write-Host $line
        }
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "dotnet WiX build failed with exit code $exitCode."
    }

    $seenFileIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unexpectedWarnings = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        if ($line -notmatch ':\s+warning\s+') {
            continue
        }
        if ($line -match 'warning WIX1076: ICE03: String overflow .*Table: File, Column: Language, Key\(s\): (?<fileId>fil[0-9A-F]+)') {
            [void]$seenFileIds.Add($Matches.fileId)
        } else {
            $unexpectedWarnings.Add($line)
        }
    }
    $missingFileIds = @($ExpectedIce03FileIds | Where-Object { !$seenFileIds.Contains($_) })
    $unknownFileIds = @($seenFileIds | Where-Object { $_ -notin $ExpectedIce03FileIds })
    if ($unexpectedWarnings.Count -gt 0 -or $missingFileIds.Count -gt 0 -or $unknownFileIds.Count -gt 0) {
        throw "WiX warning gate failed. Unexpected=$($unexpectedWarnings.Count), missing known ICE03=$($missingFileIds -join ','), unknown ICE03=$($unknownFileIds -join ',')."
    }
}

try {
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $packageRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        Join-Path $repoRoot 'out\package\windows-x64'
    } else {
        [System.IO.Path]::GetFullPath($OutputRoot)
    }
    $approvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
    if (!(Test-PathIsStrictChild -Path $packageRoot -Root $approvedOutputRoot)) {
        throw "OutputRoot must remain inside $approvedOutputRoot."
    }
    $restoreConfigArgument = @()
    if (![string]::IsNullOrWhiteSpace($NuGetConfigFile)) {
        $resolvedNuGetConfig = [System.IO.Path]::GetFullPath($NuGetConfigFile)
        if (!(Test-Path -LiteralPath $resolvedNuGetConfig -PathType Leaf)) {
            throw "NuGetConfigFile does not exist: $resolvedNuGetConfig"
        }
        $restoreConfigArgument = @("-p:RestoreConfigFile=$resolvedNuGetConfig")
    }

    $appStage = Join-Path $packageRoot 'app'
    $updaterStage = Join-Path $packageRoot 'updater'
    $runtimeHostStage = Join-Path $appStage 'runtime-host'
    $runtimeStage = Join-Path $appStage 'runtime'
    $installerOutput = if ([string]::IsNullOrWhiteSpace($InstallerOutputRoot)) {
        Join-Path $repoRoot 'out\installer'
    } else {
        [System.IO.Path]::GetFullPath($InstallerOutputRoot)
    }
    foreach ($path in @($packageRoot, $installerOutput)) {
        $resolved = [System.IO.Path]::GetFullPath($path)
        if (!(Test-PathIsStrictChild -Path $resolved -Root $approvedOutputRoot)) {
            throw "Refusing to clean a path outside $approvedOutputRoot."
        }
        if (Test-Path -LiteralPath $resolved) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $resolved | Out-Null
    }

    $nodeVersion = (& node -p 'process.versions.node').Trim()
    $nodeMajor = [int]($nodeVersion.Split('.')[0])
    if ($nodeMajor -lt 24) {
        throw "Windows packaging requires Node.js 24 or newer; found $nodeVersion."
    }
    $npmVersion = (& npm --version).Trim()
    $npmMajor = [int]($npmVersion.Split('.')[0])
    if ($npmMajor -lt 12) {
        throw "Windows packaging requires npm 12 or newer to produce the verified production dependency layout; found $npmVersion."
    }

    Invoke-Checked 'npm' @('run', 'build') $repoRoot
    Invoke-Checked 'cmake' @('--preset', 'release') $repoRoot
    Invoke-Checked 'cmake' @('--build', '--preset', 'release') $repoRoot
    if (!$SkipVerification) {
        Invoke-Checked 'cmake' @('--preset', 'dev') $repoRoot
        Invoke-Checked 'cmake' @('--build', '--preset', 'dev') $repoRoot
        Invoke-Checked 'ctest' @('--preset', 'dev') $repoRoot
        Invoke-Checked 'npm' @('test') $repoRoot
        $windowsTestArguments = @(
            'test',
            'apps/windows/tests/PiRoundtable.Windows.Tests/PiRoundtable.Windows.Tests.csproj',
            '--configuration', 'Release'
        ) + $restoreConfigArgument
        Invoke-Checked 'dotnet' $windowsTestArguments $repoRoot
    }

    New-Item -ItemType Directory -Force -Path $appStage | Out-Null
    $windowsPublishArguments = @(
        'publish',
        'apps/windows/PiRoundtable.Windows/PiRoundtable.Windows.csproj',
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $appStage,
        '-p:Platform=x64',
        "-p:Version=$Version",
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    ) + $restoreConfigArgument
    Invoke-Checked 'dotnet' $windowsPublishArguments $repoRoot
    foreach ($resourceName in @('App.xbf', 'MainWindow.xbf', 'PiRoundtable.Windows.pri')) {
        if (!(Test-Path -LiteralPath (Join-Path $appStage $resourceName) -PathType Leaf)) {
            throw "Published WinUI resource is missing: $resourceName"
        }
    }

    $updaterPublishArguments = @(
        'publish',
        'apps/windows/PiRoundtable.Updater/PiRoundtable.Updater.csproj',
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $updaterStage,
        "-p:Version=$Version",
        '-p:PublishSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    ) + $restoreConfigArgument
    Invoke-Checked 'dotnet' $updaterPublishArguments $repoRoot
    $updaterExecutable = Join-Path $updaterStage 'PiRoundtable.Updater.exe'
    if (!(Test-Path -LiteralPath $updaterExecutable -PathType Leaf)) {
        throw 'Single-file updater helper was not produced.'
    }
    Copy-Item -LiteralPath $updaterExecutable -Destination (Join-Path $appStage 'PiRoundtable.Updater.exe') -Force
    Remove-Item -LiteralPath $updaterStage -Recurse -Force

    $nativeCore = Join-Path $repoRoot 'out\build\release\core\pi_roundtable_core.dll'
    if (!(Test-Path -LiteralPath $nativeCore)) {
        throw 'Release native meeting core was not produced.'
    }
    Copy-Item -LiteralPath $nativeCore -Destination (Join-Path $appStage 'pi_roundtable_core.dll') -Force

    New-Item -ItemType Directory -Force -Path $runtimeHostStage | Out-Null
    Copy-Item -Path (Join-Path $repoRoot 'packages\runtime-host\dist\*') -Destination $runtimeHostStage -Recurse -Force
    $runtimeTests = Join-Path $runtimeHostStage 'test'
    if (Test-Path -LiteralPath $runtimeTests) {
        Remove-Item -LiteralPath $runtimeTests -Recurse -Force
    }
    $protocolStage = Join-Path $runtimeHostStage 'protocol'
    New-Item -ItemType Directory -Force -Path $protocolStage | Out-Null
    Copy-Item -Path (Join-Path $repoRoot 'packages\protocol-ts\dist') -Destination $protocolStage -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packages\protocol-ts\package.json') -Destination $protocolStage -Force

    $runtimeDependencyRoot = Join-Path $repoRoot 'packaging\windows-runtime'
    $runtimeDependencyManifest = Join-Path $runtimeDependencyRoot 'package.json'
    $runtimeDependencyLock = Join-Path $runtimeDependencyRoot 'package-lock.json'
    foreach ($requiredDependencyFile in @($runtimeDependencyManifest, $runtimeDependencyLock)) {
        if (!(Test-Path -LiteralPath $requiredDependencyFile -PathType Leaf)) {
            throw "The committed Windows Runtime dependency lock is incomplete: $requiredDependencyFile"
        }
    }
    $protocolVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'packages\protocol-ts\package.json') -Raw | ConvertFrom-Json).version
    $lockedProtocolVersion = (Get-Content -LiteralPath (Join-Path $runtimeDependencyRoot 'protocol\package.json') -Raw | ConvertFrom-Json).version
    if ($protocolVersion -ne $lockedProtocolVersion) {
        throw "The Windows Runtime dependency lock targets protocol $lockedProtocolVersion, but the workspace builds $protocolVersion."
    }
    Copy-Item -LiteralPath $runtimeDependencyManifest -Destination (Join-Path $runtimeHostStage 'package.json') -Force
    $dependencyStage = Join-Path ([System.IO.Path]::GetTempPath()) "pi-roundtable-runtime-deps-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $dependencyStage | Out-Null
    Copy-Item -LiteralPath $runtimeDependencyManifest -Destination (Join-Path $dependencyStage 'package.json') -Force
    Copy-Item -LiteralPath $runtimeDependencyLock -Destination (Join-Path $dependencyStage 'package-lock.json') -Force
    Copy-Item -LiteralPath $protocolStage -Destination (Join-Path $dependencyStage 'protocol') -Recurse -Force
    $npmCache = Join-Path $packageRoot '.npm-cache'
    New-Item -ItemType Directory -Force -Path $npmCache | Out-Null
    Invoke-Checked 'npm' @(
        'ci',
        '--omit=dev',
        '--ignore-scripts',
        '--no-audit',
        '--no-fund',
        '--prefer-offline',
        '--cache', $npmCache
    ) $dependencyStage
    Copy-Item -LiteralPath (Join-Path $dependencyStage 'node_modules') -Destination $runtimeHostStage -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $dependencyStage 'package-lock.json') -Destination $runtimeHostStage -Force
    Remove-Item -LiteralPath $dependencyStage -Recurse -Force
    $dependencyStage = $null

    $nodeExecutable = (& node -p 'process.execPath').Trim()
    if (!(Test-Path -LiteralPath $nodeExecutable)) {
        throw 'Unable to resolve the current Node.js executable.'
    }
    $nodeHome = Split-Path -Parent $nodeExecutable
    $nodeLicense = Join-Path $nodeHome 'LICENSE'
    if (!(Test-Path -LiteralPath $nodeLicense)) {
        throw 'The local Node.js distribution does not include its LICENSE file.'
    }
    New-Item -ItemType Directory -Force -Path $runtimeStage | Out-Null
    Copy-Item -LiteralPath $nodeExecutable -Destination (Join-Path $runtimeStage 'node.exe') -Force
    Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $runtimeStage 'LICENSE.node.txt') -Force

    $packagedNode = Join-Path $runtimeStage 'node.exe'
    $packagedHost = Join-Path $runtimeHostStage 'host-main.js'
    Invoke-Checked $packagedNode @('--check', $packagedHost) $appStage
    # Syntax checks do not resolve imports. Exercise the staged file dependency
    # so a missing protocol dist cannot survive until first application run.
    Invoke-Checked $packagedNode @(
        '--input-type=module',
        '--eval',
        "await import('@pi-roundtable/protocol')"
    ) $runtimeHostStage

    if (![string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        $firstPartyArtifacts = @(
            (Join-Path $appStage 'PiRoundtable.Windows.exe'),
            (Join-Path $appStage 'PiRoundtable.Windows.dll'),
            (Join-Path $appStage 'PiRoundtable.Updater.exe'),
            (Join-Path $appStage 'pi_roundtable_core.dll')
        )
        Invoke-WindowsArtifactSigning `
            -Path $firstPartyArtifacts `
            -CertificateThumbprint $SigningCertificateThumbprint `
            -TimestampUrl $TimestampUrl `
            -RequireTrustedSignature:$RequireTrustedSignature | Out-Host
    }

    $generatedWxs = Join-Path $packageRoot 'GeneratedFiles.wxs'
    Write-WixFileManifest -SourceRoot $appStage -OutputPath $generatedWxs

    $wixProject = Join-Path $repoRoot 'packaging\windows-x64\PiRoundtable.Installer.wixproj'
    if (![string]::IsNullOrWhiteSpace($NuGetConfigFile)) {
        # MSBuild resolves a project SDK before evaluating RestoreConfigFile. Place the
        # requested config beside the WiX project for that early resolver phase, then
        # remove it after the build so local restore bridges never become repository state.
        $wixProjectDirectory = Split-Path -Parent $wixProject
        $wixNuGetConfig = Join-Path $wixProjectDirectory 'NuGet.Config'
        if (![System.IO.Path]::GetFullPath($wixNuGetConfig).Equals(
                $resolvedNuGetConfig,
                [StringComparison]::OrdinalIgnoreCase)) {
            if (Test-Path -LiteralPath $wixNuGetConfig) {
                throw "Refusing to replace the existing WiX NuGet.Config: $wixNuGetConfig"
            }
            Copy-Item -LiteralPath $resolvedNuGetConfig -Destination $wixNuGetConfig
            $temporaryWixNuGetConfig = $wixNuGetConfig
        }
    }
    $knownLanguageOverflowIds = @(
        (Get-StableWixId 'fil' 'Microsoft.ui.xaml.dll'),
        (Get-StableWixId 'fil' 'Microsoft.UI.Xaml.Phone.dll')
    )
    $wixBuildArguments = @(
        'build', $wixProject,
        '--configuration', 'Release',
        "-p:ProductVersion=$Version",
        "-p:ProductName=$ProductName",
        "-p:Manufacturer=$Manufacturer",
        "-p:UpgradeCode=$($UpgradeCode.ToString().ToUpperInvariant())",
        "-p:InstallFolderName=$InstallFolderName",
        "-p:RegistryKeyPath=$RegistryKeyPath",
        "-p:OutputNamePrefix=$OutputNamePrefix",
        "-p:PublishDir=$appStage",
        "-p:GeneratedWxs=$generatedWxs",
        "-p:OutputPath=$installerOutput"
    ) + $restoreConfigArgument
    $expectedIce03FileIds = $knownLanguageOverflowIds
    if ($SuppressMsiValidation) {
        Write-Host 'WiX MSI validation is suppressed explicitly; use only after an equivalent release package passes validation.'
        $wixBuildArguments += '-p:SuppressValidation=true'
        $expectedIce03FileIds = @()
    }
    Invoke-CheckedWixBuild $wixBuildArguments $repoRoot $expectedIce03FileIds
    if ($null -ne $temporaryWixNuGetConfig) {
        Remove-Item -LiteralPath $temporaryWixNuGetConfig -Force
        $temporaryWixNuGetConfig = $null
    }

    $msi = Get-ChildItem -LiteralPath $installerOutput -Filter '*.msi' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $msi) {
        throw 'WiX build completed without producing an MSI.'
    }
    if (![string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        Invoke-WindowsArtifactSigning `
            -Path $msi.FullName `
            -CertificateThumbprint $SigningCertificateThumbprint `
            -TimestampUrl $TimestampUrl `
            -RequireTrustedSignature:$RequireTrustedSignature | Out-Host
    }
    $hash = Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256
    Write-Host "MSI: $($msi.FullName)"
    Write-Host "SHA256: $($hash.Hash)"
} catch {
    if ($null -ne $temporaryWixNuGetConfig) {
        Remove-Item -LiteralPath $temporaryWixNuGetConfig -Force -ErrorAction SilentlyContinue
        $temporaryWixNuGetConfig = $null
    }
    if ($null -ne $dependencyStage) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $resolvedDependencyStage = [System.IO.Path]::GetFullPath($dependencyStage)
        if ($resolvedDependencyStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedDependencyStage).StartsWith('pi-roundtable-runtime-deps-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedDependencyStage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Error "Windows x64 package build failed: $($_.Exception.Message)"
    exit 1
}
