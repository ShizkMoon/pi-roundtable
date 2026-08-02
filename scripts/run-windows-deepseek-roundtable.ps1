param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot '..\out\package\windows-x64\app'),
    [string]$KeyFile = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Deepseek.txt'),
    [string]$OutputRoot,
    [ValidateRange(60, 900)]
    [int]$RoundTimeoutSeconds = 360
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing.Common
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static class PiRoundtableE2EInterop
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    public static void SaveCredential(string targetName, string secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static void DeleteCredential(string targetName)
    {
        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
    }

    public static string ReadCredential(string targetName)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out IntPtr pointer))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static int[] GetWindowBounds(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out Rect rect))
        {
            throw new InvalidOperationException("Cannot resolve the Pi Roundtable window bounds.");
        }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 100 || height < 100)
        {
            throw new InvalidOperationException("Pi Roundtable window bounds are invalid.");
        }
        return new[] { rect.Left, rect.Top, width, height };
    }

    public static void BringToFront(IntPtr window)
    {
        SetForegroundWindow(window);
    }
}
'@

function Get-AutomationWindow {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][DateTime]$Deadline
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    while ([DateTime]::UtcNow -lt $Deadline) {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
        if ($null -ne $window) {
            return $window
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Timed out waiting for the Pi Roundtable window.'
}

function Find-AutomationElement {
    param(
        [Parameter(Mandatory = $true)]$Root,
        [string]$AutomationId,
        [string]$Name,
        [Parameter(Mandatory = $true)][DateTime]$Deadline
    )
    if ([string]::IsNullOrWhiteSpace($AutomationId) -eq [string]::IsNullOrWhiteSpace($Name)) {
        throw 'Specify exactly one UI Automation selector.'
    }
    $property = if (![string]::IsNullOrWhiteSpace($AutomationId)) {
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    } else {
        [System.Windows.Automation.AutomationElement]::NameProperty
    }
    $value = if (![string]::IsNullOrWhiteSpace($AutomationId)) { $AutomationId } else { $Name }
    $condition = [System.Windows.Automation.PropertyCondition]::new($property, $value)
    while ([DateTime]::UtcNow -lt $Deadline) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for UI element: $value"
}

function Invoke-AutomationElement {
    param([Parameter(Mandatory = $true)]$Element)
    $Element.SetFocus()
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

function Wait-ForEnabledElement {
    param(
        [Parameter(Mandatory = $true)]$Element,
        [Parameter(Mandatory = $true)][DateTime]$Deadline,
        [Parameter(Mandatory = $true)][string]$Description
    )
    while ([DateTime]::UtcNow -lt $Deadline) {
        try {
            if ($Element.Current.IsEnabled -and !$Element.Current.IsOffscreen) {
                return
            }
        } catch {
            # The caller will re-resolve a stale element on the next run.
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for enabled UI element: $Description"
}

function Set-AutomationText {
    param(
        [Parameter(Mandatory = $true)]$Element,
        [Parameter(Mandatory = $true)][string]$Value
    )
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Wait-ForPersistedCompletedTurns {
    param(
        [Parameter(Mandatory = $true)][string]$SessionsDirectory,
        [Parameter(Mandatory = $true)][int]$Count,
        [Parameter(Mandatory = $true)][DateTime]$Deadline
    )
    while ([DateTime]::UtcNow -lt $Deadline) {
        $sessionFile = Get-ChildItem -LiteralPath $SessionsDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $sessionFile) {
            try {
                $session = Get-Content -Raw -LiteralPath $sessionFile.FullName | ConvertFrom-Json
                $completed = @($session.messages | Where-Object {
                    $_.kind -eq 'role' -and $_.state -eq 'completed'
                }).Count
                if ($completed -ge $Count) {
                    return $completed
                }
            } catch {
                # The UI replaces this projection atomically; retry if a read overlaps replacement.
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $Count persisted completed role turns."
}

function Save-RoundScreenshot {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    try {
        $list = Find-AutomationElement -Root $Root -AutomationId 'TranscriptList' -Deadline ([DateTime]::UtcNow.AddSeconds(5))
        if ($list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scrollPattern)) {
            $scroll = [System.Windows.Automation.ScrollPattern]$scrollPattern
            if ($scroll.Current.VerticallyScrollable) {
                $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
            }
        }
    } catch {
        # The screenshot remains useful when a short transcript does not expose ScrollPattern.
    }
    Start-Sleep -Milliseconds 750
    $Process.Refresh()
    [PiRoundtableE2EInterop]::BringToFront($Process.MainWindowHandle)
    $bounds = [PiRoundtableE2EInterop]::GetWindowBounds($Process.MainWindowHandle)
    $bitmap = [Drawing.Bitmap]::new($bounds[2], $bounds[3], [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($bounds[0], $bounds[1], 0, 0, [Drawing.Size]::new($bounds[2], $bounds[3]))
            $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $graphics.Dispose()
        }
    } finally {
        $bitmap.Dispose()
    }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -lt 10000) {
        throw "Screenshot was not captured correctly: $Path"
    }
}

function Save-UiDiagnostic {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string]$EvidenceRoot
    )
    $textCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $names = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) |
        ForEach-Object { $_.Current.Name } |
        Where-Object {
            ![string]::IsNullOrWhiteSpace($_) -and
            ($_ -match 'Runtime|启动|角色|配置|error|failed|失败|中断')
        } |
        Select-Object -Unique)
    $names | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'startup-diagnostic.txt') -Encoding utf8NoBOM
    Save-RoundScreenshot -Process $Process -Root $Root -Path (Join-Path $EvidenceRoot 'startup-failed.png')
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$appExecutable = Join-Path $appRoot 'PiRoundtable.Windows.exe'
$runtimeHost = Join-Path $appRoot 'runtime-host\host-main.js'
$runtimeNode = Join-Path $appRoot 'runtime\node.exe'
foreach ($required in @($appExecutable, $runtimeHost, $runtimeNode, $KeyFile)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required E2E input is missing: $required"
    }
}

$runId = 'deepseek-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "out\e2e\$runId"
}
$evidenceRoot = [IO.Path]::GetFullPath($OutputRoot)
$approvedOutputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
if (!$evidenceRoot.StartsWith($approvedOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside $approvedOutputRoot"
}
$dataRoot = Join-Path $evidenceRoot 'data-root'
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$apiKey = [IO.File]::ReadAllText([IO.Path]::GetFullPath($KeyFile)).Trim()
if ($apiKey.Length -lt 8 -or $apiKey -match '\s') {
    throw 'The DeepSeek key file does not contain one valid non-empty credential.'
}

$endpoint = 'https://api.deepseek.com'
$http = [Net.Http.HttpClient]::new()
$process = $null
$credentialTarget = "PiRoundtable/e2e/$runId"
$credentialDeleted = $false
$previousDataRoot = [Environment]::GetEnvironmentVariable('PI_ROUNDTABLE_DATA_ROOT', 'Process')
try {
    $http.Timeout = [TimeSpan]::FromSeconds(30)
    $http.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $apiKey)
    $modelResponse = $http.GetAsync("$endpoint/models").GetAwaiter().GetResult()
    if (!$modelResponse.IsSuccessStatusCode) {
        throw "DeepSeek model discovery failed with HTTP $([int]$modelResponse.StatusCode)."
    }
    $modelPayload = $modelResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    $modelIds = @($modelPayload.data | ForEach-Object { $_.id } | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    if ($modelIds.Count -eq 0) {
        throw 'DeepSeek model discovery returned no model identifiers.'
    }
    $modelId = @('deepseek-v4-flash', 'deepseek-chat', 'deepseek-reasoner') |
        Where-Object { $modelIds -contains $_ } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($modelId)) {
        $modelId = $modelIds[0]
    }

    [PiRoundtableE2EInterop]::SaveCredential($credentialTarget, $apiKey)
    $roundTripCredential = [PiRoundtableE2EInterop]::ReadCredential($credentialTarget)
    if ($roundTripCredential -cne $apiKey) {
        throw 'Credential Manager round-trip verification failed.'
    }
    $roundTripCredential = $null
    $credentialReference = "wincred://$credentialTarget"
    $timestamp = [DateTimeOffset]::UtcNow.ToString('O')
    $modelProfileId = 'model.deepseek.e2e'
    $route = [ordered]@{
        primaryModelProfileId = $modelProfileId
        fallbackModelProfileIds = @()
        thinkingLevel = 'off'
        maxOutputTokens = 320
    }
    $capabilities = [ordered]@{ skillIds = @(); mcpGrants = @(); toolGrants = @() }
    $delegation = [ordered]@{ networkAccess = 'forbidden'; resultMode = 'summary'; maxConcurrentSubagents = 0 }
    $memory = [ordered]@{ mode = 'disabled'; writeApproval = 'always'; promptEvolution = 'disabled' }
    $roles = @(
        [ordered]@{
            roleProfileId = 'role.architect'; displayName = '体系架构师'; description = '分析系统边界和实现顺序'
            systemPrompt = '你是体系架构师。只讨论Windows本地优先AI圆桌客户端的架构边界、可靠性和实现顺序。每轮用中文给出不超过三点，并明确一个可执行结论。不要调用工具。'
            responsibilities = @('架构边界', '可靠性', '实施顺序'); autoJoin = $true
            modelRoute = $route; capabilities = $capabilities; delegation = $delegation; memory = $memory
        },
        [ordered]@{
            roleProfileId = 'role.ux'; displayName = '产品体验官'; description = '分析信息密度和直觉交互'
            systemPrompt = '你是产品体验官。只讨论Windows桌面端的信息密度、自适应、可理解状态和直觉交互。每轮用中文给出不超过三点，并指出一个用户风险。不要调用工具。'
            responsibilities = @('交互逻辑', '信息密度', '可访问性'); autoJoin = $true
            modelRoute = $route; capabilities = $capabilities; delegation = $delegation; memory = $memory
        },
        [ordered]@{
            roleProfileId = 'role.critic'; displayName = '风险审查员'; description = '独立挑战未经验证的结论'
            systemPrompt = '你是风险审查员。独立挑战其他角色的假设，区分已实现、待验证和规划。每轮用中文给出不超过三点，并给出一个验收门槛。不要调用工具。'
            responsibilities = @('反方审查', '证据门槛', '风险控制'); autoJoin = $true
            modelRoute = $route; capabilities = $capabilities; delegation = $delegation; memory = $memory
        }
    )
    $workspace = [ordered]@{
        configurationVersion = 1
        workspaceId = "workspace.$runId"
        displayName = 'DeepSeek 真实圆桌验收'
        updatedAt = $timestamp
        providers = @([ordered]@{
            providerProfileId = 'provider.deepseek.e2e'; displayName = 'DeepSeek'; apiFamily = 'openai_chat_completions'
            runtimeProviderId = 'deepseek'; endpoint = $endpoint; credentialRef = $credentialReference; enabled = $true
        })
        models = @([ordered]@{
            modelProfileId = $modelProfileId; providerProfileId = 'provider.deepseek.e2e'; modelId = $modelId
            displayName = $modelId; capabilities = @('text', 'reasoning'); contextWindow = 128000; enabled = $true
        })
        skills = @(); mcpServers = @(); roles = $roles
        sessionGroups = @([ordered]@{ groupId = 'group.e2e'; displayName = '全链路验收'; kind = 'folder'; sortOrder = 0 })
        defaults = [ordered]@{ modelRoute = $route; delegation = $delegation }
    }
    $workspace | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $dataRoot 'workspace.v1.json') -Encoding utf8NoBOM

    [Environment]::SetEnvironmentVariable('PI_ROUNDTABLE_DATA_ROOT', $dataRoot, 'Process')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $appExecutable
    $startInfo.WorkingDirectory = $appRoot
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['PI_ROUNDTABLE_DATA_ROOT'] = $dataRoot
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Failed to launch Pi Roundtable.'
    }

    $window = Get-AutomationWindow -ProcessId $process.Id -Deadline ([DateTime]::UtcNow.AddSeconds(45))
    $startButton = Find-AutomationElement -Root $window -Name '启动会议' -Deadline ([DateTime]::UtcNow.AddSeconds(30))
    [PiRoundtableE2EInterop]::BringToFront($process.MainWindowHandle)
    Wait-ForEnabledElement -Element $startButton -Deadline ([DateTime]::UtcNow.AddSeconds(45)) -Description '启动会议'
    Invoke-AutomationElement $startButton
    try {
        $pauseButton = Find-AutomationElement -Root $window -Name '暂停会议' -Deadline ([DateTime]::UtcNow.AddSeconds(60))
    } catch {
        Save-UiDiagnostic -Process $process -Root $window -EvidenceRoot $evidenceRoot
        throw
    }
    Wait-ForEnabledElement -Element $pauseButton -Deadline ([DateTime]::UtcNow.AddSeconds(60)) -Description '会议运行状态'

    $promptBox = Find-AutomationElement -Root $window -AutomationId 'PromptBox' -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    $sendButton = Find-AutomationElement -Root $window -Name '发送公开发言' -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    Wait-ForEnabledElement -Element $sendButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '发送公开发言'
    $roundOnePrompt = '@体系架构师 @产品体验官 @风险审查员 第一轮：为 Windows 本地优先的 AI 圆桌客户端，判断下一阶段最重要的一项改进。请从各自职责出发，标记 R1，并给出可验收标准。'
    Set-AutomationText -Element $promptBox -Value $roundOnePrompt
    Invoke-AutomationElement $sendButton
    $sessionsDirectory = Join-Path $dataRoot 'sessions'
    $roundOneCount = Wait-ForPersistedCompletedTurns -SessionsDirectory $sessionsDirectory -Count 3 -Deadline ([DateTime]::UtcNow.AddSeconds($RoundTimeoutSeconds))
    $roundOneScreenshot = Join-Path $evidenceRoot 'round-1.png'
    Save-RoundScreenshot -Process $process -Root $window -Path $roundOneScreenshot

    $roundTwoPrompt = '@体系架构师 @产品体验官 @风险审查员 第二轮：阅读第一轮公开记录，指出至少一个不同意之处并修正自己的建议。标记 R2，最后给出共同验收门槛候选。'
    Set-AutomationText -Element $promptBox -Value $roundTwoPrompt
    Wait-ForEnabledElement -Element $sendButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '发送第二轮公开发言'
    Invoke-AutomationElement $sendButton
    $roundTwoCount = Wait-ForPersistedCompletedTurns -SessionsDirectory $sessionsDirectory -Count 6 -Deadline ([DateTime]::UtcNow.AddSeconds($RoundTimeoutSeconds))
    $roundTwoScreenshot = Join-Path $evidenceRoot 'round-2.png'
    Save-RoundScreenshot -Process $process -Root $window -Path $roundTwoScreenshot

    Invoke-AutomationElement $pauseButton
    Start-Sleep -Seconds 2
    $process.CloseMainWindow() | Out-Null
    if (!$process.WaitForExit(15000)) {
        $process.Kill($true)
        $process.WaitForExit()
    }

    [PiRoundtableE2EInterop]::DeleteCredential($credentialTarget)
    $credentialDeleted = $true

    $sessionFile = Get-ChildItem -LiteralPath (Join-Path $dataRoot 'sessions') -Filter '*.json' -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $sessionFile) {
        throw 'The Windows client did not persist the roundtable session.'
    }
    $session = Get-Content -Raw -LiteralPath $sessionFile.FullName | ConvertFrom-Json
    $completedOutputs = @($session.messages | Where-Object { $_.kind -eq 'role' -and $_.state -eq 'completed' })
    if ($completedOutputs.Count -lt 6) {
        throw "The persisted transcript contains only $($completedOutputs.Count) completed role outputs."
    }
    $expectedSpeakers = @('role.architect', 'role.ux', 'role.critic')
    $publicMessages = @($session.messages | Where-Object { $_.kind -in @('host', 'role') })
    $hostIndexes = @(for ($index = 0; $index -lt $publicMessages.Count; $index++) {
        if ($publicMessages[$index].kind -eq 'host') { $index }
    })
    if ($hostIndexes.Count -ne 2) {
        throw "Expected exactly two persisted public prompts, found $($hostIndexes.Count)."
    }
    $roundEvidence = @(for ($roundIndex = 0; $roundIndex -lt 2; $roundIndex++) {
        $start = $hostIndexes[$roundIndex]
        $end = if ($roundIndex + 1 -lt $hostIndexes.Count) {
            $hostIndexes[$roundIndex + 1] - 1
        } else {
            $publicMessages.Count - 1
        }
        $roundOutputs = @($publicMessages[($start + 1)..$end] | Where-Object {
            $_.kind -eq 'role' -and $_.state -eq 'completed'
        })
        $actualSpeakers = @($roundOutputs | ForEach-Object { $_.speakerId } | Sort-Object -Unique)
        if ($roundOutputs.Count -ne 3 -or
            ($actualSpeakers -join ',') -cne (($expectedSpeakers | Sort-Object) -join ',')) {
            throw "Round $($roundIndex + 1) did not contain exactly one completed output from each expected role."
        }
        $expectedPromptMarker = if ($roundIndex -eq 0) { '第一轮' } else { '第二轮' }
        if (![string]$publicMessages[$start].text -or
            !([string]$publicMessages[$start].text).Contains($expectedPromptMarker, [StringComparison]::Ordinal)) {
            throw "Round $($roundIndex + 1) prompt marker was not persisted."
        }
        $outputs = @($roundOutputs | ForEach-Object {
            $bytes = [Text.Encoding]::UTF8.GetBytes([string]$_.text)
            try {
                [ordered]@{
                    speakerId = $_.speakerId
                    speakerName = $_.speakerName
                    characterCount = ([string]$_.text).Length
                    sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
                }
            } finally {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
            }
        })
        [ordered]@{
            round = $roundIndex + 1
            promptMessageId = $publicMessages[$start].messageId
            speakerIds = $actualSpeakers
            outputs = $outputs
        }
    })
    $outputEvidence = @($roundEvidence | ForEach-Object { $_.outputs })
    $evidence = [ordered]@{
        status = 'verified'
        runId = $runId
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
        client = 'PiRoundtable.Windows'
        provider = 'DeepSeek'
        modelId = $modelId
        roles = @('体系架构师', '产品体验官', '风险审查员')
        rounds = 2
        persistedCompletedCountAfterRound1 = $roundOneCount
        persistedCompletedCountAfterRound2 = $roundTwoCount
        persistedCompletedOutputs = $completedOutputs.Count
        roundEvidence = $roundEvidence
        outputEvidence = $outputEvidence
        screenshots = @($roundOneScreenshot, $roundTwoScreenshot)
        sessionFile = $sessionFile.FullName
        credentialDeletedAfterRun = $credentialDeleted
    }
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'evidence.json') -Encoding utf8NoBOM
    Write-Host "Verified 3-role, 2-round DeepSeek meeting with $($completedOutputs.Count) completed outputs."
    Write-Host "Evidence: $(Join-Path $evidenceRoot 'evidence.json')"
} finally {
    $http.Dispose()
    if ($null -ne $process -and !$process.HasExited) {
        try { $process.Kill($true) } catch { }
    }
    if (!$credentialDeleted) {
        try { [PiRoundtableE2EInterop]::DeleteCredential($credentialTarget) } catch { }
    }
    [Environment]::SetEnvironmentVariable('PI_ROUNDTABLE_DATA_ROOT', $previousDataRoot, 'Process')
    $apiKey = $null
}
