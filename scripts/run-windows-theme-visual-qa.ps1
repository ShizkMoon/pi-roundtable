param(
    [string]$AppRoot = 'apps\windows\PiRoundtable.Windows\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64',

    [string]$OutputRoot = 'artifacts\visual-qa\theme-matrix',

    [ValidateSet(0, 96, 144, 192)]
    [int]$ExpectedDpi = 0,

    [ValidateSet(720, 900, 1280, 1520)]
    [int[]]$ViewportWidths = @(720, 900, 1280, 1520),

    [ValidateRange(560, 1000)]
    [int]$ViewportHeight = 800,

    [switch]$SkipHighContrast
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$visualQaScript = Join-Path $PSScriptRoot 'run-windows-visual-qa.ps1'
$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

Add-Type -AssemblyName System.Windows.Forms
if ($null -eq ('PiRoundtableHighContrast' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class PiRoundtableHighContrastSnapshot {
  public uint Flags { get; set; }
  public string Scheme { get; set; } = string.Empty;
  public bool Enabled => (Flags & 0x1) != 0;
}

public static class PiRoundtableHighContrast {
  private const uint SPI_GETHIGHCONTRAST = 0x0042;
  private const uint SPI_SETHIGHCONTRAST = 0x0043;
  private const uint HCF_HIGHCONTRASTON = 0x00000001;
  private const uint SPIF_SENDCHANGE = 0x0002;

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct HIGHCONTRAST {
    public uint cbSize;
    public uint dwFlags;
    public IntPtr lpszDefaultScheme;
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool SystemParametersInfoW(
    uint action,
    uint parameter,
    ref HIGHCONTRAST value,
    uint update);

  public static PiRoundtableHighContrastSnapshot Read() {
    var value = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
    if (!SystemParametersInfoW(SPI_GETHIGHCONTRAST, value.cbSize, ref value, 0)) {
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read Windows high-contrast state.");
    }
    return new PiRoundtableHighContrastSnapshot {
      Flags = value.dwFlags,
      Scheme = value.lpszDefaultScheme == IntPtr.Zero
        ? string.Empty
        : Marshal.PtrToStringUni(value.lpszDefaultScheme) ?? string.Empty,
    };
  }

  public static void Set(uint flags, string scheme) {
    IntPtr schemePointer = IntPtr.Zero;
    try {
      if (!string.IsNullOrWhiteSpace(scheme)) {
        schemePointer = Marshal.StringToHGlobalUni(scheme);
      }
      var value = new HIGHCONTRAST {
        cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>(),
        dwFlags = flags,
        lpszDefaultScheme = schemePointer,
      };
      if (!SystemParametersInfoW(SPI_SETHIGHCONTRAST, value.cbSize, ref value, SPIF_SENDCHANGE)) {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set Windows high-contrast state.");
      }
    } finally {
      if (schemePointer != IntPtr.Zero) {
        Marshal.FreeHGlobal(schemePointer);
      }
    }
  }

  public static uint WithEnabled(uint flags, bool enabled) => enabled
    ? flags | HCF_HIGHCONTRASTON
    : flags & ~HCF_HIGHCONTRASTON;
}
'@
}

function Wait-HighContrastState {
    param([Parameter(Mandatory = $true)][bool]$Enabled)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        if ([System.Windows.Forms.SystemInformation]::HighContrast -eq $Enabled) {
            return
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Windows did not reach highContrast=$Enabled within 15 seconds."
}

function Set-HighContrastState {
    param(
        [Parameter(Mandatory = $true)]$Snapshot,
        [Parameter(Mandatory = $true)][bool]$Enabled
    )

    $flags = [PiRoundtableHighContrast]::WithEnabled([uint32]$Snapshot.Flags, $Enabled)
    [PiRoundtableHighContrast]::Set($flags, [string]$Snapshot.Scheme)
    Wait-HighContrastState -Enabled $Enabled
}

function Invoke-ThemeQa {
    param(
        [Parameter(Mandatory = $true)][string]$Theme,
        [Parameter(Mandatory = $true)][string]$ContrastExpectation,
        [Parameter(Mandatory = $true)][string]$DirectoryName
    )

    $themeOutput = Join-Path $resolvedOutput $DirectoryName
    & $visualQaScript `
        -AppRoot $AppRoot `
        -OutputRoot $themeOutput `
        -ViewportWidths $ViewportWidths `
        -ViewportHeight $ViewportHeight `
        -ThemeMode $Theme `
        -ExpectedDpi $ExpectedDpi `
        -ExpectedHighContrast $ContrastExpectation | Out-Null
    $reportPath = Join-Path $themeOutput 'visual-qa-report.json'
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "Visual QA did not produce $reportPath."
    }
    return [ordered]@{
        theme = $Theme
        highContrast = $ContrastExpectation -eq 'on'
        reportPath = $reportPath
        report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    }
}

$original = [PiRoundtableHighContrast]::Read()
$results = [System.Collections.Generic.List[object]]::new()
$restored = $false
try {
    Set-HighContrastState -Snapshot $original -Enabled $false
    $results.Add((Invoke-ThemeQa -Theme 'light' -ContrastExpectation 'off' -DirectoryName 'light'))
    $results.Add((Invoke-ThemeQa -Theme 'dark' -ContrastExpectation 'off' -DirectoryName 'dark'))
    if (!$SkipHighContrast) {
        Set-HighContrastState -Snapshot $original -Enabled $true
        $results.Add((Invoke-ThemeQa -Theme 'system' -ContrastExpectation 'on' -DirectoryName 'high-contrast'))
    }
} finally {
    [PiRoundtableHighContrast]::Set([uint32]$original.Flags, [string]$original.Scheme)
    Wait-HighContrastState -Enabled ([bool]$original.Enabled)
    $restored = $true
}

$actualDpis = @($results | ForEach-Object { [int]$_.report.dpi } | Sort-Object -Unique)
if ($actualDpis.Count -ne 1) {
    throw "Theme runs did not share one real DPI context: $($actualDpis -join ', ')."
}
$report = [ordered]@{
    status = 'verified'
    dpi = $actualDpis[0]
    expectedDpi = $(if ($ExpectedDpi -eq 0) { $null } else { $ExpectedDpi })
    requiredViewportWidthsDip = $ViewportWidths
    originalHighContrast = [bool]$original.Enabled
    systemStateRestored = $restored
    themes = $results
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
$reportPath = Join-Path $resolvedOutput 'theme-visual-qa-report.json'
[System.IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 12),
    [System.Text.UTF8Encoding]::new($false))
$report | ConvertTo-Json -Depth 12
