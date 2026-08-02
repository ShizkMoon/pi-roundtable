param(
    [string]$AppRoot = 'apps\windows\PiRoundtable.Windows\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64',

    [string]$OutputRoot = 'artifacts\visual-qa\viewport-matrix',

    [ValidateSet(720, 900, 1280, 1520)]
    [int[]]$ViewportWidths = @(720, 900, 1280, 1520),

    [ValidateRange(560, 1000)]
    [int]$ViewportHeight = 800
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedAppRoot = if ([System.IO.Path]::IsPathRooted($AppRoot)) {
    [System.IO.Path]::GetFullPath($AppRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $AppRoot))
}
$executable = Join-Path $resolvedAppRoot 'PiRoundtable.Windows.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the Windows application first: $executable"
}
$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$dataRoot = Join-Path $resolvedOutput "data-root-$runId"
$sessionDirectory = Join-Path $dataRoot 'sessions'
New-Item -ItemType Directory -Force -Path $sessionDirectory | Out-Null

$startAt = [DateTimeOffset]::Parse('2026-08-02T00:00:00Z')
$markdown = @'
## Markdown 与公式验收

| 能力 | 状态 |
| --- | --- |
| 表格 | **已实现** |
| 任务列表 | 已实现 |

- [x] 只让被 `@` 的角色回应
- [ ] LaTeX 排版引擎（当前安全显示源码）

~~旧的全角色误触发行为~~

$$
E = mc^2
$$

```powershell
Get-Process -Name 'PiRoundtable.Windows'
```
'@
$messages = @(
    [ordered]@{
        messageId = 'message.visual-host'
        kind = 'host'
        speakerId = 'user.direct_host'
        speakerName = '我'
        visibility = 'public'
        audienceRoleIds = @()
        text = '@体系架构师 请只由你说明当前 Markdown 验收结果。'
        state = 'completed'
        occurredAt = $startAt.ToString('O')
    },
    [ordered]@{
        messageId = 'message.visual-role'
        kind = 'role'
        speakerId = 'role.architect'
        speakerName = '体系架构师'
        visibility = 'public'
        audienceRoleIds = @()
        text = $markdown
        state = 'completed'
        occurredAt = $startAt.AddSeconds(1).ToString('O')
    }
)
$session = [ordered]@{
    sessionVersion = 1
    sessionId = 'session.visual-qa'
    workspaceId = 'workspace.default'
    title = 'Windows 自适应与 Markdown 视觉验收'
    groupId = 'group.general'
    phase = 'draft'
    createdAt = $startAt.ToString('O')
    updatedAt = $startAt.AddSeconds(1).ToString('O')
    agenda = [ordered]@{ subject = '视觉验收'; objectives = @(); constraints = @() }
    participants = @()
    messages = $messages
}
[System.IO.File]::WriteAllText(
    (Join-Path $sessionDirectory 'session.visual-qa.json'),
    ($session | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
if ($null -eq ('PiRoundtableVisualCapture' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PiRoundtableVisualCapture {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
}

function Find-ByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Save-WindowCapture {
    param(
        [IntPtr]$Handle,
        [string]$Path
    )
    $rect = New-Object PiRoundtableVisualCapture+RECT
    if (![PiRoundtableVisualCapture]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (![PiRoundtableVisualCapture]::PrintWindow($Handle, $hdc, 2)) {
            throw 'PrintWindow failed.'
        }
    } finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }
    try {
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
}

function Invoke-AutomationButton {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) {
        throw 'The requested automation button was not found.'
    }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-VisibilityByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $element = Find-ByName -Root $Root -Name $Name
    return $null -ne $element -and !$element.Current.IsOffscreen
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
$startInfo.WorkingDirectory = $resolvedAppRoot
$startInfo.UseShellExecute = $false
$startInfo.Environment['PI_ROUNDTABLE_DATA_ROOT'] = $dataRoot
$process = [System.Diagnostics.Process]::Start($startInfo)
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $root = $null
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) {
            throw "Windows app exited early with code $($process.ExitCode)."
        }
        if ($process.MainWindowHandle -ne 0) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        }
    } while (($null -eq $root -or $null -eq (Find-ByName -Root $root -Name '公开会话记录')) -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $root) {
        throw 'Windows automation root was not available within 30 seconds.'
    }

    $dpi = [PiRoundtableVisualCapture]::GetDpiForWindow($process.MainWindowHandle)
    if ($dpi -lt 96) {
        throw "Unexpected window DPI: $dpi"
    }
    $scale = $dpi / 96.0
    $initialWindowRect = New-Object PiRoundtableVisualCapture+RECT
    $initialClientRect = New-Object PiRoundtableVisualCapture+RECT
    [PiRoundtableVisualCapture]::GetWindowRect($process.MainWindowHandle, [ref]$initialWindowRect) | Out-Null
    [PiRoundtableVisualCapture]::GetClientRect($process.MainWindowHandle, [ref]$initialClientRect) | Out-Null
    $nonClientWidth = ($initialWindowRect.Right - $initialWindowRect.Left) - ($initialClientRect.Right - $initialClientRect.Left)
    $nonClientHeight = ($initialWindowRect.Bottom - $initialWindowRect.Top) - ($initialClientRect.Bottom - $initialClientRect.Top)
    $measurements = [System.Collections.Generic.List[object]]::new()
    foreach ($viewportWidth in $ViewportWidths) {
        $physicalWidth = [int][Math]::Round($viewportWidth * $scale)
        $physicalHeight = [int][Math]::Round($ViewportHeight * $scale)
        if (![PiRoundtableVisualCapture]::MoveWindow(
            $process.MainWindowHandle,
            16,
            16,
            $physicalWidth + $nonClientWidth,
            $physicalHeight + $nonClientHeight,
            $true)) {
            throw "MoveWindow failed for $viewportWidth DIP."
        }
        Start-Sleep -Milliseconds 700
        $process.Refresh()
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $clientRect = New-Object PiRoundtableVisualCapture+RECT
        [PiRoundtableVisualCapture]::GetClientRect($process.MainWindowHandle, [ref]$clientRect) | Out-Null
        $actualWidth = [Math]::Round(($clientRect.Right - $clientRect.Left) / $scale, 1)
        $actualHeight = [Math]::Round(($clientRect.Bottom - $clientRect.Top) / $scale, 1)
        $capturePath = Join-Path $resolvedOutput "meeting-$viewportWidth-dip.png"
        Save-WindowCapture -Handle $process.MainWindowHandle -Path $capturePath
        $measurements.Add([ordered]@{
            viewportWidthDip = $viewportWidth
            actualWidthDip = $actualWidth
            actualHeightDip = $actualHeight
            shellNavigationVisible = Get-VisibilityByName -Root $root -Name '角色管理'
            transcriptVisible = Get-VisibilityByName -Root $root -Name '公开会话记录'
            responding = $process.Responding
            screenshot = $capturePath
        })
        if ([Math]::Abs($actualWidth - $viewportWidth) -gt 3 -or !$process.Responding) {
            throw "Viewport acceptance failed for $viewportWidth DIP (actual $actualWidth DIP)."
        }
    }

    [PiRoundtableVisualCapture]::MoveWindow(
        $process.MainWindowHandle,
        16,
        16,
        [int][Math]::Round(1280 * $scale) + $nonClientWidth,
        [int][Math]::Round($ViewportHeight * $scale) + $nonClientHeight,
        $true) | Out-Null
    Start-Sleep -Milliseconds 500
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $transcript = Find-ByName -Root $root -Name '公开会话记录'
    $scrollPattern = $transcript.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scrollPattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    Start-Sleep -Milliseconds 500
    $markdownBottomCapture = Join-Path $resolvedOutput 'markdown-bottom-1280-dip.png'
    Save-WindowCapture -Handle $process.MainWindowHandle -Path $markdownBottomCapture

    $mainAutomationNames = [ordered]@{
        publicTranscript = $null -ne (Find-ByName -Root $root -Name '公开会话记录')
        publicPrompt = $null -ne (Find-ByName -Root $root -Name '公开发言输入')
        sendPublic = $null -ne (Find-ByName -Root $root -Name '发送公开发言')
    }
    $settingsButton = Find-ByName -Root $root -Name '设置'
    Invoke-AutomationButton -Element $settingsButton
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $updateButton = Find-ByName -Root $root -Name '检查软件更新'
    } while ($null -eq $updateButton -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $updateButton -or $updateButton.Current.IsOffscreen) {
        throw 'The updater card was not visible at the top of Settings.'
    }
    foreach ($settingsWidth in @(720, 1280)) {
        [PiRoundtableVisualCapture]::MoveWindow(
            $process.MainWindowHandle,
            16,
            16,
            [int][Math]::Round($settingsWidth * $scale) + $nonClientWidth,
            [int][Math]::Round($ViewportHeight * $scale) + $nonClientHeight,
            $true) | Out-Null
        Start-Sleep -Milliseconds 700
        $settingsCapture = Join-Path $resolvedOutput "settings-$settingsWidth-dip.png"
        Save-WindowCapture -Handle $process.MainWindowHandle -Path $settingsCapture
    }

    $interactiveControlTypes = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($typeName in @(
        'ControlType.Button',
        'ControlType.CheckBox',
        'ControlType.ComboBox',
        'ControlType.Edit',
        'ControlType.Hyperlink',
        'ControlType.ListItem',
        'ControlType.MenuItem',
        'ControlType.RadioButton',
        'ControlType.TabItem')) {
        $interactiveControlTypes.Add($typeName) | Out-Null
    }
    $tabStops = [System.Collections.Generic.List[object]]::new()
    $focusableElements = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($focusable in $focusableElements) {
        try {
            $controlTypeName = $focusable.Current.ControlType.ProgrammaticName
            if ($focusable.Current.ProcessId -ne $process.Id -or
                !$focusable.Current.IsKeyboardFocusable -or
                !$interactiveControlTypes.Contains($controlTypeName)) {
                continue
            }
            $name = $focusable.Current.Name
            $tabStops.Add([ordered]@{
                name = $(if ([string]::IsNullOrWhiteSpace($name)) { '<unnamed>' } else { $name })
                automationId = $focusable.Current.AutomationId
                controlType = $controlTypeName
                className = $focusable.Current.ClassName
            })
        } catch {
            # Ignore a control that disappears while a dynamic page updates.
        }
    }
    $unnamedFocusable = @($tabStops | Where-Object name -eq '<unnamed>')
    if ($tabStops.Count -eq 0 -or $unnamedFocusable.Count -gt 0) {
        $details = $unnamedFocusable | ConvertTo-Json -Compress
        throw "An application keyboard-focusable control is missing its accessible name: $details"
    }

    $report = [ordered]@{
        dpi = $dpi
        rasterizationScale = $scale
        highContrast = [System.Windows.Forms.SystemInformation]::HighContrast
        viewportHeightDip = $ViewportHeight
        measurements = $measurements
        markdownBottomScreenshot = $markdownBottomCapture
        settingsUpdaterVisibleAtTop = $true
        requiredAutomationNames = [ordered]@{
            publicTranscript = $mainAutomationNames.publicTranscript
            publicPrompt = $mainAutomationNames.publicPrompt
            sendPublic = $mainAutomationNames.sendPublic
            checkForUpdates = $null -ne (Find-ByName -Root $root -Name '检查软件更新')
            installVerifiedUpdate = $null -ne (Find-ByName -Root $root -Name '下载并安装已验证更新')
        }
        sampledTabStops = $tabStops
        keyboardSamplingMode = 'UIA focusable interactive controls owned by the application process'
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $reportPath = Join-Path $resolvedOutput 'visual-qa-report.json'
    [System.IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    $report | ConvertTo-Json -Depth 8
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
