function Get-StableWixId {
    param(
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    return "$Prefix$($hash.Substring(0, 24))"
}

function ConvertTo-XmlAttribute {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Get-MsiProperty {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase',
            'InvokeMethod',
            $null,
            $installer,
            @([System.IO.Path]::GetFullPath($Path), 0))
        $view = $database.GetType().InvokeMember(
            'OpenView',
            'InvokeMethod',
            $null,
            $database,
            @("SELECT `Value` FROM `Property` WHERE `Property`='$Name'"))
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { throw "MSI property is missing: $Name" }
        return [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    } finally {
        if ($null -ne $view) {
            try { [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) } catch {}
        }
        foreach ($comObject in @($view, $database, $installer)) {
            if ($null -ne $comObject -and [System.Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
            }
        }
    }
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
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
    $directories = Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse | Sort-Object FullName
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
        if ($relative -ieq 'PiRoundtable.Windows.exe') {
            # An advertised shortcut belongs to the executable's machine-wide file component.
            # This keeps the File key path in Program Files and avoids mixing an HKCU key path
            # with a per-machine package merely to own a non-advertised Start Menu shortcut.
            [void]$builder.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="yes"{2}>' -f $fileId, $source, $defaultLanguage))
            [void]$builder.AppendLine('          <Shortcut Id="ApplicationStartMenuShortcut" Directory="ApplicationProgramsFolder" Name="$(var.ProductName)" Description="打开 $(var.ProductName)" Advertise="yes" WorkingDirectory="INSTALLFOLDER" />')
            [void]$builder.AppendLine('        </File>')
            [void]$builder.AppendLine('        <RemoveFolder Id="RemoveApplicationProgramsFolder" Directory="ApplicationProgramsFolder" On="uninstall" />')
        } else {
            [void]$builder.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="yes"{2} />' -f $fileId, $source, $defaultLanguage))
        }
        [void]$builder.AppendLine('      </Component>')
    }
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    [System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($true))
    Write-Host "Generated WiX manifest for $($files.Count) files and $($directories.Count) directories."
}
