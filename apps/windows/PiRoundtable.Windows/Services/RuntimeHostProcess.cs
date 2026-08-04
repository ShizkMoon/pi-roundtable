using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed record RuntimeHostStartOptions(
    string MeetingId,
    string RuntimeId,
    ulong RuntimeGeneration,
    ulong InitialSequence,
    WorkspaceConfiguration Workspace,
    RoundtableSessionConfiguration Session,
    IReadOnlyDictionary<string, string> Credentials,
    DiscussionSchedulerStateConfiguration? DiscussionState = null);

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
    string Visibility,
    IReadOnlyList<string> Audience,
    JsonElement Payload);

internal sealed class RuntimeHostProcess : IRuntimeHostProcess
{
    private sealed record PendingRuntimeCommand(
        string Fingerprint,
        TaskCompletionSource<RuntimeCommandReceipt> Completion);

    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly ConcurrentDictionary<string, PendingRuntimeCommand> _pending = new();
    private readonly IMeetingEventStore? _commandJournal;
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
    private ulong _expectedReadySequence;
    private int _stderrReported;
    private bool _disposed;

    public RuntimeHostProcess(IMeetingEventStore? commandJournal = null)
    {
        _commandJournal = commandJournal;
    }

    public event EventHandler<RuntimeMeetingEvent>? MeetingEventReceived;

    public event EventHandler<string>? DiagnosticReceived;

    public event EventHandler<string>? EventStreamFaulted;

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
        _expectedReadySequence = checked(options.InitialSequence + 1);
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveNodeExecutable(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        // Node 24 does not use HTTP_PROXY/HTTPS_PROXY for fetch unless this opt-in
        // is present. Older development runtimes ignore the environment variable.
        startInfo.Environment["NODE_USE_ENV_PROXY"] = "1";
        startInfo.ArgumentList.Add("--use-env-proxy");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["PI_ROUNDTABLE_MEETING_ID"] = options.MeetingId;
        startInfo.Environment["PI_ROUNDTABLE_RUNTIME_ID"] = options.RuntimeId;
        startInfo.Environment["PI_ROUNDTABLE_RUNTIME_GENERATION"] = options.RuntimeGeneration.ToString();
        startInfo.Environment["PI_ROUNDTABLE_WORKING_DIRECTORY"] = AppContext.BaseDirectory;

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
            initialSequence = options.InitialSequence,
            discussionState = options.DiscussionState,
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
        CancellationToken cancellationToken,
        string? commandId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stdin is null)
        {
            throw new InvalidOperationException("Runtime Host is not ready.");
        }

        commandId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId.Trim();
        var fingerprint = CreateCommandFingerprint(kind, actorId, targetId, payload);
        var completion = new TaskCompletionSource<RuntimeCommandReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRuntimeCommand(fingerprint, completion);
        if (!_pending.TryAdd(commandId, pending))
        {
            var existing = _pending[commandId];
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return CommandConflict(commandId);
            }
            return await existing.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        var journalReserved = false;

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
            if (_commandJournal is not null)
            {
                var reservation = await _commandJournal.ReserveCommandAsync(
                    _meetingId,
                    commandId,
                    fingerprint,
                    cancellationToken);
                if (reservation.Disposition == CommandJournalReservationDisposition.Conflict)
                {
                    return CommandConflict(commandId);
                }
                if (reservation.Disposition == CommandJournalReservationDisposition.Duplicate)
                {
                    return reservation.Receipt ?? new RuntimeCommandReceipt(
                        commandId,
                        "rejected",
                        null,
                        "command_outcome_unknown",
                        "该命令已在先前进程中开始，但没有持久终态；为避免重复副作用，本次不会重放。");
                }
                journalReserved = true;
            }
            await WriteFrameAsync(new Dictionary<string, object?>
            {
                ["type"] = "command",
                ["command"] = command,
            }, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch
        {
            if (journalReserved && _commandJournal is not null)
            {
                try
                {
                    await _commandJournal.MarkCommandInterruptedAsync(
                        _meetingId,
                        commandId,
                        fingerprint,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original process/transport failure. The pending journal row still blocks replay.
                }
            }
            throw;
        }
        finally
        {
            _pending.TryRemove(commandId, out _);
        }
    }

    public async Task StopAsync(RuntimeHostShutdownMode mode, CancellationToken cancellationToken)
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
                mode = mode == RuntimeHostShutdownMode.Close ? "close" : "suspend",
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
            await StopAsync(RuntimeHostShutdownMode.Suspend, timeout.Token);
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
                TraceProtocolFrame($"fault reason=oversize bytes={line.Length}");
                ReportEventStreamFault("Runtime Host 返回了超出限制的数据帧；会议已安全暂停。");
                return;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                await HandleFrameAsync(document.RootElement, cancellationToken);
            }
            catch (JsonException error)
            {
                TraceProtocolFrame(
                    $"fault reason=json length={line.Length} byte={error.BytePositionInLine?.ToString() ?? "unknown"} window={RedactJsonWindow(line, error.BytePositionInLine)}");
                ReportEventStreamFault("Runtime Host 输出了无法解析的数据；会议已安全暂停。");
                return;
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                TraceProtocolFrame($"fault reason=shape error={error.GetType().Name}");
                ReportEventStreamFault("Runtime Host 输出的数据不符合本地协议；会议已安全暂停。");
                return;
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

    private async Task HandleFrameAsync(JsonElement frame, CancellationToken cancellationToken)
    {
        if (!frame.TryGetProperty("type", out var typeElement))
        {
            throw new InvalidOperationException("Runtime Host frame type is missing.");
        }
        var frameType = typeElement.GetString() ?? "unknown";
        if (frameType == "event" && frame.TryGetProperty("event", out var tracedEvent))
        {
            var tracedSequence = tracedEvent.TryGetProperty("sequence", out var sequenceElement)
                ? sequenceElement.GetRawText()
                : "missing";
            var tracedGeneration = tracedEvent.TryGetProperty("runtimeGeneration", out var generationElement)
                ? generationElement.GetRawText()
                : "missing";
            var tracedKind = tracedEvent.TryGetProperty("kind", out var kindElement)
                ? kindElement.GetString() ?? "missing"
                : "missing";
            TraceProtocolFrame($"frame type=event sequence={tracedSequence} generation={tracedGeneration} kind={tracedKind}");
        }
        else
        {
            TraceProtocolFrame($"frame type={frameType}");
        }
        switch (frameType)
        {
            case "ready":
                if (
                    frame.GetProperty("protocolVersion").GetInt32() != 3 ||
                    frame.GetProperty("meetingId").GetString() != _meetingId ||
                    frame.GetProperty("runtimeId").GetString() != _runtimeId ||
                    frame.GetProperty("runtimeGeneration").GetUInt64() != _runtimeGeneration ||
                    frame.GetProperty("sequence").GetUInt64() != _expectedReadySequence)
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
                await HandleReceiptAsync(frame.GetProperty("receipt"), cancellationToken);
                break;
            case "event":
                HandleEvent(frame.GetProperty("event"));
                break;
            case "error":
                var message = frame.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "Runtime Host 报告了未知错误。";
                var errorCode = frame.TryGetProperty("errorCode", out var errorCodeElement)
                    ? errorCodeElement.GetString()
                    : null;
                ReportDiagnostic(string.IsNullOrWhiteSpace(errorCode)
                    ? message ?? "Runtime Host 报告了未知错误。"
                    : $"{message ?? "Runtime Host 报告了错误。"} [{errorCode}]");
                break;
            case "stopped":
                _stopped.TrySetResult();
                break;
            default:
                throw new InvalidOperationException($"Runtime Host frame type is unsupported: {frameType}.");
        }
    }

    private static void TraceProtocolFrame(string message)
    {
        var tracePath = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_EVENT_TRACE_FILE");
        if (string.IsNullOrWhiteSpace(tracePath) || !Path.IsPathFullyQualified(tracePath))
        {
            return;
        }
        try
        {
            File.AppendAllText(
                tracePath,
                $"{DateTimeOffset.UtcNow:O} protocol {message}{Environment.NewLine}");
        }
        catch
        {
            // Opt-in diagnostics cannot affect protocol processing.
        }
    }

    private static string RedactJsonWindow(string line, long? bytePosition)
    {
        var center = bytePosition is null
            ? 0
            : Math.Clamp((int)bytePosition.Value, 0, Math.Max(0, line.Length - 1));
        var start = Math.Max(0, center - 32);
        var length = Math.Min(65, line.Length - start);
        return new string(line.AsSpan(start, length).ToArray().Select(character => character switch
        {
            '{' or '}' or '[' or ']' or ':' or ',' or '"' or '\\' => character,
            _ => 'x',
        }).ToArray());
    }

    private async Task HandleReceiptAsync(JsonElement receipt, CancellationToken cancellationToken)
    {
        var commandId = receipt.GetProperty("commandId").GetString();
        if (commandId is null || !_pending.TryGetValue(commandId, out var pending))
        {
            return;
        }
        var runtimeReceipt = new RuntimeCommandReceipt(
            commandId,
            receipt.GetProperty("status").GetString() ?? "rejected",
            receipt.TryGetProperty("sequence", out var sequence) ? sequence.GetUInt64() : null,
            receipt.TryGetProperty("errorCode", out var errorCode) ? errorCode.GetString() : null,
            receipt.TryGetProperty("message", out var message) ? message.GetString() : null);
        try
        {
            if (_commandJournal is not null)
            {
                await _commandJournal.CompleteCommandAsync(
                    _meetingId,
                    pending.Fingerprint,
                    runtimeReceipt,
                    cancellationToken);
            }
            pending.Completion.TrySetResult(runtimeReceipt);
        }
        catch (Exception error)
        {
            pending.Completion.TrySetException(error);
        }
    }

    private void HandleEvent(JsonElement eventElement)
    {
        var meetingEvent = RuntimeMeetingEventParser.Parse(eventElement);
        if (
            meetingEvent.MeetingId != _meetingId ||
            meetingEvent.RuntimeGeneration != _runtimeGeneration)
        {
            throw new InvalidOperationException("Runtime Host event does not belong to the active meeting generation.");
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
            if (_pending.TryRemove(commandId, out var pending))
            {
                pending.Completion.TrySetException(error);
            }
        }
    }

    internal static string CreateCommandFingerprint(
        string kind,
        string? actorId,
        string? targetId,
        IReadOnlyDictionary<string, object?> payload)
    {
        var value = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["actorId"] = actorId,
            ["targetId"] = targetId,
            ["payload"] = payload,
        }, SerializerOptions);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalJson(writer, value);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("命令载荷包含不支持的 JSON 值。");
        }
    }

    private static RuntimeCommandReceipt CommandConflict(string commandId) => new(
        commandId,
        "rejected",
        null,
        "command_id_conflict",
        "同一命令 ID 不能用于不同命令内容。");

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

    private void ReportEventStreamFault(string message)
    {
        Terminate();
        ReportDiagnostic(message);
        try
        {
            EventStreamFaulted?.Invoke(this, message);
        }
        catch
        {
            // Process termination remains the final safety boundary.
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
        var packaged = Path.Combine(AppContext.BaseDirectory, "runtime-host", "host-main.js");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        // Overrides exist for tests and source checkouts only. A packaged app
        // always prefers its colocated, reviewed runtime even if the parent
        // process inherited untrusted PI_ROUNDTABLE_* environment values.
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

        throw new FileNotFoundException(
            "找不到 Runtime Host。请先运行 npm run build，或设置 PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT。",
            packaged);
    }

    private static string ResolveNodeExecutable()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        var configured = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        return "node";
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
