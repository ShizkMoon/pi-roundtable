using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, TranscriptItem> _streamingMessages = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private RuntimeHostProcess? _runtime;
    private RuntimeHostProcess? _startingRuntime;
    private MeetingCoreSession? _meetingCore;
    private RoleItem? _selectedRole;
    private string _meetingTitle = "新圆桌会议";
    private string _providerId = "openai";
    private string _modelId = string.Empty;
    private string _statusText = "等待配置";
    private string _errorMessage = string.Empty;
    private ulong _sequence;
    private ulong _runtimeGeneration = 1;
    private bool _isRunning;
    private bool _isBusy;
    private bool _eventStreamFaulted;
    private bool _disposed;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Roles.Add(new RoleItem("role.host", "主持人", "long_term"));
        Roles.Add(new RoleItem("role.analyst", "分析者", "long_term"));
        SelectedRole = Roles[0];
    }

    public ObservableCollection<RoleItem> Roles { get; } = [];

    public ObservableCollection<TranscriptItem> Transcript { get; } = [];

    public string MeetingTitle
    {
        get => _meetingTitle;
        set => SetField(ref _meetingTitle, value);
    }

    public string ProviderId
    {
        get => _providerId;
        set => SetField(ref _providerId, value);
    }

    public string ModelId
    {
        get => _modelId;
        set => SetField(ref _modelId, value);
    }

    public RoleItem? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (SetField(ref _selectedRole, value))
            {
                OnPropertyChanged(nameof(CanPromoteSelectedRole));
                OnPropertyChanged(nameof(CanArchiveSelectedRole));
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanOperate));
                OnPropertyChanged(nameof(CanAddTemporaryRole));
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool IsNotRunning => !IsRunning;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanOperate));
                OnPropertyChanged(nameof(CanAddTemporaryRole));
                OnPropertyChanged(nameof(CanPromoteSelectedRole));
                OnPropertyChanged(nameof(CanArchiveSelectedRole));
            }
        }
    }

    public bool CanStart => !IsRunning && !IsBusy;

    public bool CanOperate => IsRunning && !IsBusy;

    public bool CanAddTemporaryRole => !IsBusy;

    public bool CanSend => CanOperate && SelectedRole is { IsArchived: false };

    public bool CanPromoteSelectedRole =>
        !IsBusy && SelectedRole is { Scope: "temporary", IsArchived: false };

    public bool CanArchiveSelectedRole => !IsBusy && SelectedRole is { IsArchived: false };

    public string MeetingSummary => $"{Roles.Count(role => !role.IsArchived)} 个活跃角色 · {StatusText}";

    public string GenerationSummary => $"{_runtimeGeneration} / {_sequence}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ClearError()
    {
        ErrorMessage = string.Empty;
    }

    public void ReportClientError(string message)
    {
        ShowError(message);
    }

    public async Task StartMeetingAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(ModelId))
            {
                ShowError("请填写提供商 ID 和模型 ID。");
                return;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ShowError("请输入提供商 API Key；本里程碑只在当前 Runtime Host 进程内使用它。");
                return;
            }

            IsBusy = true;
            ErrorMessage = string.Empty;
            StatusText = "正在启动 Runtime Host";
            var runtime = new RuntimeHostProcess();
            _startingRuntime = runtime;
            runtime.MeetingEventReceived += OnMeetingEventReceived;
            runtime.DiagnosticReceived += OnDiagnosticReceived;
            try
            {
                _meetingCore = new MeetingCoreSession();
                var meetingId = $"meeting-{Guid.NewGuid():N}";
                await runtime.StartAsync(
                    new RuntimeHostStartOptions(
                        meetingId,
                        $"runtime-windows-{Environment.ProcessId}",
                        1,
                        ProviderId.Trim(),
                        ModelId.Trim(),
                        apiKey),
                    cancellationToken);
                _runtime = runtime;
                _startingRuntime = null;
                _runtimeGeneration = 1;
                _sequence = 0;
                _eventStreamFaulted = false;

                foreach (var role in Roles.Where(role => !role.IsArchived))
                {
                    await EnsureAcceptedAsync(
                        runtime.SendCommandAsync(
                            role.Scope == "long_term" ? "role.add" : "role.create_temporary",
                            role.RoleId,
                            null,
                            new Dictionary<string, object?> { ["displayName"] = role.DisplayName },
                            cancellationToken));
                }
                await EnsureAcceptedAsync(runtime.SendCommandAsync(
                    "meeting.open",
                    null,
                    null,
                    EmptyPayload,
                    cancellationToken));
                IsRunning = true;
                StatusText = "本地会议运行中";
            }
            catch
            {
                runtime.MeetingEventReceived -= OnMeetingEventReceived;
                runtime.DiagnosticReceived -= OnDiagnosticReceived;
                await runtime.DisposeAsync();
                _runtime = null;
                _startingRuntime = null;
                _meetingCore?.Dispose();
                _meetingCore = null;
                ShowError("启动失败：请检查 Runtime Host、提供商 ID、模型 ID 与 API Key。");
                StatusText = "启动失败";
            }
            finally
            {
                IsBusy = false;
                NotifySummary();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> SendPromptAsync(string message, CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        var role = SelectedRole;
        if (runtime is null || !IsRunning || role is null || role.IsArchived)
        {
            ShowError("请先启动会议并选择一个活跃角色。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowError("请输入要交给角色的议题或约束。");
            return false;
        }

        ErrorMessage = string.Empty;
        var receipt = await runtime.SendCommandAsync(
            "speech.prompt",
            role.RoleId,
            null,
            new Dictionary<string, object?> { ["message"] = message.Trim() },
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
            return false;
        }
        Transcript.Add(new TranscriptItem(
            "user",
            $"你 → {role.DisplayName}",
            message.Trim(),
            "已提交"));
        role.Status = "等待回应";
        NotifySummary();
        return true;
    }

    public async Task<bool> InterruptAsync(string message, CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        var interruptor = SelectedRole;
        var target = Roles.FirstOrDefault(role => role.Status == "发言中");
        if (runtime is null || !IsRunning || interruptor is null || target is null)
        {
            ShowError("当前没有可打断的发言。");
            return false;
        }
        if (interruptor.RoleId == target.RoleId)
        {
            ShowError("请选择另一个角色发起打断。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowError("打断时需要给新发言者一条明确指令。");
            return false;
        }

        var receipt = await runtime.SendCommandAsync(
            "speech.interrupt",
            interruptor.RoleId,
            target.RoleId,
            new Dictionary<string, object?> { ["message"] = message.Trim() },
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
            return false;
        }
        StatusText = $"{interruptor.DisplayName} 正在接管发言";
        NotifySummary();
        return true;
    }

    public async Task CancelActiveGenerationAsync(CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        var target = Roles.FirstOrDefault(role => role.Status is "发言中" or "等待回应");
        if (runtime is null || target is null)
        {
            ShowError("当前没有正在生成的角色。");
            return;
        }
        var receipt = await runtime.SendCommandAsync(
            "generation.cancel",
            null,
            target.RoleId,
            EmptyPayload,
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
        }
    }

    public async Task AddTemporaryRoleAsync(string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ShowError("请输入临时角色名称。");
            return;
        }
        var role = new RoleItem($"role.temp.{Guid.NewGuid():N}", displayName.Trim(), "temporary");
        Roles.Add(role);
        SelectedRole = role;
        if (_runtime is not null && IsRunning)
        {
            var receipt = await _runtime.SendCommandAsync(
                "role.create_temporary",
                role.RoleId,
                null,
                new Dictionary<string, object?> { ["displayName"] = role.DisplayName },
                cancellationToken);
            if (!receipt.Accepted)
            {
                Roles.Remove(role);
                ShowReceiptError(receipt);
            }
        }
        NotifySummary();
    }

    public async Task PromoteSelectedRoleAsync(CancellationToken cancellationToken = default)
    {
        var role = SelectedRole;
        if (role is null || role.Scope != "temporary" || role.IsArchived)
        {
            ShowError("请选择一个仍在会议中的临时角色。");
            return;
        }
        if (_runtime is null || !IsRunning)
        {
            role.Scope = "long_term";
            role.Status = "已预配置为长期角色";
            OnPropertyChanged(nameof(CanPromoteSelectedRole));
            return;
        }
        var receipt = await _runtime.SendCommandAsync(
            "role.promote",
            role.RoleId,
            null,
            EmptyPayload,
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
        }
    }

    public async Task ArchiveSelectedRoleAsync(CancellationToken cancellationToken = default)
    {
        var role = SelectedRole;
        if (role is null || role.IsArchived)
        {
            return;
        }
        if (_runtime is not null && IsRunning)
        {
            var receipt = await _runtime.SendCommandAsync(
                "role.archive",
                role.RoleId,
                null,
                EmptyPayload,
                cancellationToken);
            if (!receipt.Accepted)
            {
                ShowReceiptError(receipt);
                return;
            }
        }
        else
        {
            role.IsArchived = true;
            role.Status = "已归档";
        }
        OnPropertyChanged(nameof(CanArchiveSelectedRole));
        NotifySummary();
    }

    public async Task CloseMeetingAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var runtime = _runtime;
            if (runtime is null)
            {
                return;
            }
            _runtime = null;
            var wasRunning = IsRunning;
            IsRunning = false;
            IsBusy = true;
            try
            {
                if (wasRunning)
                {
                    var receipt = await runtime.SendCommandAsync(
                        "meeting.close",
                        null,
                        null,
                        EmptyPayload,
                        cancellationToken);
                    if (!receipt.Accepted)
                    {
                        ShowReceiptError(receipt);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                ShowError($"结束会议时出现问题：{error.Message}");
            }
            finally
            {
                runtime.MeetingEventReceived -= OnMeetingEventReceived;
                runtime.DiagnosticReceived -= OnDiagnosticReceived;
                try
                {
                    await runtime.DisposeAsync();
                }
                finally
                {
                    _meetingCore?.Dispose();
                    _meetingCore = null;
                    _streamingMessages.Clear();
                    IsBusy = false;
                    StatusText = "会议已结束";
                    foreach (var role in Roles.Where(role => !role.IsArchived))
                    {
                        role.Status = "未连接";
                    }
                    NotifySummary();
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await CloseMeetingAsync();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _disposed = true;
        await CloseMeetingAsync(cancellationToken);
    }

    public void TerminateRuntimeForAppExit()
    {
        _runtime?.Terminate();
        _startingRuntime?.Terminate();
    }

    private void OnMeetingEventReceived(object? sender, RuntimeMeetingEvent meetingEvent)
    {
        _dispatcher.TryEnqueue(() => ApplyMeetingEvent(meetingEvent));
    }

    private void OnDiagnosticReceived(object? sender, string message)
    {
        _dispatcher.TryEnqueue(() => ShowError(message));
    }

    private void ApplyMeetingEvent(RuntimeMeetingEvent meetingEvent)
    {
        if (meetingEvent.RuntimeGeneration != _runtimeGeneration || meetingEvent.Sequence <= _sequence)
        {
            return;
        }
        if (_eventStreamFaulted)
        {
            return;
        }
        if (meetingEvent.Sequence != _sequence + 1)
        {
            _eventStreamFaulted = true;
            StatusText = "事件流已中断";
            ShowError($"Runtime Host 事件序号不连续：期待 {_sequence + 1}，收到 {meetingEvent.Sequence}。会议将安全关闭。");
            _ = CloseAfterStreamFaultAsync();
            return;
        }
        try
        {
            _meetingCore?.Apply(meetingEvent);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            return;
        }
        _sequence = meetingEvent.Sequence;
        var role = meetingEvent.ActorId is null
            ? null
            : Roles.FirstOrDefault(item => item.RoleId == meetingEvent.ActorId);

        switch (meetingEvent.Kind)
        {
            case "runtime.lease_acquired":
                StatusText = "Runtime Owner 已就绪";
                break;
            case "meeting.opened":
                StatusText = "本地会议运行中";
                break;
            case "meeting.closed":
                StatusText = "会议已结束";
                break;
            case "role.registered":
            case "role.temporary_registered":
                if (role is not null)
                {
                    role.Status = "空闲";
                }
                break;
            case "role.promoted":
                if (role is not null)
                {
                    role.Scope = "long_term";
                    OnPropertyChanged(nameof(CanPromoteSelectedRole));
                }
                break;
            case "role.archived":
                if (role is not null)
                {
                    role.IsArchived = true;
                    role.Status = "已归档";
                    OnPropertyChanged(nameof(CanArchiveSelectedRole));
                }
                break;
            case "speech.started":
                if (role is not null)
                {
                    foreach (var item in Roles.Where(item => item.Status == "发言中"))
                    {
                        item.Status = "空闲";
                    }
                    role.Status = "发言中";
                    var transcript = new TranscriptItem(role.RoleId, role.DisplayName, string.Empty, "生成中");
                    Transcript.Add(transcript);
                    _streamingMessages[role.RoleId] = transcript;
                }
                break;
            case "speech.delta":
                if (role is not null &&
                    _streamingMessages.TryGetValue(role.RoleId, out var streaming) &&
                    meetingEvent.Payload.TryGetProperty("delta", out var delta))
                {
                    streaming.Text += delta.GetString() ?? string.Empty;
                }
                break;
            case "speech.completed":
            case "speech.cancelled":
                var finishedRole = meetingEvent.Kind == "speech.cancelled" && meetingEvent.TargetId is not null
                    ? Roles.FirstOrDefault(item => item.RoleId == meetingEvent.TargetId)
                    : role;
                if (finishedRole is not null)
                {
                    finishedRole.Status = "空闲";
                    if (_streamingMessages.Remove(finishedRole.RoleId, out var finished))
                    {
                        finished.State = meetingEvent.Kind == "speech.completed" ? "已完成" : "已取消";
                    }
                }
                break;
            case "interruption.requested":
                var interruptorName = role?.DisplayName ?? meetingEvent.ActorId ?? "未知角色";
                var targetName = Roles.FirstOrDefault(item => item.RoleId == meetingEvent.TargetId)?.DisplayName
                    ?? meetingEvent.TargetId
                    ?? "当前发言者";
                Transcript.Add(new TranscriptItem(
                    "system",
                    "会议控制",
                    $"{interruptorName} 请求打断 {targetName}。",
                    "处理中"));
                break;
        }
        NotifySummary();
    }

    private static async Task EnsureAcceptedAsync(Task<RuntimeCommandReceipt> receiptTask)
    {
        var receipt = await receiptTask;
        if (!receipt.Accepted)
        {
            throw new InvalidOperationException(receipt.Message ?? receipt.ErrorCode ?? "Runtime Host rejected a command.");
        }
    }

    private async Task CloseAfterStreamFaultAsync()
    {
        try
        {
            await CloseMeetingAsync();
        }
        catch
        {
            // RuntimeHostProcess.DisposeAsync owns last-resort process termination.
        }
    }

    private void ShowReceiptError(RuntimeCommandReceipt receipt)
    {
        ShowError(receipt.Message ?? $"命令被拒绝：{receipt.ErrorCode ?? "unknown"}");
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(MeetingSummary));
        OnPropertyChanged(nameof(GenerationSummary));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanArchiveSelectedRole));
        OnPropertyChanged(nameof(CanPromoteSelectedRole));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static IReadOnlyDictionary<string, object?> EmptyPayload { get; } =
        new Dictionary<string, object?>();
}
