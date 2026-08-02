param(
    [int]$MessageCount = 1000,

    [string]$AppRoot = 'apps\windows\PiRoundtable.Windows\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64',

    [string]$OutputRoot = 'artifacts\performance'
)

$ErrorActionPreference = 'Stop'
if ($MessageCount -lt 1000 -or $MessageCount -gt 10000) {
    throw 'MessageCount must be between 1000 and 10000.'
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedAppRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $AppRoot))
$executable = Join-Path $resolvedAppRoot 'PiRoundtable.Windows.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the Windows application first: $executable"
}
$resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$dataRoot = Join-Path $resolvedOutput 'data-root'
$sessionDirectory = Join-Path $dataRoot 'sessions'
New-Item -ItemType Directory -Force -Path $sessionDirectory | Out-Null

$messages = [System.Collections.Generic.List[object]]::new($MessageCount)
$startAt = [DateTimeOffset]::Parse('2026-08-02T00:00:00Z')
for ($index = 0; $index -lt $MessageCount; $index++) {
    $text = if ($index % 50 -eq 0) {
        "## 性能样本 $index`n`n| 项目 | 值 |`n|---|---:|`n| sequence | $index |`n| formula | ```` `$x_$index = \\sum_{i=1}^{n} i`$ ```` |`n`n``````cpp`nint sequence = $index;`n``````"
    } else {
        "第 $index 条规范化消息；用于验证 1000+ 长会话虚拟化、Markdown 延迟渲染与滚动跟随。"
    }
    $messages.Add([ordered]@{
        messageId = "message.performance-$index"
        kind = if ($index % 3 -eq 0) { 'host' } else { 'role' }
        speakerId = if ($index % 3 -eq 0) { 'user.direct_host' } else { "role.performance-$($index % 3)" }
        speakerName = if ($index % 3 -eq 0) { '我' } else { "性能角色 $($index % 3)" }
        visibility = 'public'
        audienceRoleIds = @()
        text = $text
        state = 'completed'
        occurredAt = $startAt.AddSeconds($index).ToString('O')
    })
}
$session = [ordered]@{
    sessionVersion = 1
    sessionId = 'session.performance-1000'
    workspaceId = 'workspace.default'
    title = "$MessageCount 条长会话性能验收"
    groupId = 'group.general'
    phase = 'draft'
    createdAt = $startAt.ToString('O')
    updatedAt = $startAt.AddSeconds($MessageCount).ToString('O')
    agenda = [ordered]@{ subject = '长会话性能验收'; objectives = @(); constraints = @() }
    participants = @()
    messages = $messages
}
[System.IO.File]::WriteAllText(
    (Join-Path $sessionDirectory 'session.performance-1000.json'),
    ($session | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PiRoundtablePerfCapture {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
$startInfo.WorkingDirectory = $resolvedAppRoot
$startInfo.UseShellExecute = $false
$startInfo.Environment['PI_ROUNDTABLE_DATA_ROOT'] = $dataRoot
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$process = [System.Diagnostics.Process]::Start($startInfo)
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $transcript = $null
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) {
            throw "Windows app exited early with code $($process.ExitCode)."
        }
        if ($process.MainWindowHandle -ne 0) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty,
                '公开会话记录')
            $transcript = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        }
    } while ($null -eq $transcript -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $transcript) {
        throw 'Public transcript automation element was not available within 30 seconds.'
    }
    $startupMs = $stopwatch.ElapsedMilliseconds
    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $realizedBefore = $transcript.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition).Count

    $scroll = [System.Diagnostics.Stopwatch]::StartNew()
    $scrollPattern = $transcript.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scrollPattern.SetScrollPercent(
        [System.Windows.Automation.ScrollPattern]::NoScroll,
        100)
    Start-Sleep -Milliseconds 750
    $scroll.Stop()
    $process.Refresh()
    $realizedAfter = $transcript.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition).Count

    $rect = New-Object PiRoundtablePerfCapture+RECT
    [PiRoundtablePerfCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        [PiRoundtablePerfCapture]::PrintWindow($process.MainWindowHandle, $hdc, 2) | Out-Null
    } finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }
    $screenshot = Join-Path $resolvedOutput 'transcript-1000.png'
    $bitmap.Save($screenshot, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $report = [ordered]@{
        messageCount = $MessageCount
        startupMs = $startupMs
        scrollToEndMs = $scroll.ElapsedMilliseconds
        realizedItemsBefore = $realizedBefore
        realizedItemsAfter = $realizedAfter
        workingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 1)
        responding = $process.Responding
        screenshot = $screenshot
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $reportPath = Join-Path $resolvedOutput 'transcript-1000-report.json'
    [System.IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 4),
        [System.Text.UTF8Encoding]::new($false))
    $report | Format-List
    if (!$process.Responding -or $startupMs -gt 30000 -or $scroll.ElapsedMilliseconds -gt 5000 -or
        $realizedBefore -ge $MessageCount -or $realizedAfter -ge $MessageCount) {
        throw 'Transcript performance acceptance failed.'
    }
} finally {
    if ($null -ne $process -and !$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(10000)) {
            $process.Kill($true)
            $process.WaitForExit()
        }
    }
    if ($null -ne $process) {
        $process.Dispose()
    }
}
