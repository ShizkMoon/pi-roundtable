param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.0',

    [string]$OutputRoot,

    [switch]$SkipVerification,

    [switch]$SuppressMsiValidation
)

$ErrorActionPreference = 'Stop'
$dependencyStage = $null

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

function Get-StableWixId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    return "$Prefix$($hash.Substring(0, 24))"
}

function ConvertTo-XmlAttribute {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Test-PathIsStrictChild {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    return $resolvedPath.StartsWith(
        $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Write-WixFileManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
    $directories = Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse |
        Sort-Object FullName
    $directoryIds = @{ $resolvedRoot = 'INSTALLFOLDER' }
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$builder.AppendLine('  <Fragment>')
    foreach ($directory in $directories) {
        $relative = [System.IO.Path]::GetRelativePath($resolvedRoot, $directory.FullName)
        $directoryId = Get-StableWixId 'dir' $relative
        $directoryIds[$directory.FullName] = $directoryId
        $parentPath = [System.IO.Path]::GetFullPath($directory.Parent.FullName).TrimEnd('\')
        $parentId = $directoryIds[$parentPath]
        if ([string]::IsNullOrWhiteSpace($parentId)) {
            throw "Missing WiX parent directory for $relative."
        }
        [void]$builder.AppendLine(('    <DirectoryRef Id="{0}">' -f $parentId))
        [void]$builder.AppendLine(('      <Directory Id="{0}" Name="{1}" />' -f $directoryId, (ConvertTo-XmlAttribute $directory.Name)))
        [void]$builder.AppendLine('    </DirectoryRef>')
    }
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <ComponentGroup Id="PublishedFiles">')
    $msiUnsupportedMuiCultures = @('gd-gb', 'mi-NZ', 'ug-CN')
    $files = Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse |
        Where-Object {
            $_.Extension -ne '.pdb' -and
            !($_.Extension -ieq '.mui' -and $_.Directory.Name -cin $msiUnsupportedMuiCultures)
        } |
        Sort-Object FullName
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($resolvedRoot, $file.FullName)
        $directoryPath = [System.IO.Path]::GetFullPath($file.DirectoryName).TrimEnd('\')
        $directoryId = $directoryIds[$directoryPath]
        $componentId = Get-StableWixId 'cmp' $relative
        $fileId = Get-StableWixId 'fil' $relative
        $source = ConvertTo-XmlAttribute $file.FullName
        $ignoreInvalidEmbeddedLanguage = $file.Extension -ieq '.mui' -or
            $file.Name -iin @('Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Phone.dll')
        $defaultLanguage = if ($ignoreInvalidEmbeddedLanguage) { ' DefaultLanguage="0"' } else { '' }
        [void]$builder.AppendLine(('      <Component Id="{0}" Directory="{1}" Guid="*">' -f $componentId, $directoryId))
        [void]$builder.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="yes"{2} />' -f $fileId, $source, $defaultLanguage))
        [void]$builder.AppendLine('      </Component>')
    }
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    [System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($true))
    Write-Host "Generated WiX manifest for $($files.Count) files and $($directories.Count) directories."
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

    $appStage = Join-Path $packageRoot 'app'
    $updaterStage = Join-Path $packageRoot 'updater'
    $runtimeHostStage = Join-Path $appStage 'runtime-host'
    $runtimeStage = Join-Path $appStage 'runtime'
    $installerOutput = Join-Path $repoRoot 'out\installer'
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

    Invoke-Checked 'npm' @('run', 'build') $repoRoot
    Invoke-Checked 'cmake' @('--preset', 'release') $repoRoot
    Invoke-Checked 'cmake' @('--build', '--preset', 'release') $repoRoot
    if (!$SkipVerification) {
        Invoke-Checked 'cmake' @('--preset', 'dev') $repoRoot
        Invoke-Checked 'cmake' @('--build', '--preset', 'dev') $repoRoot
        Invoke-Checked 'ctest' @('--preset', 'dev') $repoRoot
        Invoke-Checked 'npm' @('test') $repoRoot
        Invoke-Checked 'dotnet' @(
            'test',
            'apps/windows/tests/PiRoundtable.Windows.Tests/PiRoundtable.Windows.Tests.csproj',
            '--configuration', 'Release'
        ) $repoRoot
    }

    New-Item -ItemType Directory -Force -Path $appStage | Out-Null
    Invoke-Checked 'dotnet' @(
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
    ) $repoRoot
    foreach ($resourceName in @('App.xbf', 'MainWindow.xbf', 'PiRoundtable.Windows.pri')) {
        if (!(Test-Path -LiteralPath (Join-Path $appStage $resourceName) -PathType Leaf)) {
            throw "Published WinUI resource is missing: $resourceName"
        }
    }

    Invoke-Checked 'dotnet' @(
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
    ) $repoRoot
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

    $runtimePackage = [ordered]@{
        name = 'pi-roundtable-packaged-runtime'
        private = $true
        type = 'module'
        dependencies = [ordered]@{
            '@earendil-works/pi-ai' = '0.83.0'
            '@earendil-works/pi-coding-agent' = '0.83.0'
            '@modelcontextprotocol/sdk' = '1.30.0'
            '@pi-roundtable/protocol' = 'file:./protocol'
        }
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText(
        (Join-Path $runtimeHostStage 'package.json'),
        $runtimePackage,
        [System.Text.UTF8Encoding]::new($false))
    $dependencyStage = Join-Path ([System.IO.Path]::GetTempPath()) "pi-roundtable-runtime-deps-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $dependencyStage | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $dependencyStage 'package.json'),
        $runtimePackage,
        [System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $protocolStage -Destination (Join-Path $dependencyStage 'protocol') -Recurse -Force
    Invoke-Checked 'npm' @('install', '--omit=dev', '--no-audit', '--no-fund') $dependencyStage
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

    $generatedWxs = Join-Path $packageRoot 'GeneratedFiles.wxs'
    Write-WixFileManifest -SourceRoot $appStage -OutputPath $generatedWxs

    $wixProject = Join-Path $repoRoot 'packaging\windows-x64\PiRoundtable.Installer.wixproj'
    $knownLanguageOverflowIds = @(
        (Get-StableWixId 'fil' 'Microsoft.ui.xaml.dll'),
        (Get-StableWixId 'fil' 'Microsoft.UI.Xaml.Phone.dll')
    )
    $wixBuildArguments = @(
        'build', $wixProject,
        '--configuration', 'Release',
        "-p:ProductVersion=$Version",
        "-p:PublishDir=$appStage",
        "-p:GeneratedWxs=$generatedWxs",
        "-p:OutputPath=$installerOutput"
    )
    $expectedIce03FileIds = $knownLanguageOverflowIds
    if ($SuppressMsiValidation) {
        Write-Host 'WiX MSI validation is suppressed explicitly; use only after an equivalent release package passes validation.'
        $wixBuildArguments += '-p:SuppressValidation=true'
        $expectedIce03FileIds = @()
    }
    Invoke-CheckedWixBuild $wixBuildArguments $repoRoot $expectedIce03FileIds

    $msi = Get-ChildItem -LiteralPath $installerOutput -Filter '*.msi' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $msi) {
        throw 'WiX build completed without producing an MSI.'
    }
    $hash = Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256
    Write-Host "MSI: $($msi.FullName)"
    Write-Host "SHA256: $($hash.Hash)"
} catch {
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
