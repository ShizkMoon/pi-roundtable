param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(3, 3)]
    [string[]]$ReportPath,

    [string]$OutputPath = 'artifacts\visual-qa\windows-visual-matrix.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$requiredDpis = @(96, 144, 192)
$requiredWidths = @(720, 900, 1280, 1520)
$reports = [System.Collections.Generic.List[object]]::new()

foreach ($path in $ReportPath) {
    $resolved = if ([System.IO.Path]::IsPathRooted($path)) {
        [System.IO.Path]::GetFullPath($path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot $path))
    }
    if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Visual matrix input does not exist: $resolved"
    }
    $report = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    if ($report.status -ne 'verified' -or !$report.systemStateRestored) {
        throw "Visual matrix input is not a completed, restored QA run: $resolved"
    }
    $reportDpi = [int]$report.dpi
    if ($reportDpi -notin $requiredDpis -or [int]$report.expectedDpi -ne $reportDpi) {
        throw "Visual matrix input does not bind its expected DPI to a required real DPI: $resolved"
    }
    $reports.Add([ordered]@{ path = $resolved; report = $report })
}

$seenDpis = @($reports | ForEach-Object { [int]$_.report.dpi } | Sort-Object -Unique)
if (($seenDpis -join ',') -ne ($requiredDpis -join ',')) {
    throw "The real Windows visual matrix must contain exactly one 96, 144, and 192 DPI report. Found: $($seenDpis -join ', ')."
}

foreach ($entry in $reports) {
    $themes = @($entry.report.themes)
    $themeKinds = @($themes | ForEach-Object {
        if ($_.theme -eq 'system' -and $_.highContrast -eq $true) {
            'high-contrast'
        } elseif ($_.theme -in @('light', 'dark') -and $_.highContrast -eq $false) {
            [string]$_.theme
        } else {
            "invalid:$($_.theme):$($_.highContrast)"
        }
    })
    $uniqueThemeKinds = @($themeKinds | Sort-Object -Unique)
    $requiredThemeKinds = @('dark', 'high-contrast', 'light')
    if ($themes.Count -ne 3 -or
        $uniqueThemeKinds.Count -ne 3 -or
        ($uniqueThemeKinds -join ',') -ne ($requiredThemeKinds -join ',')) {
        throw "Report $($entry.path) must contain exactly one light, dark, and real Windows high-contrast theme. Found: $($themeKinds -join ', ')."
    }
    foreach ($theme in $themes) {
        $expectedHighContrast = $theme.highContrast -eq $true
        $expectedHighContrastLabel = if ($expectedHighContrast) { 'on' } else { 'off' }
        $expectedRequestedTheme = if ($expectedHighContrast) { 'system' } else { [string]$theme.theme }
        if ($theme.report.visualStatus -ne 'verified' -or
            [int]$theme.report.dpi -ne [int]$entry.report.dpi -or
            [int]$theme.report.expectedDpi -ne [int]$entry.report.dpi -or
            $theme.report.highContrast -ne $expectedHighContrast -or
            $theme.report.expectedHighContrast -ne $expectedHighContrastLabel -or
            $theme.report.requestedTheme -ne $expectedRequestedTheme) {
            throw "Report $($entry.path) theme $($theme.theme) is not verified against the run DPI and requested Windows theme state."
        }
        $widths = @($theme.report.measurements | ForEach-Object { [int]$_.viewportWidthDip } | Sort-Object -Unique)
        foreach ($width in $requiredWidths) {
            if ($width -notin $widths) {
                throw "Report $($entry.path) theme $($theme.theme) is missing $width DIP."
            }
        }
    }
}

$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
$matrix = [ordered]@{
    status = 'verified'
    requiredDpis = $requiredDpis
    requiredThemes = @('light', 'dark', 'high-contrast')
    requiredViewportWidthsDip = $requiredWidths
    runs = $reports
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
[System.IO.File]::WriteAllText(
    $resolvedOutput,
    ($matrix | ConvertTo-Json -Depth 14),
    [System.Text.UTF8Encoding]::new($false))
$matrix | ConvertTo-Json -Depth 14
