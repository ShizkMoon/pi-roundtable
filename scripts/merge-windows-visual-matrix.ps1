param(
    [string[]]$ReportPath,

    [string]$ReportPath96,

    [string]$ReportPath144,

    [string]$ReportPath192,

    [string]$OutputPath = 'artifacts\visual-qa\windows-visual-matrix.json',

    [ValidateRange(1, 168)]
    [int]$MaximumAgeHours = 24
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$requiredDpis = @(96, 144, 192)
$requiredWidths = @(720, 900, 1280, 1520)
$reports = [System.Collections.Generic.List[object]]::new()
$now = [DateTimeOffset]::UtcNow
$oldestSourceVerifiedAt = $now
$explicitReportPaths = @($ReportPath96, $ReportPath144, $ReportPath192)
$hasArrayPaths = $null -ne $ReportPath -and $ReportPath.Count -ne 0
$hasExplicitPaths = @($explicitReportPaths | Where-Object { ![string]::IsNullOrWhiteSpace($_) }).Count -ne 0
if ($hasArrayPaths -eq $hasExplicitPaths) {
    throw 'Specify either ReportPath with three values or all three ReportPath96/144/192 values.'
}
$inputReportPaths = if ($hasArrayPaths) { @($ReportPath) } else { $explicitReportPaths }
if ($inputReportPaths.Count -ne 3 -or
    @($inputReportPaths | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
    throw 'The visual matrix requires exactly three non-empty report paths.'
}

function Assert-FreshTimestamp {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $timestamp = [DateTimeOffset]::MinValue
    if (![DateTimeOffset]::TryParse($Value, [ref]$timestamp) -or
        $timestamp -gt $now.AddMinutes(5) -or
        $now - $timestamp -gt [TimeSpan]::FromHours($MaximumAgeHours)) {
        throw "$Label is missing, stale, or comes from the future."
    }
    if ($timestamp -lt $script:oldestSourceVerifiedAt) {
        $script:oldestSourceVerifiedAt = $timestamp
    }
    return $timestamp
}

foreach ($path in $inputReportPaths) {
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
    if ($report.schemaVersion -ne 2 -or
        $report.evidenceClass -ne 'real-windows-theme-dpi-visual-qa' -or
        [string]$report.productVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        [string]$report.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]$report.appExecutableSha256 -notmatch '^[0-9A-F]{64}$') {
        throw "Visual matrix input is missing its build identity: $resolved"
    }
    $reportEvidenceId = [Guid]::Empty
    if (![Guid]::TryParse([string]$report.evidenceId, [ref]$reportEvidenceId) -or
        $reportEvidenceId -eq [Guid]::Empty) {
        throw "Visual matrix input has no valid non-empty evidenceId: $resolved"
    }
    $startedAt = Assert-FreshTimestamp -Value ([string]$report.startedAt) -Label "Visual matrix input startedAt in $resolved"
    $verifiedAt = Assert-FreshTimestamp -Value ([string]$report.verifiedAt) -Label "Visual matrix input verifiedAt in $resolved"
    if ($verifiedAt -lt $startedAt) {
        throw "Visual matrix input completed before it started: $resolved"
    }
    $reportDpi = [int]$report.dpi
    if ($reportDpi -notin $requiredDpis -or [int]$report.expectedDpi -ne $reportDpi) {
        throw "Visual matrix input does not bind its expected DPI to a required real DPI: $resolved"
    }
    $reports.Add([ordered]@{
        path = $resolved
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToUpperInvariant()
        report = $report
    })
}

$productVersions = @($reports | ForEach-Object { [string]$_.report.productVersion } | Sort-Object -Unique)
$sourceCommits = @($reports | ForEach-Object { [string]$_.report.sourceCommit } | Sort-Object -Unique)
$appHashes = @($reports | ForEach-Object { [string]$_.report.appExecutableSha256 } | Sort-Object -Unique)
if ($productVersions.Count -ne 1 -or $sourceCommits.Count -ne 1 -or $appHashes.Count -ne 1) {
    throw 'Visual matrix inputs do not describe the same product version, Git commit, and application executable.'
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
        [void](Assert-FreshTimestamp `
            -Value ([string]$theme.report.verifiedAt) `
            -Label "Report $($entry.path) theme $($theme.theme) verifiedAt")
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
    schemaVersion = 2
    evidenceId = [Guid]::NewGuid().ToString()
    status = 'verified'
    evidenceClass = 'real-windows-dpi-visual-matrix'
    productVersion = $productVersions[0]
    sourceCommit = $sourceCommits[0]
    appExecutableSha256 = $appHashes[0]
    requiredDpis = $requiredDpis
    requiredThemes = @('light', 'dark', 'high-contrast')
    requiredViewportWidthsDip = $requiredWidths
    runs = $reports
    oldestSourceVerifiedAt = $oldestSourceVerifiedAt.ToString('O')
    maximumSourceAgeHours = $MaximumAgeHours
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
[System.IO.File]::WriteAllText(
    $resolvedOutput,
    ($matrix | ConvertTo-Json -Depth 14),
    [System.Text.UTF8Encoding]::new($false))
$matrix | ConvertTo-Json -Depth 14
