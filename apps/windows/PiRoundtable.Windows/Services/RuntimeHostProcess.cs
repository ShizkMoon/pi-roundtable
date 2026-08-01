using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed record RuntimeHostStartOptions(
    string MeetingId,
    string RuntimeId,
    ulong RuntimeGeneration,
    WorkspaceConfiguration Workspace,
    RoundtableSessionConfiguration Session,
    IReadOnlyDictionary<string, string> Credentials);

internal sealed record RuntimeCommandReceipt(
    string CommandId,
    string Status,
    ulong? Sequence,
    string? ErrorCode,
    string? Message)
{
    public bool Accepted => Status is "accepted" or "duplicate";
}

internal sealed record RuntimeMeetingEvent(
    string MeetingId,
    string EventId,
    ulong Sequence,
    ulong RuntimeGeneration,
    string Kind,
    DateTimeOffset OccurredAt,
    string? ActorId,
    string? TargetId,
    string? CausationId,
    JsonElement Payload);

internal sealed class RuntimeHostProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RuntimeCommandReceipt>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateGate = new();
    private Process? _process;
    private EventHandler? _processExitedHandler;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private StreamWriter? _stdin;
    private string _meetingId = string.Empty;
    private string _runtimeId = string.Empty;
    private ulong _runtimeGeneration;
    private int _stderrReported;
    private bool _disposed;

    public event EventHandler<RuntimeMeetingEvent>? MeetingEventReceived;

    public event EventHandler<string>? DiagnosticReceived;

    public async Task StartAsync(RuntimeHostStartOptions options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("Runtime Host is already started.");
        }

        var scriptPath = ResolveHostScript();
        _meetingId = options.MeetingId;
        _runtimeId = options.RuntimeId;
        _runtimeGeneration = options.RuntimeGeneration;
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_NODE_PATH") ?? "node",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["PI_ROUNDTABLE_MEETING_ID"] = options.MeetingId;
        startInfo.Environment["PI_ROUNDTABLE_RUNTIME_ID"] = options.RuntimeId;
        startInfo.Environment["PI_ROUNDTABLE_RUNTIME_GENERATION"] = options.RuntimeGeneration.ToString();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        EventHandler exitedHandler = (_, _) => OnProcessExited(process);
        _processExitedHandler = exitedHandler;
        process.Exited += exitedHandler;
        if (!process.Start())
        {
            process.Exited -= exitedHandler;
            _processExitedHandler = null;
            process.Dispose();
            throw new InvalidOperationException("Runtime Host process did not start.");
        }

        lock (_stateGate)
        {
            _process = process;
        }
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _stdoutTask = ReadStdoutAsync(process.StandardOutput, _lifetime.Token);
        _stderrTask = ReadStderrAsync(process.StandardError, _lifetime.Token);

        await WriteFrameAsync(new
        {
            type = "initialize",
            requestId = Guid.NewGuid().ToString("N"),
            workspace = options.Workspace,
            session = options.Session,
            credentials = options.Credentials,
        }, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _ready.Task.WaitAsync(timeout.Token);
    }

    public async Task<RuntimeCommandReceipt> SendCommandAsync(
        string kind,
        string? actorId,
        string? targetId,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stdin is null)
        {
            throw new InvalidOperationException("Runtime Host is not ready.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<RuntimeCommandReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(commandId, completion))
        {
            throw new InvalidOperationException("Could not reserve a command ID.");
        }

        var command = new Dictionary<string, object?>
        {
            ["protocolVersion"] = 1,
            ["meetingId"] = _meetingId,
            ["commandId"] = commandId,
            ["kind"] = kind,
            ["issuedAt"] = DateTimeOffset.UtcNow,
            ["runtimeGeneration"] = _runtimeGeneration,
            ["payload"] = payload,
        };
        if (!string.IsNullOrWhiteSpace(actorId))
        {
            command["actorId"] = actorId;
        }
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            command["targetId"] = targetId;
        }

        try
        {
            await WriteFrameAsync(new Dictionary<string, object?>
            {
                ["type"] = "command",
                ["command"] = command,
            }, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            _pending.TryRemove(commandId, out _);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_stateGate)
        {
            process = _process;
        }
        if (process is null || HasExited(process))
        {
            return;
        }

        try
        {
            await WriteFrameAsync(new
            {
                type = "shutdown",
                requestId = Guid.NewGuid().ToString("N"),
            }, cancellationToken);
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(exitTimeout.Token);
        }
        catch (Exception error) when (
            error is TimeoutException or OperationCanceledException or IOException or InvalidOperationException)
        {
            KillProcessTree(process);
        }
    }

    public void Terminate()
    {
        Process? process;
        lock (_stateGate)
        {
            process = _process;
        }
        if (process is not null)
        {
            KillProcessTree(process);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopAsync(timeout.Token);
        }
        catch
        {
            // The finally block owns the last-resort process-tree termination.
        }
        finally
        {
            Process? process;
            EventHandler? exitedHandler;
            lock (_stateGate)
            {
                process = _process;
                _process = null;
                exitedHandler = _processExitedHandler;
                _processExitedHandler = null;
            }
            if (process is not null)
            {
                if (exitedHandler is not null)
                {
                    process.Exited -= exitedHandler;
                }
                if (!HasExited(process))
                {
                    KillProcessTree(process);
                }
            }
            FailPending(new ObjectDisposedException(nameof(RuntimeHostProcess)));
            _lifetime.Cancel();
            if (_stdoutTask is not null)
            {
                await IgnoreFailureAsync(_stdoutTask);
            }
            if (_stderrTask is not null)
            {
                await IgnoreFailureAsync(_stderrTask);
            }
            _stdin?.Dispose();
            process?.Dispose();
            _writeGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task WriteFrameAsync<T>(T frame, CancellationToken cancellationToken)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("Runtime Host stdin is closed.");
        var json = JsonSerializer.Serialize(frame, SerializerOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await stdin.WriteLineAsync(json.AsMemory(), cancellationToken);
            await stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            if (line.Length > 1_048_576)
            {
                ReportDiagnostic("Runtime Host 返回了超出限制的数据帧。");
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                HandleFrame(document.RootElement);
            }
            catch (JsonException)
            {
                ReportDiagnostic("Runtime Host 输出了无法解析的数据。");
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                ReportDiagnostic("Runtime Host 输出的数据不符合本地协议。");
            }
        }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            if (Interlocked.Exchange(ref _stderrReported, 1) == 0)
            {
                ReportDiagnostic("Runtime Host 报告了进程级诊断，请检查本地安装和提供商配置。");
            }
        }
    }

    private void HandleFrame(JsonElement frame)
    {
        if (!frame.TryGetProperty("type", out var typeElement))
        {
            return;
        }
        switch (typeElement.GetString())
        {
            case "ready":
                if (
                    frame.GetProperty("protocolVersion").GetInt32() != 2 ||
                    frame.GetProperty("meetingId").GetString() != _meetingId ||
                    frame.GetProperty("runtimeId").GetString() != _runtimeId ||
                    frame.GetProperty("runtimeGeneration").GetUInt64() != _runtimeGeneration)
                {
                    _ready.TrySetException(new InvalidOperationException(
                        "Runtime Host ready frame does not match the requested meeting lease."));
                }
                else
                {
                    _ready.TrySetResult();
                }
                break;
            case "receipt":
                HandleReceipt(frame.GetProperty("receipt"));
                break;
            case "event":
                HandleEvent(frame.GetProperty("event"));
                break;
            case "error":
                var message = frame.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "Runtime Host 报告了未知错误。";
                ReportDiagnostic(message ?? "Runtime Host 报告了未知错误。");
                break;
            case "stopped":
                _stopped.TrySetResult();
                break;
        }
    }

    private void HandleReceipt(JsonElement receipt)
    {
        var commandId = receipt.GetProperty("commandId").GetString();
        if (commandId is null || !_pending.TryGetValue(commandId, out var completion))
        {
            return;
        }
        completion.TrySetResult(new RuntimeCommandReceipt(
            commandId,
            receipt.GetProperty("status").GetString() ?? "rejected",
            receipt.TryGetProperty("sequence", out var sequence) ? sequence.GetUInt64() : null,
            receipt.TryGetProperty("errorCode", out var errorCode) ? errorCode.GetString() : null,
            receipt.TryGetProperty("message", out var message) ? message.GetString() : null));
    }

    private void HandleEvent(JsonElement eventElement)
    {
        var meetingEvent = new RuntimeMeetingEvent(
            eventElement.GetProperty("meetingId").GetString() ?? string.Empty,
            eventElement.GetProperty("eventId").GetString() ?? string.Empty,
            eventElement.GetProperty("sequence").GetUInt64(),
            eventElement.GetProperty("runtimeGeneration").GetUInt64(),
            eventElement.GetProperty("kind").GetString() ?? string.Empty,
            eventElement.GetProperty("occurredAt").GetDateTimeOffset(),
            eventElement.TryGetProperty("actorId", out var actorId) ? actorId.GetString() : null,
            eventElement.TryGetProperty("targetId", out var targetId) ? targetId.GetString() : null,
            eventElement.TryGetProperty("causationId", out var causationId) ? causationId.GetString() : null,
            eventElement.GetProperty("payload").Clone());
        if (
            meetingEvent.MeetingId != _meetingId ||
            meetingEvent.RuntimeGeneration != _runtimeGeneration)
        {
            ReportDiagnostic("Runtime Host 事件不属于当前会议代次，已拒绝。");
            return;
        }
        try
        {
            MeetingEventReceived?.Invoke(this, meetingEvent);
        }
        catch
        {
            ReportDiagnostic("客户端无法投递 Runtime Host 事件。");
        }
    }

    private void OnProcessExited(Process process)
    {
        if (!_ready.Task.IsCompleted)
        {
            int? exitCode = null;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // A concurrent teardown may make the exit code unavailable.
            }
            _ready.TrySetException(new InvalidOperationException(
                exitCode is null
                    ? "Runtime Host exited before ready."
                    : $"Runtime Host exited before ready (code {exitCode})."));
        }
        _stopped.TrySetResult();
        FailPending(new InvalidOperationException("Runtime Host process exited."));
    }

    private void FailPending(Exception error)
    {
        foreach (var commandId in _pending.Keys)
        {
            if (_pending.TryRemove(commandId, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private void ReportDiagnostic(string message)
    {
        try
        {
            DiagnosticReceived?.Invoke(this, message);
        }
        catch
        {
            // Diagnostics cannot destabilize the process supervisor.
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and kill.
        }
    }

    private static string ResolveHostScript()
    {
        var configured = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "packages",
                "runtime-host",
                "dist",
                "host-main.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "runtime-host", "host-main.js");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        throw new FileNotFoundException(
            "找不到 Runtime Host。请先运行 npm run build，或设置 PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT。",
            packaged);
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Shutdown is already best-effort at this point.
        }
    }
}
