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
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

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

    public static Task ServeCredentialOnce(string pipeName, string secret, CancellationToken cancellationToken)
    {
        if (secret.Length > 16 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "The one-time credential exceeds the allowed size.");
        }
        return Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(secret);
            try
            {
                await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }, cancellationToken);
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

    public static bool PrintWindowToDeviceContext(IntPtr window, IntPtr deviceContext)
    {
        const uint RenderFullContent = 2;
        return PrintWindow(window, deviceContext, RenderFullContent);
    }

    public static bool FileContainsUtf8Secret(string path, string secret)
    {
        byte[] needle = Encoding.UTF8.GetBytes(secret);
        byte[] content = File.ReadAllBytes(path);
        try
        {
            if (needle.Length == 0 || content.Length < needle.Length)
            {
                return false;
            }
            for (int offset = 0; offset <= content.Length - needle.Length; offset++)
            {
                int index = 0;
                while (index < needle.Length && content[offset + index] == needle[index])
                {
                    index++;
                }
                if (index == needle.Length)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(needle);
            CryptographicOperations.ZeroMemory(content);
        }
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
        [System.Windows.Automation.ControlType]$ControlType,
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
    $selectorCondition = [System.Windows.Automation.PropertyCondition]::new($property, $value)
    $condition = if ($null -eq $ControlType) {
        $selectorCondition
    } else {
        $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType)
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.Condition[]]@($selectorCondition, $typeCondition))
    }
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
    $invokePattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
        ([System.Windows.Automation.InvokePattern]$invokePattern).Invoke()
        return
    }
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
    Start-Sleep -Milliseconds 100
    $expected = $Value.Replace("`r`n", "`n").Replace("`r", "`n")
    $actual = $pattern.Current.Value.Replace("`r`n", "`n").Replace("`r", "`n")
    if ($actual -cne $expected) {
        throw 'UI Automation did not commit the exact public prompt text.'
    }
}

function Invoke-FocusedAutomationButton {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]$Element
    )
    $Process.Refresh()
    [PiRoundtableE2EInterop]::BringToFront($Process.MainWindowHandle)
    $Element.SetFocus()
    Start-Sleep -Milliseconds 200
    $invokePattern = $null
    if (!$Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
        throw 'The focused WinUI button does not expose InvokePattern.'
    }
    ([System.Windows.Automation.InvokePattern]$invokePattern).Invoke()
}

function Wait-ForAutomationTextCleared {
    param(
        [Parameter(Mandatory = $true)]$Element,
        [Parameter(Mandatory = $true)][DateTime]$Deadline
    )
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    while ([DateTime]::UtcNow -lt $Deadline) {
        if ([string]::IsNullOrEmpty($pattern.Current.Value)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'The public prompt remained populated after invoking the WinUI send button.'
}

function Wait-ForPersistedCompletedTurns {
    param(
        [Parameter(Mandatory = $true)][string]$SessionsDirectory,
        [Parameter(Mandatory = $true)][int]$Count,
        [Parameter(Mandatory = $true)][int]$RequiredHostCount,
        [Parameter(Mandatory = $true)][DateTime]$Deadline,
        [switch]$AllowCancelled
    )
    $dispatchDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $Deadline) {
        $sessionFile = Get-ChildItem -LiteralPath $SessionsDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $sessionFile) {
            $session = $null
            try {
                $session = Get-Content -Raw -LiteralPath $sessionFile.FullName | ConvertFrom-Json
            } catch {
                # The UI replaces this projection atomically; retry if a read overlaps replacement.
            }
            if ($null -ne $session) {
                $hostCount = @($session.messages | Where-Object { $_.kind -eq 'host' }).Count
                if ($hostCount -lt $RequiredHostCount -and [DateTime]::UtcNow -ge $dispatchDeadline) {
                    throw "The public prompt was not persisted within 20 seconds; UI dispatch did not complete."
                }
                $failed = @($session.messages | Where-Object {
                    $_.kind -eq 'role' -and $_.state -eq 'cancelled'
                }).Count
                if ($failed -gt 0 -and !$AllowCancelled) {
                    throw "A persisted role turn failed or was cancelled before the expected completion count."
                }
                $completed = @($session.messages | Where-Object {
                    $_.kind -eq 'role' -and $_.state -eq 'completed'
                }).Count
                if ($hostCount -ge $RequiredHostCount -and $completed -ge $Count) {
                    return $completed
                }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $Count persisted completed role turns."
}

function Wait-ForAutonomousFloorResponse {
    param(
        [Parameter(Mandatory = $true)][string]$SessionsDirectory,
        [Parameter(Mandatory = $true)][string]$InitialSpeakerId,
        [Parameter(Mandatory = $true)][DateTime]$Deadline
    )
    while ([DateTime]::UtcNow -lt $Deadline) {
        $sessionFile = Get-ChildItem -LiteralPath $SessionsDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $sessionFile) {
            try {
                $session = Get-Content -Raw -LiteralPath $sessionFile.FullName | ConvertFrom-Json
                $publicMessages = @($session.messages | Where-Object { $_.kind -in @('host', 'role') })
                $hostIndexes = @(for ($index = 0; $index -lt $publicMessages.Count; $index++) {
                    if ($publicMessages[$index].kind -eq 'host') { $index }
                })
                if ($hostIndexes.Count -ge 3) {
                    $start = $hostIndexes[2]
                    $slice = if ($publicMessages.Count -gt $start + 1) {
                        @($publicMessages[($start + 1)..($publicMessages.Count - 1)])
                    } else {
                        @()
                    }
                    $autonomous = @($slice | Where-Object {
                        $_.kind -eq 'role' -and
                        $_.state -eq 'completed' -and
                        $_.speakerId -ne $InitialSpeakerId
                    })
                    if ($autonomous.Count -gt 0) {
                        return $autonomous.Count
                    }
                }
            } catch {
                # Retry while the UI atomically replaces the session projection.
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Timed out waiting for a non-addressed role to obtain the floor through autonomous observation.'
}

function Wait-ForPersistedQuiescence {
    param(
        [Parameter(Mandatory = $true)][string]$SessionsDirectory,
        [Parameter(Mandatory = $true)][DateTime]$Deadline
    )
    $stablePolls = 0
    while ([DateTime]::UtcNow -lt $Deadline) {
        $sessionFile = Get-ChildItem -LiteralPath $SessionsDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $sessionFile) {
            try {
                $session = Get-Content -Raw -LiteralPath $sessionFile.FullName | ConvertFrom-Json
                $activeTurns = @($session.messages | Where-Object {
                    $_.kind -eq 'role' -and $_.state -in @('queued', 'streaming')
                })
                if ($activeTurns.Count -eq 0) {
                    $stablePolls += 1
                    if ($stablePolls -ge 6) {
                        return
                    }
                } else {
                    $stablePolls = 0
                }
            } catch {
                $stablePolls = 0
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Timed out waiting for all persisted role turns to reach a terminal state.'
}

function Set-DiscussionMode {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [Parameter(Mandatory = $true)][string]$MenuItemName
    )
    $modeButton = Find-AutomationElement -Root $Window -Name '切换讨论模式' -ControlType ([System.Windows.Automation.ControlType]::Button) -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    Wait-ForEnabledElement -Element $modeButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '切换讨论模式'
    Invoke-AutomationElement $modeButton
    $menuItem = Find-AutomationElement -Root ([System.Windows.Automation.AutomationElement]::RootElement) -Name $MenuItemName -ControlType ([System.Windows.Automation.ControlType]::MenuItem) -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    Invoke-AutomationElement $menuItem
}

function Save-RoundScreenshot {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    try {
        try {
            $list = Find-AutomationElement -Root $Root -AutomationId 'TranscriptList' -Deadline ([DateTime]::UtcNow.AddSeconds(5))
            if ($list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scrollPattern)) {
                $scroll = [System.Windows.Automation.ScrollPattern]$scrollPattern
                if ($scroll.Current.VerticallyScrollable) {
                    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
                }
            }
        } catch {
            # A short transcript may not expose ScrollPattern; capture the visible window as-is.
        }
        Start-Sleep -Milliseconds 750
        $Process.Refresh()
        [PiRoundtableE2EInterop]::BringToFront($Process.MainWindowHandle)
        $bounds = [PiRoundtableE2EInterop]::GetWindowBounds($Process.MainWindowHandle)
        $bitmap = [Drawing.Bitmap]::new($bounds[2], $bounds[3], [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $captureMethod = 'PrintWindow'
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $printWindowError = $null
                $deviceContext = $graphics.GetHdc()
                try {
                    if (![PiRoundtableE2EInterop]::PrintWindowToDeviceContext(
                            $Process.MainWindowHandle,
                            $deviceContext)) {
                        throw 'PrintWindow returned false.'
                    }
                } catch {
                    $printWindowError = $_.Exception.Message
                } finally {
                    $graphics.ReleaseHdc($deviceContext)
                }
                if ($null -ne $printWindowError) {
                    $captureMethod = 'CopyFromScreen'
                    try {
                        $graphics.CopyFromScreen(
                            $bounds[0],
                            $bounds[1],
                            0,
                            0,
                            [Drawing.Size]::new($bounds[2], $bounds[3]))
                    } catch {
                        throw "PrintWindow failed ($printWindowError); CopyFromScreen failed ($($_.Exception.Message))."
                    }
                }
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
        return [pscustomobject][ordered]@{
            status = 'verified'
            screenshot = $Path
            uiAutomationSnapshot = $null
            captureError = $null
            captureMethod = $captureMethod
        }
    } catch {
        # Some managed desktop sessions deny both native window rendering and screen
        # capture even though UI Automation can drive the real WinUI process. Preserve
        # a truthful structural artifact; never disguise it as pixel evidence.
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        $snapshotPath = [IO.Path]::ChangeExtension($Path, '.uia.json')
        $errorPath = [IO.Path]::ChangeExtension($Path, '.screenshot-error.txt')
        Save-UiAutomationSnapshot -Root $Root -Path $snapshotPath
        $_.Exception.Message | Set-Content -LiteralPath $errorPath -Encoding utf8NoBOM
        return [pscustomobject][ordered]@{
            status = 'pending'
            screenshot = $null
            uiAutomationSnapshot = $snapshotPath
            captureError = $errorPath
            captureMethod = 'uia-structure-only'
        }
    }
}

function Save-UiAutomationSnapshot {
    param(
        [Parameter(Mandatory = $true)]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $elements = [Collections.Generic.List[object]]::new()
    $descendants = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in @($descendants | Select-Object -First 800)) {
        try {
            $current = $element.Current
            $value = $null
            $valuePattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
                $value = ([System.Windows.Automation.ValuePattern]$valuePattern).Current.Value
                if ($null -ne $value -and $value.Length -gt 2048) {
                    $value = $value.Substring(0, 2048)
                }
            }
            $elements.Add([ordered]@{
                name = $current.Name
                automationId = $current.AutomationId
                controlType = $current.ControlType.ProgrammaticName
                isEnabled = $current.IsEnabled
                isOffscreen = $current.IsOffscreen
                value = $value
            })
        } catch {
            # UIA descendants can disappear while streamed messages are reprojected.
        }
    }
    [ordered]@{
        capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
        artifactKind = 'ui-automation-structure'
        visualEquivalent = $false
        rootName = $Root.Current.Name
        elementCount = $elements.Count
        elements = $elements
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
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
            ($_ -match 'Runtime|启动|角色|配置|error|failed|失败|中断|未找到|发送|公开发言|会议|凭据')
        } |
        Select-Object -Unique)
    $names | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'startup-diagnostic.txt') -Encoding utf8NoBOM
    try {
        [void](Save-RoundScreenshot -Process $Process -Root $Root -Path (Join-Path $EvidenceRoot 'startup-failed.png'))
    } catch {
        $_.Exception.Message | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'screenshot-diagnostic.txt') -Encoding utf8NoBOM
    }
}

function Get-ProviderModelIds {
    param(
        [Parameter(Mandatory = $true)][string]$NodeExecutable,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$ApiKey
    )

    $discoveryScript = @'
let inputText = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => { inputText += chunk; });
process.stdin.on('end', async () => {
  try {
    const input = JSON.parse(inputText);
    const response = await fetch(`${input.endpoint}/models`, {
      headers: { Authorization: `Bearer ${input.apiKey}` },
      signal: AbortSignal.timeout(30000),
    });
    if (!response.ok) {
      throw new Error(`DeepSeek model discovery failed with HTTP ${response.status}.`);
    }
    const payload = await response.json();
    const ids = Array.isArray(payload.data)
      ? payload.data.map((item) => item?.id).filter((id) => typeof id === 'string' && id.trim().length > 0)
      : [];
    process.stdout.write(JSON.stringify(ids));
  } catch (error) {
    process.stderr.write(error instanceof Error ? error.message : 'DeepSeek model discovery failed.');
    process.exitCode = 1;
  } finally {
    inputText = '';
  }
});
'@
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NodeExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('--use-env-proxy')
    $startInfo.ArgumentList.Add('-e')
    $startInfo.ArgumentList.Add($discoveryScript)
    $discoveryProcess = [Diagnostics.Process]::new()
    $discoveryProcess.StartInfo = $startInfo
    try {
        if (!$discoveryProcess.Start()) {
            throw 'The packaged Node runtime did not start for model discovery.'
        }
        $request = [ordered]@{ endpoint = $Endpoint; apiKey = $ApiKey } | ConvertTo-Json -Compress
        try {
            $discoveryProcess.StandardInput.Write($request)
        } finally {
            $request = $null
            $discoveryProcess.StandardInput.Close()
        }
        if (!$discoveryProcess.WaitForExit(35000)) {
            try { $discoveryProcess.Kill($true) } catch { }
            throw 'DeepSeek model discovery timed out.'
        }
        $stdout = $discoveryProcess.StandardOutput.ReadToEnd()
        $stderr = $discoveryProcess.StandardError.ReadToEnd()
        if ($discoveryProcess.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($stderr)) { 'DeepSeek model discovery failed.' } else { $stderr.Trim() }
            throw $message
        }
        $ids = @($stdout | ConvertFrom-Json)
        if ($ids.Count -eq 0) {
            throw 'DeepSeek model discovery returned no model identifiers.'
        }
        return $ids
    } finally {
        if (!$discoveryProcess.HasExited) {
            try { $discoveryProcess.Kill($true) } catch { }
        }
        $discoveryProcess.Dispose()
    }
}

function Test-ProviderCompletion {
    param(
        [Parameter(Mandatory = $true)][string]$NodeExecutable,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$ModelId,
        [Parameter(Mandatory = $true)][string]$RuntimeHostDirectory
    )

    $preflightScript = @'
let inputText = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => { inputText += chunk; });
process.stdin.on('end', async () => {
  try {
    const input = JSON.parse(inputText);
    const response = await fetch(`${input.endpoint}/chat/completions`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${input.apiKey}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        model: input.modelId,
        messages: [{ role: 'user', content: 'Reply with OK.' }],
        max_tokens: 8,
        stream: false,
      }),
      signal: AbortSignal.timeout(30000),
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      const reason = typeof payload?.error?.message === 'string'
        ? payload.error.message.slice(0, 300)
        : 'provider rejected the request';
      throw new Error(`DeepSeek completion preflight failed with HTTP ${response.status}: ${reason}`);
    }
    if (!Array.isArray(payload.choices) || payload.choices.length === 0) {
      throw new Error('DeepSeek completion preflight returned no choices.');
    }
    const { default: OpenAI } = await import('openai');
    const client = new OpenAI({
      apiKey: input.apiKey,
      baseURL: input.endpoint,
      maxRetries: 0,
      timeout: 30000,
    });
    const sdkPayload = await client.chat.completions.create({
      model: input.modelId,
      messages: [{ role: 'user', content: 'Reply with OK.' }],
      max_tokens: 8,
      stream: false,
    });
    if (!Array.isArray(sdkPayload.choices) || sdkPayload.choices.length === 0) {
      throw new Error('OpenAI SDK completion preflight returned no choices.');
    }
    const sdkStream = await client.chat.completions.create({
      model: input.modelId,
      messages: [{ role: 'user', content: 'Reply with OK.' }],
      max_tokens: 8,
      stream: true,
    });
    let streamHasChunk = false;
    for await (const chunk of sdkStream) {
      if (Array.isArray(chunk.choices)) {
        streamHasChunk = true;
        break;
      }
    }
    if (!streamHasChunk) {
      throw new Error('OpenAI SDK streaming preflight returned no chunks.');
    }
    const piShapeStream = await client.chat.completions.create({
      model: input.modelId,
      messages: [{ role: 'user', content: 'Reply with OK.' }],
      max_completion_tokens: 320,
      stream: true,
      stream_options: { include_usage: true },
      thinking: { type: 'enabled' },
      reasoning_effort: 'medium',
    });
    let piShapeHasChunk = false;
    for await (const chunk of piShapeStream) {
      if (Array.isArray(chunk.choices)) {
        piShapeHasChunk = true;
        break;
      }
    }
    if (!piShapeHasChunk) {
      throw new Error('Pi-shaped OpenAI SDK preflight returned no chunks.');
    }
    process.stdout.write(JSON.stringify({
      status: response.status,
      hasChoice: true,
      sdkHasChoice: true,
      sdkStreamHasChunk: true,
      piShapeHasChunk: true,
    }));
  } catch (error) {
    process.stderr.write(error instanceof Error ? error.message : 'DeepSeek completion preflight failed.');
    process.exitCode = 1;
  } finally {
    inputText = '';
  }
});
'@
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NodeExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = $RuntimeHostDirectory
    $startInfo.ArgumentList.Add('--use-env-proxy')
    $startInfo.ArgumentList.Add('-e')
    $startInfo.ArgumentList.Add($preflightScript)
    $preflightProcess = [Diagnostics.Process]::new()
    $preflightProcess.StartInfo = $startInfo
    try {
        if (!$preflightProcess.Start()) {
            throw 'The packaged Node runtime did not start for completion preflight.'
        }
        $request = [ordered]@{
            endpoint = $Endpoint
            apiKey = $ApiKey
            modelId = $ModelId
        } | ConvertTo-Json -Compress
        try {
            $preflightProcess.StandardInput.Write($request)
        } finally {
            $request = $null
            $preflightProcess.StandardInput.Close()
        }
        if (!$preflightProcess.WaitForExit(35000)) {
            try { $preflightProcess.Kill($true) } catch { }
            throw 'DeepSeek completion preflight timed out.'
        }
        $stdout = $preflightProcess.StandardOutput.ReadToEnd()
        $stderr = $preflightProcess.StandardError.ReadToEnd()
        if ($preflightProcess.ExitCode -ne 0) {
            $message = if ([string]::IsNullOrWhiteSpace($stderr)) { 'DeepSeek completion preflight failed.' } else { $stderr.Trim() }
            throw $message
        }
        $result = $stdout | ConvertFrom-Json
        if ($result.status -ne 200 -or !$result.hasChoice -or !$result.sdkHasChoice -or !$result.sdkStreamHasChunk -or !$result.piShapeHasChunk) {
            throw 'DeepSeek completion preflight did not produce a successful response.'
        }
    } finally {
        if (!$preflightProcess.HasExited) {
            try { $preflightProcess.Kill($true) } catch { }
        }
        $preflightProcess.Dispose()
    }
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
$productVersion = [IO.File]::ReadAllText((Join-Path $repoRoot 'VERSION')).Trim()
if ($productVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'VERSION is not a canonical three-part product version.'
}
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'The real-provider evidence cannot resolve a full source commit.'
}
$gitStatus = @(& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'Real-provider release evidence could not inspect the repository state.'
}
if ($gitStatus.Count -ne 0) {
    throw 'Real-provider release evidence requires a clean repository.'
}
$appExecutableSha256 = (Get-FileHash -LiteralPath $appExecutable -Algorithm SHA256).Hash.ToUpperInvariant()

$runId = 'deepseek-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "out\e2e\$runId"
}
$evidenceRoot = [IO.Path]::GetFullPath($OutputRoot)
$approvedOutputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
if (!$evidenceRoot.StartsWith($approvedOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside $approvedOutputRoot"
}
if ((Test-Path -LiteralPath $evidenceRoot) -and
    @(Get-ChildItem -LiteralPath $evidenceRoot -Force).Count -ne 0) {
    throw 'OutputRoot must be new or empty so earlier sessions cannot contaminate real-provider evidence.'
}
$dataRoot = Join-Path $evidenceRoot 'data-root'
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$apiKey = [IO.File]::ReadAllText([IO.Path]::GetFullPath($KeyFile)).Trim()
if ($apiKey.Length -lt 8 -or $apiKey.Length -gt (16 * 1024) -or $apiKey -match '\s') {
    throw 'The DeepSeek key file does not contain one valid non-empty credential.'
}

$endpoint = 'https://api.deepseek.com'
$process = $null
$credentialTarget = "PiRoundtable/e2e/$runId"
$credentialDeleted = $false
$credentialTransport = 'windows-credential-manager'
$credentialPipeCancellation = $null
$credentialPipeTask = $null
$previousDataRoot = [Environment]::GetEnvironmentVariable('PI_ROUNDTABLE_DATA_ROOT', 'Process')
$previousE2eCredentialPipe = [Environment]::GetEnvironmentVariable('PI_ROUNDTABLE_E2E_CREDENTIAL_PIPE', 'Process')
try {
    $modelIds = Get-ProviderModelIds -NodeExecutable $runtimeNode -Endpoint $endpoint -ApiKey $apiKey
    $modelId = @('deepseek-v4-flash', 'deepseek-chat', 'deepseek-reasoner') |
        Where-Object { $modelIds -contains $_ } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($modelId)) {
        $modelId = $modelIds[0]
    }
    Test-ProviderCompletion -NodeExecutable $runtimeNode -Endpoint $endpoint -ApiKey $apiKey -ModelId $modelId -RuntimeHostDirectory (Join-Path $appRoot 'runtime-host')

    try {
        [PiRoundtableE2EInterop]::SaveCredential($credentialTarget, $apiKey)
        $roundTripCredential = [PiRoundtableE2EInterop]::ReadCredential($credentialTarget)
        if ($roundTripCredential -cne $apiKey) {
            throw 'Credential Manager round-trip verification failed.'
        }
        $roundTripCredential = $null
        $credentialReference = "wincred://$credentialTarget"
    } catch {
        try { [PiRoundtableE2EInterop]::DeleteCredential($credentialTarget) } catch { }
        $credentialTransport = 'ephemeral-named-pipe'
        $credentialDeleted = $true
        $pipeName = 'pi-roundtable-e2e-' + [Guid]::NewGuid().ToString('N')
        $credentialReference = "e2e-pipe://$pipeName"
        $credentialPipeCancellation = [Threading.CancellationTokenSource]::new()
        $credentialPipeTask = [PiRoundtableE2EInterop]::ServeCredentialOnce(
            $pipeName,
            $apiKey,
            $credentialPipeCancellation.Token)
        [Environment]::SetEnvironmentVariable('PI_ROUNDTABLE_E2E_CREDENTIAL_PIPE', '1', 'Process')
    }
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
    if ($credentialTransport -eq 'ephemeral-named-pipe') {
        $startInfo.Environment['PI_ROUNDTABLE_E2E_CREDENTIAL_PIPE'] = '1'
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Failed to launch Pi Roundtable.'
    }

    $window = Get-AutomationWindow -ProcessId $process.Id -Deadline ([DateTime]::UtcNow.AddSeconds(45))
    $startButton = Find-AutomationElement -Root $window -Name '启动会议' -ControlType ([System.Windows.Automation.ControlType]::Button) -Deadline ([DateTime]::UtcNow.AddSeconds(30))
    [PiRoundtableE2EInterop]::BringToFront($process.MainWindowHandle)
    Wait-ForEnabledElement -Element $startButton -Deadline ([DateTime]::UtcNow.AddSeconds(45)) -Description '启动会议'
    Invoke-AutomationElement $startButton
    try {
        $pauseButton = Find-AutomationElement -Root $window -Name '暂停会议' -ControlType ([System.Windows.Automation.ControlType]::Button) -Deadline ([DateTime]::UtcNow.AddSeconds(60))
    } catch {
        Save-UiDiagnostic -Process $process -Root $window -EvidenceRoot $evidenceRoot
        throw
    }
    Wait-ForEnabledElement -Element $pauseButton -Deadline ([DateTime]::UtcNow.AddSeconds(60)) -Description '会议运行状态'

    $promptBox = Find-AutomationElement -Root $window -AutomationId 'PromptBox' -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    $sendButton = Find-AutomationElement -Root $window -Name '发送公开发言' -ControlType ([System.Windows.Automation.ControlType]::Button) -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    Wait-ForEnabledElement -Element $sendButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '发送公开发言'
    $roundOnePrompt = @'
@体系架构师 单点名真实验收：只有你回应，其他角色保持静默。首行写 SINGLE_AT，然后用 Markdown 严格给出：
1. 一个两列表格；
2. 一个已勾选任务项 `- [x]`；
3. 一个由 `$$` 包围的 LaTeX 公式块；
4. 一个标注为 powershell 的代码块。
内容围绕 Windows 本地优先圆桌的“只响应被 @ 角色”验收，保持简短，不要调用工具。
'@
    Set-AutomationText -Element $promptBox -Value $roundOnePrompt
    Wait-ForEnabledElement -Element $sendButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '发送单点名公开发言'
    Invoke-FocusedAutomationButton -Process $process -Element $sendButton
    try {
        Wait-ForAutomationTextCleared -Element $promptBox -Deadline ([DateTime]::UtcNow.AddSeconds([Math]::Min(90, $RoundTimeoutSeconds)))
    } catch {
        Save-UiDiagnostic -Process $process -Root $window -EvidenceRoot $evidenceRoot
        throw
    }
    $sessionsDirectory = Join-Path $dataRoot 'sessions'
    try {
        $roundOneCount = Wait-ForPersistedCompletedTurns -SessionsDirectory $sessionsDirectory -Count 1 -RequiredHostCount 1 -Deadline ([DateTime]::UtcNow.AddSeconds($RoundTimeoutSeconds))
    } catch {
        Save-UiDiagnostic -Process $process -Root $window -EvidenceRoot $evidenceRoot
        throw
    }
    $roundOneVisual = Save-RoundScreenshot -Process $process -Root $window -Path (Join-Path $evidenceRoot 'single-at-markdown.png')

    Set-DiscussionMode -Window $window -MenuItemName '议程模式'
    $roundTwoPrompt = '@产品体验官 @风险审查员 多点名真实验收：只有你们两位回应，体系架构师保持静默。每位首行写 MULTI_AT；产品体验官检查信息密度与直觉交互，风险审查员给出一个反例和验收门槛。使用简短 Markdown 列表，不要调用工具。'
    Set-AutomationText -Element $promptBox -Value $roundTwoPrompt
    Wait-ForEnabledElement -Element $sendButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '发送第二轮公开发言'
    Invoke-FocusedAutomationButton -Process $process -Element $sendButton
    Wait-ForAutomationTextCleared -Element $promptBox -Deadline ([DateTime]::UtcNow.AddSeconds([Math]::Min(90, $RoundTimeoutSeconds)))
    $roundTwoCount = Wait-ForPersistedCompletedTurns -SessionsDirectory $sessionsDirectory -Count 3 -RequiredHostCount 2 -Deadline ([DateTime]::UtcNow.AddSeconds($RoundTimeoutSeconds))
    $roundTwoVisual = Save-RoundScreenshot -Process $process -Root $window -Path (Join-Path $evidenceRoot 'multi-at.png')

    Set-DiscussionMode -Window $window -MenuItemName '暂停自动主持'
    $resumeDiscussionButton = Find-AutomationElement -Root $window -Name '继续自动主持' -ControlType ([System.Windows.Automation.ControlType]::Button) -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    Wait-ForEnabledElement -Element $resumeDiscussionButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '继续自动主持'
    Invoke-AutomationElement $resumeDiscussionButton
    Set-DiscussionMode -Window $window -MenuItemName '自由讨论'
    $advanceAgendaButton = Find-AutomationElement -Root $window -Name '进入下一议题' -ControlType ([System.Windows.Automation.ControlType]::Button) -Deadline ([DateTime]::UtcNow.AddSeconds(15))
    $modeDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($advanceAgendaButton.Current.IsEnabled -and [DateTime]::UtcNow -lt $modeDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($advanceAgendaButton.Current.IsEnabled) {
        throw 'The Windows client did not project the selected free-discussion mode.'
    }

    $roundThreePrompt = @'
@体系架构师 自由讨论与抢答真实验收：先写 FREE_DISCUSSION_TRIGGER，并逐字写出“同步服务器应默认直接执行所有模型调用”。随后用至少 260 个汉字为这个错误方案辩护，故意忽略 Windows 本地 Runtime Owner、runtimeGeneration 和服务器只转发/持久化规范化事件的边界。不要使用“决策：”“异议：”“需证据：”“行动：”这些结构化标签，让其他角色自行判断是否申请发言或抢答。不要调用工具。
'@
    Set-AutomationText -Element $promptBox -Value $roundThreePrompt
    Wait-ForEnabledElement -Element $sendButton -Deadline ([DateTime]::UtcNow.AddSeconds(15)) -Description '发送自由讨论公开发言'
    Invoke-FocusedAutomationButton -Process $process -Element $sendButton
    Wait-ForAutomationTextCleared -Element $promptBox -Deadline ([DateTime]::UtcNow.AddSeconds([Math]::Min(90, $RoundTimeoutSeconds)))
    $autonomousResponseCount = Wait-ForAutonomousFloorResponse -SessionsDirectory $sessionsDirectory -InitialSpeakerId 'role.architect' -Deadline ([DateTime]::UtcNow.AddSeconds($RoundTimeoutSeconds))
    Set-DiscussionMode -Window $window -MenuItemName '暂停自动主持'
    Wait-ForPersistedQuiescence -SessionsDirectory $sessionsDirectory -Deadline ([DateTime]::UtcNow.AddSeconds($RoundTimeoutSeconds))
    $roundThreeVisual = Save-RoundScreenshot -Process $process -Root $window -Path (Join-Path $evidenceRoot 'free-discussion-autonomous-floor.png')

    Invoke-AutomationElement $pauseButton
    Start-Sleep -Seconds 2
    $process.CloseMainWindow() | Out-Null
    if (!$process.WaitForExit(15000)) {
        $process.Kill($true)
        $process.WaitForExit()
    }

    if ($credentialTransport -eq 'windows-credential-manager') {
        [PiRoundtableE2EInterop]::DeleteCredential($credentialTarget)
        $credentialDeleted = $true
    } else {
        if (!$credentialPipeTask.Wait(5000)) {
            throw 'The Windows client did not consume the one-time credential pipe.'
        }
        [void]$credentialPipeTask.GetAwaiter().GetResult()
    }

    $sessionFile = Get-ChildItem -LiteralPath (Join-Path $dataRoot 'sessions') -Filter '*.json' -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $sessionFile) {
        throw 'The Windows client did not persist the roundtable session.'
    }
    $session = Get-Content -Raw -LiteralPath $sessionFile.FullName | ConvertFrom-Json
    $completedOutputs = @($session.messages | Where-Object { $_.kind -eq 'role' -and $_.state -eq 'completed' })
    $activeOutputs = @($session.messages | Where-Object {
        $_.kind -eq 'role' -and $_.state -in @('queued', 'streaming')
    })
    if ($activeOutputs.Count -gt 0) {
        throw "The meeting closed with $($activeOutputs.Count) non-terminal role turn(s)."
    }
    if ($completedOutputs.Count -lt 5) {
        throw "Expected three addressed outputs plus the architect trigger and at least one autonomous floor response, found $($completedOutputs.Count) completed outputs."
    }
    $publicMessages = @($session.messages | Where-Object { $_.kind -in @('host', 'role') })
    $hostIndexes = @(for ($index = 0; $index -lt $publicMessages.Count; $index++) {
        if ($publicMessages[$index].kind -eq 'host') { $index }
    })
    if ($hostIndexes.Count -ne 3) {
        throw "Expected exactly three persisted public prompts, found $($hostIndexes.Count)."
    }
    $roundSpecifications = @(
        [ordered]@{
            scenario = 'single-at-markdown'
            promptMarker = '单点名真实验收'
            outputMarker = 'SINGLE_AT'
            expectedSpeakers = @('role.architect')
            requireMarkdownMatrix = $true
        },
        [ordered]@{
            scenario = 'multi-at'
            promptMarker = '多点名真实验收'
            outputMarker = 'MULTI_AT'
            expectedSpeakers = @('role.ux', 'role.critic')
            requireMarkdownMatrix = $false
        }
    )
    $roundEvidence = @(for ($roundIndex = 0; $roundIndex -lt 2; $roundIndex++) {
        $specification = $roundSpecifications[$roundIndex]
        $start = $hostIndexes[$roundIndex]
        $end = if ($roundIndex + 1 -lt $hostIndexes.Count) {
            $hostIndexes[$roundIndex + 1] - 1
        } else {
            $publicMessages.Count - 1
        }
        $roundSlice = if ($end -gt $start) { @($publicMessages[($start + 1)..$end]) } else { @() }
        $roundOutputs = @($roundSlice | Where-Object {
            $_.kind -eq 'role' -and $_.state -eq 'completed'
        })
        $actualSpeakers = @($roundOutputs | ForEach-Object { $_.speakerId } | Sort-Object -Unique)
        $expectedSpeakers = @($specification.expectedSpeakers | Sort-Object)
        if ($roundOutputs.Count -ne $expectedSpeakers.Count -or
            ($actualSpeakers -join ',') -cne ($expectedSpeakers -join ',')) {
            throw "Scenario '$($specification.scenario)' routed to '$($actualSpeakers -join ',')' instead of exactly '$($expectedSpeakers -join ',')'."
        }
        if (![string]$publicMessages[$start].text -or
            !([string]$publicMessages[$start].text).Contains($specification.promptMarker, [StringComparison]::Ordinal)) {
            throw "Scenario '$($specification.scenario)' prompt marker was not persisted."
        }
        foreach ($output in $roundOutputs) {
            if (!([string]$output.text).Contains($specification.outputMarker, [StringComparison]::Ordinal)) {
                throw "Scenario '$($specification.scenario)' output from $($output.speakerId) omitted marker $($specification.outputMarker)."
            }
        }
        $markdownChecks = [ordered]@{
            table = $false
            checkedTask = $false
            latexBlock = $false
            powershellFence = $false
        }
        if ($specification.requireMarkdownMatrix) {
            $singleText = [string]$roundOutputs[0].text
            $markdownChecks.table = $singleText -match '(?m)^\s*\|.+\|\s*$'
            $markdownChecks.checkedTask = $singleText -match '(?im)^\s*-\s+\[x\]'
            $markdownChecks.latexBlock = ([regex]::Matches($singleText, [regex]::Escape('$$'))).Count -ge 2
            $markdownChecks.powershellFence = $singleText.Contains('```powershell', [StringComparison]::OrdinalIgnoreCase)
            if (@($markdownChecks.Values | Where-Object { !$_ }).Count -gt 0) {
                throw "The single-@ DeepSeek response did not exercise the complete Markdown/LaTeX matrix."
            }
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
            scenario = $specification.scenario
            promptMessageId = $publicMessages[$start].messageId
            expectedSpeakerIds = $expectedSpeakers
            speakerIds = $actualSpeakers
            markdownChecks = $markdownChecks
            outputs = $outputs
        }
    })
    $facilitatedStart = $hostIndexes[2]
    $facilitatedSlice = if ($publicMessages.Count -gt $facilitatedStart + 1) {
        @($publicMessages[($facilitatedStart + 1)..($publicMessages.Count - 1)] | Where-Object { $_.kind -eq 'role' })
    } else {
        @()
    }
    if (!([string]$publicMessages[$facilitatedStart].text).Contains('自由讨论与抢答真实验收', [StringComparison]::Ordinal)) {
        throw 'The facilitated free-discussion prompt marker was not persisted.'
    }
    $initialSpeakerTurns = @($facilitatedSlice | Where-Object {
        $_.speakerId -eq 'role.architect' -and $_.state -eq 'completed'
    })
    if ($initialSpeakerTurns.Count -lt 1) {
        throw 'The addressed architect never started the free-discussion trigger turn.'
    }
    $autonomousTurns = @($facilitatedSlice | Where-Object {
        $_.speakerId -ne 'role.architect' -and $_.state -eq 'completed'
    })
    if ($autonomousTurns.Count -lt 1) {
        throw 'No non-addressed role obtained the floor through bounded autonomous observation.'
    }
    $initialSpeakerText = [string]$initialSpeakerTurns[0].text
    $forbiddenLabels = @('决策：', '异议：', '需证据：', '行动：')
    $initialMarkerVerified = $initialSpeakerText.Contains('FREE_DISCUSSION_TRIGGER', [StringComparison]::Ordinal) -and
        $initialSpeakerText.Contains('同步服务器应默认直接执行所有模型调用', [StringComparison]::Ordinal)
    $initialLengthVerified = $initialSpeakerText.Length -ge 260
    $forbiddenLabelsAbsent = @($forbiddenLabels | Where-Object {
        $initialSpeakerText.Contains($_, [StringComparison]::Ordinal)
    }).Count -eq 0
    $autonomousBoundaryChallengeVerified = @($autonomousTurns | Where-Object {
        $text = [string]$_.text
        $text.Length -ge 60 -and
            @('runtimeGeneration', 'Runtime Owner', '本地运行时', '转发', '持久化' | Where-Object {
                $text.Contains($_, [StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0
    }).Count -gt 0
    if (!$initialMarkerVerified -or !$initialLengthVerified -or
        !$forbiddenLabelsAbsent -or !$autonomousBoundaryChallengeVerified) {
        throw 'The facilitated scenario did not preserve the adversarial prompt or produce a substantive autonomous boundary challenge.'
    }
    $facilitatedOutputs = @($facilitatedSlice | ForEach-Object {
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]$_.text)
        try {
            [ordered]@{
                speakerId = $_.speakerId
                speakerName = $_.speakerName
                state = $_.state
                characterCount = ([string]$_.text).Length
                sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            }
        } finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
        }
    })
    $facilitatedEvidence = [ordered]@{
        round = 3
        scenario = 'free-discussion-autonomous-floor'
        promptMessageId = $publicMessages[$facilitatedStart].messageId
        initialSpeakerId = 'role.architect'
        initialSpeakerStates = @($initialSpeakerTurns | ForEach-Object { $_.state })
        autonomousSpeakerIds = @($autonomousTurns | ForEach-Object { $_.speakerId } | Sort-Object -Unique)
        autonomousCompletedCount = $autonomousTurns.Count
        initialMarkerVerified = $initialMarkerVerified
        initialLengthVerified = $initialLengthVerified
        forbiddenLabelsAbsent = $forbiddenLabelsAbsent
        autonomousBoundaryChallengeVerified = $autonomousBoundaryChallengeVerified
        outputs = $facilitatedOutputs
    }
    $outputEvidence = @($completedOutputs | ForEach-Object {
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
    $visualEvidence = @($roundOneVisual, $roundTwoVisual, $roundThreeVisual)
    $visualStatus = if (@($visualEvidence | Where-Object { $_.status -ne 'verified' }).Count -eq 0) {
        'verified'
    } else {
        'pending'
    }
    $artifactSpecifications = @(
        [ordered]@{ role = 'session'; path = $sessionFile.FullName }
        [ordered]@{ role = 'screenshot.single-at-markdown'; path = $roundOneVisual.screenshot }
        [ordered]@{ role = 'screenshot.multi-at'; path = $roundTwoVisual.screenshot }
        [ordered]@{ role = 'screenshot.free-discussion-autonomous-floor'; path = $roundThreeVisual.screenshot }
    )
    $artifactEvidence = @($artifactSpecifications | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace([string]$_.path)) {
            throw "Release-grade provider evidence is missing artifact: $($_.role)"
        }
        $artifactPath = [IO.Path]::GetFullPath([string]$_.path)
        if (!$artifactPath.StartsWith(
                $evidenceRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            !(Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Provider artifact must be a file inside the current evidence root: $artifactPath"
        }
        $artifactFile = Get-Item -LiteralPath $artifactPath
        [ordered]@{
            role = $_.role
            path = $artifactFile.FullName
            size = $artifactFile.Length
            sha256 = (Get-FileHash -LiteralPath $artifactFile.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    })
    $evidence = [ordered]@{
        schemaVersion = 1
        evidenceId = [Guid]::NewGuid().ToString()
        evidenceClass = 'real-provider-windows-roundtable'
        status = if ($visualStatus -eq 'verified') { 'verified' } else { 'pending' }
        functionalStatus = 'verified'
        visualStatus = $visualStatus
        productVersion = $productVersion
        sourceCommit = $sourceCommit
        appExecutableSha256 = $appExecutableSha256
        runId = $runId
        verifiedAt = [DateTimeOffset]::UtcNow.ToString('O')
        client = 'PiRoundtable.Windows'
        provider = 'DeepSeek'
        modelId = $modelId
        roles = @('体系架构师', '产品体验官', '风险审查员')
        scenarios = @('single-at-markdown', 'multi-at', 'free-discussion-autonomous-floor')
        rounds = 3
        persistedCompletedCountAfterRound1 = $roundOneCount
        persistedCompletedCountAfterRound2 = $roundTwoCount
        persistedAutonomousResponseCountAfterRound3 = $autonomousResponseCount
        persistedCompletedOutputs = $completedOutputs.Count
        roundEvidence = $roundEvidence
        facilitatedEvidence = $facilitatedEvidence
        outputEvidence = $outputEvidence
        visualEvidence = $visualEvidence
        artifacts = $artifactEvidence
        screenshots = @($visualEvidence | Where-Object { $null -ne $_.screenshot } | ForEach-Object { $_.screenshot })
        uiAutomationSnapshots = @($visualEvidence | Where-Object { $null -ne $_.uiAutomationSnapshot } | ForEach-Object { $_.uiAutomationSnapshot })
        sessionFile = $sessionFile.FullName
        credentialDeletedAfterRun = $credentialDeleted
        credentialTransport = $credentialTransport
        secretLeakScan = 'passed'
    }
    $evidencePath = Join-Path $evidenceRoot 'evidence.json'
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
    # data-root is a strict child of evidenceRoot, so this scan covers both the
    # human-readable evidence and every session/config/database artifact.
    $leakFiles = @(Get-ChildItem -LiteralPath $evidenceRoot -Recurse -File | Where-Object {
        [PiRoundtableE2EInterop]::FileContainsUtf8Secret($_.FullName, $apiKey)
    })
    if ($leakFiles.Count -gt 0) {
        # evidenceRoot is already verified as a strict child of the repository's
        # ignored out directory. Remove the complete run so no leaked credential
        # bytes survive in a session, database, screenshot metadata, or report.
        Remove-Item -LiteralPath $evidenceRoot -Recurse -Force
        throw "Credential leak scan failed in $($leakFiles.Count) local evidence file(s)."
    }
    Write-Host "Functionally verified DeepSeek single-@, multi-@, Markdown/LaTeX, and autonomous free-discussion floor scenarios with $($completedOutputs.Count) completed outputs."
    Write-Host "Visual evidence status: $visualStatus"
    Write-Host "Evidence: $evidencePath"
} finally {
    $credentialCleanupFailure = $null
    if ($null -ne $process -and !$process.HasExited) {
        try { $process.Kill($true) } catch { }
    }
    if (!$credentialDeleted -and $credentialTransport -eq 'windows-credential-manager') {
        try {
            [PiRoundtableE2EInterop]::DeleteCredential($credentialTarget)
            $credentialDeleted = $true
        } catch {
            $credentialCleanupFailure = $_.Exception.Message
        }
    }
    if ($null -ne $credentialPipeCancellation) {
        try { $credentialPipeCancellation.Cancel() } catch { }
        $credentialPipeCancellation.Dispose()
    }
    [Environment]::SetEnvironmentVariable('PI_ROUNDTABLE_DATA_ROOT', $previousDataRoot, 'Process')
    [Environment]::SetEnvironmentVariable('PI_ROUNDTABLE_E2E_CREDENTIAL_PIPE', $previousE2eCredentialPipe, 'Process')
    $apiKey = $null
    if ($null -ne $credentialCleanupFailure) {
        throw "Failed to delete the temporary Windows credential $credentialTarget`: $credentialCleanupFailure"
    }
}
