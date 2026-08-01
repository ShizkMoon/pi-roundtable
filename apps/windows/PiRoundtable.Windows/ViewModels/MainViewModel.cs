using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, TranscriptItem> _streamingMessages = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly WorkspaceConfigurationStore _workspaceStore = new();
    private readonly RoundtableSessionStore _sessionStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly List<RoleItem> _longTermRoles = [];
    private WorkspaceConfiguration _workspace = new();
    private RuntimeHostProcess? _runtime;
    private RuntimeHostProcess? _startingRuntime;
    private MeetingCoreSession? _meetingCore;
    private RoleItem? _selectedRole;
    private SessionItem? _selectedSession;
    private ProviderProfileConfiguration? _selectedProvider;
    private ModelProfileConfiguration? _selectedModel;
    private ModelProfileConfiguration? _selectedRoleModel;
    private ModelProfileConfiguration? _selectedInvitationModel;
    private string _meetingTitle = "新圆桌会议";
    private string _providerDisplayName = "OpenAI";
    private string _runtimeProviderId = "openai";
    private string _apiFamily = "openai_responses";
    private string _providerEndpoint = string.Empty;
    private string _modelDisplayName = string.Empty;
    private string _modelId = string.Empty;
    private string _temporaryRoleName = string.Empty;
    private string _temporaryRolePurpose = string.Empty;
    private string _temporaryRoleSystemPrompt = string.Empty;
    private string _invitationInviterId = "user.direct_host";
    private string _invitationRetentionPolicy = "review_at_close";
    private string _invitationNetworkAccess = "subagent_required";
    private string _statusText = "等待配置";
    private string _errorMessage = string.Empty;
    private ulong _sequence;
    private ulong _runtimeGeneration;
    private bool _isRunning;
    private bool _isBusy;
    private bool _eventStreamFaulted;
    private bool _initialized;
    private bool _disposed;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _longTermRoles.Add(new RoleItem(
            "role.host",
            "主持人",
            "long_term",
            "你是圆桌会议主持人。维护议程、分配发言权、澄清决策，并在需要时邀请临时角色。"));
        _longTermRoles.Add(new RoleItem(
            "role.secretary",
            "秘书",
            "long_term",
            "你是圆桌会议秘书。维护上下文、记录决定与未决问题，只提出可审核的长期记忆和提示词改进建议。"));
        foreach (var role in _longTermRoles)
        {
            Roles.Add(role);
        }
        RefreshInvitationInviters();
        var session = new SessionItem($"session-{Guid.NewGuid():N}", _meetingTitle);
        Sessions.Add(session);
        SelectedSession = session;
        SelectedRole = Roles[0];
    }

    public ObservableCollection<RoleItem> Roles { get; } = [];

    public ObservableCollection<TranscriptItem> Transcript { get; } = [];

    public ObservableCollection<SessionItem> Sessions { get; } = [];

    public ObservableCollection<ProviderProfileConfiguration> Providers { get; } = [];

    public ObservableCollection<ModelProfileConfiguration> Models { get; } = [];

    public ObservableCollection<CapabilityGrantItem> AvailableCapabilities { get; } = [];

    public ObservableCollection<CapabilityGrantItem> InvitationCapabilities { get; } = [];

    public ObservableCollection<SelectionOptionItem> InvitationInviterOptions { get; } = [];

    public IReadOnlyList<string> ApiFamilies { get; } =
        ["openai_responses", "openai_chat_completions", "anthropic_messages", "google_generate_content", "custom"];

    public IReadOnlyList<SelectionOptionItem> RetentionOptions { get; } =
    [
        new("review_at_close", "闭会时审核"),
        new("delete_after_session", "会后删除"),
        new("promote_candidate", "列为长期角色候选"),
    ];

    public IReadOnlyList<SelectionOptionItem> NetworkAccessOptions { get; } =
    [
        new("subagent_required", "必须委派 SubAgent"),
        new("subagent_preferred", "优先委派 SubAgent"),
        new("forbidden", "禁止联网"),
        new("direct_allowed", "允许直接联网"),
    ];

    public string MeetingTitle
    {
        get => _meetingTitle;
        set
        {
            if (SetField(ref _meetingTitle, value) && SelectedSession is not null)
            {
                SelectedSession.Title = value;
                SelectedSession.UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public string ProviderDisplayName
    {
        get => _providerDisplayName;
        set => SetField(ref _providerDisplayName, value);
    }

    public string RuntimeProviderId
    {
        get => _runtimeProviderId;
        set => SetField(ref _runtimeProviderId, value);
    }

    public string ApiFamily
    {
        get => _apiFamily;
        set => SetField(ref _apiFamily, value);
    }

    public string ProviderEndpoint
    {
        get => _providerEndpoint;
        set => SetField(ref _providerEndpoint, value);
    }

    public string ModelDisplayName
    {
        get => _modelDisplayName;
        set => SetField(ref _modelDisplayName, value);
    }

    public string ModelId
    {
        get => _modelId;
        set => SetField(ref _modelId, value);
    }

    public string TemporaryRoleName
    {
        get => _temporaryRoleName;
        set
        {
            if (SetField(ref _temporaryRoleName, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
    }

    public string TemporaryRolePurpose
    {
        get => _temporaryRolePurpose;
        set
        {
            if (SetField(ref _temporaryRolePurpose, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
    }

    public string TemporaryRoleSystemPrompt
    {
        get => _temporaryRoleSystemPrompt;
        set
        {
            if (SetField(ref _temporaryRoleSystemPrompt, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
    }

    public string InvitationRetentionPolicy
    {
        get => _invitationRetentionPolicy;
        set
        {
            if (SetField(ref _invitationRetentionPolicy, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
    }

    public string InvitationInviterId
    {
        get => _invitationInviterId;
        set
        {
            if (SetField(ref _invitationInviterId, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
    }

    public string InvitationNetworkAccess
    {
        get => _invitationNetworkAccess;
        set
        {
            if (SetField(ref _invitationNetworkAccess, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
    }

    public SessionItem? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (IsRunning || value is null || !SetField(ref _selectedSession, value))
            {
                return;
            }
            _meetingTitle = value.Title;
            OnPropertyChanged(nameof(MeetingTitle));
            Roles.Clear();
            foreach (var role in _longTermRoles.Where(role => !role.IsArchived))
            {
                Roles.Add(role);
            }
            foreach (var role in value.TemporaryRoles.Where(role => !role.IsArchived))
            {
                Roles.Add(role);
            }
            SelectedRole = Roles.FirstOrDefault();
        }
    }

    public ProviderProfileConfiguration? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!SetField(ref _selectedProvider, value) || value is null)
            {
                return;
            }
            ProviderDisplayName = value.DisplayName;
            RuntimeProviderId = value.RuntimeProviderId;
            ApiFamily = value.ApiFamily;
            ProviderEndpoint = value.Endpoint ?? string.Empty;
            SelectedModel = Models.FirstOrDefault(model => model.ProviderProfileId == value.ProviderProfileId);
        }
    }

    public ModelProfileConfiguration? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetField(ref _selectedModel, value) || value is null)
            {
                return;
            }
            ModelDisplayName = value.DisplayName;
            ModelId = value.ModelId;
        }
    }

    public ModelProfileConfiguration? SelectedRoleModel
    {
        get => _selectedRoleModel;
        set
        {
            if (SetField(ref _selectedRoleModel, value) && SelectedRole is not null && value is not null)
            {
                SelectedRole.ModelProfileId = value.ModelProfileId;
            }
        }
    }

    public ModelProfileConfiguration? SelectedInvitationModel
    {
        get => _selectedInvitationModel;
        set
        {
            if (SetField(ref _selectedInvitationModel, value))
            {
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
            }
        }
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
                OnPropertyChanged(nameof(IsRoleConfigurationEditable));
                SelectedRoleModel = value is null
                    ? null
                    : Models.FirstOrDefault(model => model.ModelProfileId == value.ModelProfileId)
                        ?? Models.FirstOrDefault();
                RefreshCapabilityGrants();
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
                OnPropertyChanged(nameof(CanInviteTemporaryRole));
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(IsRoleConfigurationEditable));
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
                OnPropertyChanged(nameof(IsRoleConfigurationEditable));
            }
        }
    }

    public bool CanStart => !IsRunning && !IsBusy && Providers.Count > 0 && Models.Count > 0;

    public bool CanOperate => IsRunning && !IsBusy;

    public bool CanAddTemporaryRole => !IsBusy;

    public bool CanInviteTemporaryRole =>
        !IsBusy &&
        SelectedInvitationModel is not null &&
        !string.IsNullOrWhiteSpace(TemporaryRoleName) &&
        !string.IsNullOrWhiteSpace(TemporaryRolePurpose) &&
        !string.IsNullOrWhiteSpace(TemporaryRoleSystemPrompt) &&
        InvitationInviterOptions.Any(option => option.Value == InvitationInviterId) &&
        RetentionOptions.Any(option => option.Value == InvitationRetentionPolicy) &&
        NetworkAccessOptions.Any(option => option.Value == InvitationNetworkAccess);

    public bool IsRoleConfigurationEditable => !IsRunning && !IsBusy;

    public bool CanSend => CanOperate && SelectedRole is { IsArchived: false };

    public bool CanPromoteSelectedRole =>
        !IsBusy && SelectedRole is { Scope: "temporary", IsArchived: false };

    public bool CanArchiveSelectedRole => !IsBusy && SelectedRole is { IsArchived: false };

    public string MeetingSummary => $"{Roles.Count(role => !role.IsArchived)} 个活跃角色 · {StatusText}";

    public string GenerationSummary => _runtimeGeneration == 0
        ? "尚未启动"
        : $"{_runtimeGeneration} / {_sequence}";

    public Visibility TranscriptEmptyVisibility => Transcript.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ClearError()
    {
        ErrorMessage = string.Empty;
    }

    public void ReportClientError(string message)
    {
        ShowError(message);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }
        try
        {
            _workspace = await _workspaceStore.LoadAsync(cancellationToken);
            Providers.Clear();
            Models.Clear();
            foreach (var provider in _workspace.Providers)
            {
                Providers.Add(provider);
            }
            foreach (var model in _workspace.Models)
            {
                Models.Add(model);
            }
            if (_workspace.Roles.Count > 0)
            {
                _longTermRoles.Clear();
                foreach (var profile in _workspace.Roles)
                {
                    var role = new RoleItem(
                        profile.RoleProfileId,
                        profile.DisplayName,
                        "long_term",
                        profile.SystemPrompt,
                        profile.ModelRoute.PrimaryModelProfileId);
                    role.SkillIds.UnionWith(profile.Capabilities.SkillIds);
                    role.McpServerIds.UnionWith(
                        profile.Capabilities.McpGrants.Select(grant => grant.McpServerId));
                    _longTermRoles.Add(role);
                }
                Roles.Clear();
                foreach (var role in _longTermRoles)
                {
                    Roles.Add(role);
                }
                SelectedRole = Roles.FirstOrDefault();
            }
            var defaultModel = Models.FirstOrDefault(model => model.Enabled);
            foreach (var role in _longTermRoles.Where(role => string.IsNullOrWhiteSpace(role.ModelProfileId)))
            {
                role.ModelProfileId = defaultModel?.ModelProfileId ?? string.Empty;
            }
            var persistedSessions = await _sessionStore.LoadAllAsync(cancellationToken);
            var workspaceSessions = persistedSessions
                .Where(session => session.WorkspaceId == _workspace.WorkspaceId)
                .ToArray();
            if (workspaceSessions.Length > 0)
            {
                Sessions.Clear();
                foreach (var definition in workspaceSessions)
                {
                    var session = new SessionItem(definition.SessionId, definition.Title)
                    {
                        Phase = definition.Phase == "live" ? "draft" : definition.Phase,
                        CreatedAt = definition.CreatedAt,
                        UpdatedAt = definition.UpdatedAt,
                    };
                    foreach (var participant in definition.Participants.Where(
                        participant => participant.Scope == "temporary" && participant.Invitation is not null))
                    {
                        var invitation = participant.Invitation!;
                        var role = new RoleItem(
                            participant.ParticipantId,
                            participant.DisplayName,
                            "temporary",
                            participant.SystemPromptSnapshot,
                            participant.ModelRouteSnapshot.PrimaryModelProfileId,
                            invitation.Purpose,
                            invitation.InviterId,
                            participant.RetentionPolicy,
                            participant.DelegationSnapshot.NetworkAccess,
                            invitation.InvitationId,
                            invitation.CreatedAt);
                        role.SkillIds.UnionWith(participant.CapabilitiesSnapshot.SkillIds);
                        role.McpServerIds.UnionWith(
                            participant.CapabilitiesSnapshot.McpGrants.Select(grant => grant.McpServerId));
                        session.TemporaryRoles.Add(role);
                    }
                    Sessions.Add(session);
                }
                SelectedSession = Sessions.FirstOrDefault();
            }
            SelectedProvider = Providers.FirstOrDefault(provider => provider.Enabled);
            SelectedModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedInvitationModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedRoleModel = SelectedRole is null
                ? null
                : Models.FirstOrDefault(model => model.ModelProfileId == SelectedRole.ModelProfileId);
            RefreshInvitationInviters();
            RefreshInvitationCapabilities();
            StatusText = Providers.Count == 0 ? "等待提供商配置" : "配置已加载";
            _initialized = true;
            OnPropertyChanged(nameof(CanStart));
        }
        catch
        {
            ShowError($"无法读取长期配置，请检查 {_workspaceStore.ConfigurationPath}。");
            StatusText = "配置加载失败";
        }
    }

    public void BeginNewProvider()
    {
        SelectedProvider = null;
        SelectedModel = null;
        ProviderDisplayName = string.Empty;
        RuntimeProviderId = string.Empty;
        ApiFamily = "openai_responses";
        ProviderEndpoint = string.Empty;
        ModelDisplayName = string.Empty;
        ModelId = string.Empty;
    }

    public async Task SaveProviderConfigurationAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                ShowError("会议启动或运行期间不能修改提供商配置。");
                return;
            }
            IsBusy = true;
            try
            {
                if (string.IsNullOrWhiteSpace(ProviderDisplayName) ||
                    string.IsNullOrWhiteSpace(RuntimeProviderId) ||
                    string.IsNullOrWhiteSpace(ModelId))
                {
                    ShowError("提供商名称、Runtime Provider ID 和模型 ID 都不能为空。");
                    return;
                }
                if (!TryNormalizeEndpoint(ProviderEndpoint, out var endpoint))
                {
                    ShowError("端点必须使用 HTTPS，或使用本机回环 HTTP，且不能包含用户名或密码。");
                    return;
                }

                var providerProfileId = SelectedProvider?.ProviderProfileId
                    ?? $"provider.{NormalizeId(RuntimeProviderId)}";
                var provider = SelectedProvider
                    ?? Providers.FirstOrDefault(item => item.ProviderProfileId == providerProfileId)
                    ?? new ProviderProfileConfiguration { ProviderProfileId = providerProfileId };
                provider.DisplayName = ProviderDisplayName.Trim();
                provider.ApiFamily = ApiFamily;
                provider.RuntimeProviderId = RuntimeProviderId.Trim();
                provider.Endpoint = endpoint;
                provider.CredentialRef = $"wincred://PiRoundtable/provider/{providerProfileId}";
                provider.Enabled = true;

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    await _credentialStore.SaveAsync(provider.CredentialRef, apiKey, cancellationToken);
                }
                else if (await _credentialStore.ReadAsync(provider.CredentialRef, cancellationToken) is null)
                {
                    ShowError("首次保存该提供商时需要填写 API Key。");
                    return;
                }

                if (!Providers.Contains(provider))
                {
                    Providers.Add(provider);
                }
                var modelProfileId = SelectedModel is not null &&
                    SelectedModel.ProviderProfileId == provider.ProviderProfileId
                        ? SelectedModel.ModelProfileId
                        : $"model.{NormalizeId(provider.RuntimeProviderId)}.{NormalizeId(ModelId)}";
                var model = SelectedModel is not null && SelectedModel.ProviderProfileId == provider.ProviderProfileId
                    ? SelectedModel
                    : Models.FirstOrDefault(item => item.ModelProfileId == modelProfileId)
                        ?? new ModelProfileConfiguration
                        {
                            ModelProfileId = modelProfileId,
                            ProviderProfileId = provider.ProviderProfileId,
                        };
                model.ModelId = ModelId.Trim();
                model.DisplayName = string.IsNullOrWhiteSpace(ModelDisplayName)
                    ? model.ModelId
                    : ModelDisplayName.Trim();
                model.Enabled = true;
                if (!Models.Contains(model))
                {
                    Models.Add(model);
                }
                foreach (var role in _longTermRoles.Where(role => string.IsNullOrWhiteSpace(role.ModelProfileId)))
                {
                    role.ModelProfileId = model.ModelProfileId;
                }
                SelectedProvider = provider;
                SelectedModel = model;
                SelectedInvitationModel ??= model;
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                await PersistSelectedSessionAsync(cancellationToken);
                ErrorMessage = string.Empty;
                StatusText = "长期配置已保存";
                OnPropertyChanged(nameof(CanStart));
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SaveRoleConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                ShowError("会议启动或运行期间不能修改角色配置。");
                return;
            }
            IsBusy = true;
            try
            {
                var role = SelectedRole;
                if (role is null || role.Scope != "long_term")
                {
                    ShowError("请选择一个长期角色保存配置。");
                    return;
                }
                if (string.IsNullOrWhiteSpace(role.SystemPrompt) ||
                    Models.All(model => model.ModelProfileId != role.ModelProfileId))
                {
                    ShowError("长期角色必须拥有完整系统提示词和有效模型路由。");
                    return;
                }
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                await PersistSelectedSessionAsync(cancellationToken);
                StatusText = $"已保存 {role.DisplayName}";
                ErrorMessage = string.Empty;
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            ShowError("请先结束当前会话。");
            return;
        }
        var session = new SessionItem($"session-{Guid.NewGuid():N}", "新圆桌会议");
        Sessions.Insert(0, session);
        SelectedSession = session;
        Transcript.Clear();
        Roles.Clear();
        foreach (var role in _longTermRoles.Where(role => !role.IsArchived))
        {
            Roles.Add(role);
        }
        SelectedRole = Roles.FirstOrDefault();
        StatusText = "新会话草稿";
        NotifySummary();
        await PersistSelectedSessionAsync(cancellationToken);
    }

    public async Task StartMeetingAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                return;
            }
            await InitializeAsync(cancellationToken);
            var activeRoles = Roles.Where(role => !role.IsArchived).ToArray();
            if (activeRoles.Length == 0 || Providers.Count == 0 || Models.Count == 0)
            {
                ShowError("启动会话前需要至少一个参与者、提供商和模型配置。");
                return;
            }
            if (SelectedSession is null || string.IsNullOrWhiteSpace(MeetingTitle))
            {
                ShowError("启动会话前需要填写会议标题。");
                return;
            }
            if (activeRoles.Any(role =>
                    string.IsNullOrWhiteSpace(role.SystemPrompt) ||
                    Models.All(model => model.ModelProfileId != role.ModelProfileId)))
            {
                ShowError("每个参与者都必须拥有完整系统提示词和有效模型路由。");
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
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                var credentials = await ResolveSessionCredentialsAsync(activeRoles, cancellationToken);
                _meetingCore = new MeetingCoreSession();
                var meetingId = SelectedSession?.SessionId ?? $"session-{Guid.NewGuid():N}";
                var sessionDefinition = BuildSessionConfiguration(
                    SelectedSession ?? new SessionItem(meetingId, MeetingTitle),
                    activeRoles,
                    "draft");
                await _sessionStore.SaveAsync(sessionDefinition, cancellationToken);
                var nextGeneration = checked(_runtimeGeneration + 1);
                await runtime.StartAsync(
                    new RuntimeHostStartOptions(
                        meetingId,
                        $"runtime-windows-{Environment.ProcessId}",
                        nextGeneration,
                        _workspace,
                        sessionDefinition,
                        credentials),
                    cancellationToken);
                if (_disposed)
                {
                    runtime.MeetingEventReceived -= OnMeetingEventReceived;
                    runtime.DiagnosticReceived -= OnDiagnosticReceived;
                    _startingRuntime = null;
                    await runtime.DisposeAsync();
                    _meetingCore?.Dispose();
                    _meetingCore = null;
                    return;
                }
                _runtime = runtime;
                _startingRuntime = null;
                _runtimeGeneration = nextGeneration;
                _sequence = 0;
                _eventStreamFaulted = false;

                foreach (var role in activeRoles)
                {
                    await EnsureAcceptedAsync(
                        runtime.SendCommandAsync(
                            role.Scope == "long_term" ? "role.add" : "role.create_temporary",
                            role.RoleId,
                            null,
                            EmptyPayload,
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
                if (SelectedSession is not null)
                {
                    SelectedSession.Phase = "live";
                    SelectedSession.UpdatedAt = DateTimeOffset.Now;
                    await PersistSelectedSessionAsync(cancellationToken);
                }
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
                ShowError("启动失败：请检查 Runtime Host、角色模型路由和 Credential Manager 中的提供商凭据。");
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

    public async Task AddTemporaryRoleAsync(
        string displayName,
        string purpose,
        string systemPrompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(purpose) ||
            string.IsNullOrWhiteSpace(systemPrompt))
        {
            ShowError("临时角色需要名称、邀请目的和完整系统提示词。");
            return;
        }
        var model = SelectedInvitationModel ?? Models.FirstOrDefault(item => item.Enabled);
        if (model is null)
        {
            ShowError("创建临时角色前需要选择可用模型。");
            return;
        }
        var role = new RoleItem(
            $"role.temp.{Guid.NewGuid():N}",
            displayName.Trim(),
            "temporary",
            systemPrompt.Trim(),
            model.ModelProfileId,
            purpose.Trim(),
            InvitationInviterId,
            InvitationRetentionPolicy,
            InvitationNetworkAccess);
        role.SkillIds.UnionWith(
            InvitationCapabilities.Where(grant => grant.Kind == "Skill" && grant.IsGranted)
                .Select(grant => grant.CapabilityId));
        role.McpServerIds.UnionWith(
            InvitationCapabilities.Where(grant => grant.Kind == "MCP" && grant.IsGranted)
                .Select(grant => grant.CapabilityId));
        Roles.Add(role);
        SelectedSession?.TemporaryRoles.Add(role);
        SelectedRole = role;
        if (_runtime is not null && IsRunning)
        {
            var receipt = await _runtime.SendCommandAsync(
                "role.create_temporary",
                role.RoleId,
                null,
                new Dictionary<string, object?>
                {
                    ["participantManifest"] = BuildParticipantManifest(role),
                },
                cancellationToken);
            if (!receipt.Accepted)
            {
                Roles.Remove(role);
                SelectedSession?.TemporaryRoles.Remove(role);
                ShowReceiptError(receipt);
                return;
            }
        }
        NotifySummary();
        await PersistSelectedSessionAsync(cancellationToken);
        foreach (var grant in InvitationCapabilities)
        {
            grant.IsGranted = false;
        }
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
            role.RetentionPolicy = "retain_profile";
            role.Status = "已预配置为长期角色";
            if (!_longTermRoles.Contains(role))
            {
                _longTermRoles.Add(role);
            }
            RefreshInvitationInviters();
            SelectedSession?.TemporaryRoles.Remove(role);
            OnPropertyChanged(nameof(CanPromoteSelectedRole));
            SynchronizeWorkspaceConfiguration();
            await _workspaceStore.SaveAsync(_workspace, cancellationToken);
            await PersistSelectedSessionAsync(cancellationToken);
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
            return;
        }
        role.Scope = "long_term";
        role.RetentionPolicy = "retain_profile";
        if (!_longTermRoles.Contains(role))
        {
            _longTermRoles.Add(role);
        }
        RefreshInvitationInviters();
        SelectedSession?.TemporaryRoles.Remove(role);
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        await PersistSelectedSessionAsync(cancellationToken);
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
        if (role.Scope == "long_term")
        {
            RefreshInvitationInviters();
            SynchronizeWorkspaceConfiguration();
            await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        }
        await PersistSelectedSessionAsync(cancellationToken);
    }

    public async Task CloseMeetingAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var runtime = _runtime;
            if (runtime is null)
            {
                var startingRuntime = _startingRuntime;
                _startingRuntime = null;
                if (startingRuntime is not null)
                {
                    startingRuntime.MeetingEventReceived -= OnMeetingEventReceived;
                    startingRuntime.DiagnosticReceived -= OnDiagnosticReceived;
                    startingRuntime.Terminate();
                    await startingRuntime.DisposeAsync();
                }
                _meetingCore?.Dispose();
                _meetingCore = null;
                await PersistSelectedSessionAsync(CancellationToken.None);
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
                    if (SelectedSession is not null)
                    {
                        SelectedSession.Phase = "closed";
                        SelectedSession.UpdatedAt = DateTimeOffset.Now;
                    }
                    foreach (var role in Roles.Where(role => !role.IsArchived))
                    {
                        role.Status = "未连接";
                    }
                    NotifySummary();
                    await PersistSelectedSessionAsync(CancellationToken.None);
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
        await PersistSelectedSessionAsync(CancellationToken.None);
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
                    role.RetentionPolicy = "retain_profile";
                    if (!_longTermRoles.Contains(role))
                    {
                        _longTermRoles.Add(role);
                    }
                    SelectedSession?.TemporaryRoles.Remove(role);
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

    private void RefreshCapabilityGrants()
    {
        AvailableCapabilities.Clear();
        var role = SelectedRole;
        if (role is null)
        {
            return;
        }
        foreach (var skill in _workspace.Skills.Where(item => item.Enabled))
        {
            AddCapabilityGrant(new CapabilityGrantItem(
                skill.SkillId,
                skill.DisplayName,
                "Skill",
                role.SkillIds.Contains(skill.SkillId)));
        }
        foreach (var server in _workspace.McpServers.Where(item => item.Enabled))
        {
            AddCapabilityGrant(new CapabilityGrantItem(
                server.McpServerId,
                server.DisplayName,
                "MCP",
                role.McpServerIds.Contains(server.McpServerId)));
        }
    }

    private void RefreshInvitationCapabilities()
    {
        InvitationCapabilities.Clear();
        foreach (var skill in _workspace.Skills.Where(item => item.Enabled))
        {
            InvitationCapabilities.Add(new CapabilityGrantItem(
                skill.SkillId,
                skill.DisplayName,
                "Skill",
                false));
        }
        foreach (var server in _workspace.McpServers.Where(item => item.Enabled))
        {
            InvitationCapabilities.Add(new CapabilityGrantItem(
                server.McpServerId,
                server.DisplayName,
                "MCP",
                false));
        }
    }

    private void RefreshInvitationInviters()
    {
        InvitationInviterOptions.Clear();
        InvitationInviterOptions.Add(new SelectionOptionItem("user.direct_host", "我（会议主持）"));
        foreach (var role in _longTermRoles.Where(role => !role.IsArchived))
        {
            InvitationInviterOptions.Add(new SelectionOptionItem(role.RoleId, role.DisplayName));
        }
        if (InvitationInviterOptions.All(option => option.Value != InvitationInviterId))
        {
            InvitationInviterId = "user.direct_host";
        }
        OnPropertyChanged(nameof(CanInviteTemporaryRole));
    }

    private void AddCapabilityGrant(CapabilityGrantItem grant)
    {
        grant.PropertyChanged += OnCapabilityGrantChanged;
        AvailableCapabilities.Add(grant);
    }

    private void OnCapabilityGrantChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(CapabilityGrantItem.IsGranted) ||
            sender is not CapabilityGrantItem grant ||
            SelectedRole is not { } role)
        {
            return;
        }
        var target = grant.Kind == "Skill" ? role.SkillIds : role.McpServerIds;
        if (grant.IsGranted)
        {
            target.Add(grant.CapabilityId);
        }
        else
        {
            target.Remove(grant.CapabilityId);
        }
        role.NotifyCapabilitiesChanged();
    }

    private void SynchronizeWorkspaceConfiguration()
    {
        _workspace.Providers = Providers.ToList();
        _workspace.Models = Models.ToList();
        _workspace.Roles = _longTermRoles
            .Where(role => !role.IsArchived)
            .Select(role => new RoleProfileConfiguration
            {
                RoleProfileId = role.RoleId,
                DisplayName = role.DisplayName,
                Description = $"Pi Roundtable 长期角色：{role.DisplayName}",
                SystemPrompt = role.SystemPrompt,
                Responsibilities = [$"承担 {role.DisplayName} 的会议职责"],
                AutoJoin = true,
                ModelRoute = new ModelRouteConfiguration
                {
                    PrimaryModelProfileId = role.ModelProfileId,
                    FallbackModelProfileIds = [],
                    ThinkingLevel = "medium",
                },
                Capabilities = new CapabilityPolicyConfiguration
                {
                    SkillIds = role.SkillIds.Order(StringComparer.Ordinal).ToList(),
                    McpGrants = role.McpServerIds
                        .Order(StringComparer.Ordinal)
                        .Select(id => new McpGrantConfiguration
                        {
                            McpServerId = id,
                            ToolAllowlist = [],
                            ApprovalMode = "always",
                            ExecutionMode = "subagent_preferred",
                        })
                        .ToList(),
                    ToolGrants = [],
                },
                Delegation = new DelegationPolicyConfiguration
                {
                    NetworkAccess = "subagent_required",
                    ResultMode = "summary_with_citations",
                    MaxConcurrentSubagents = 2,
                },
                Memory = new MemoryPolicyConfiguration
                {
                    Mode = "selective",
                    WriteApproval = "meeting_close",
                    PromptEvolution = "review_required",
                },
            })
            .ToList();
        _workspace.Defaults = Models.FirstOrDefault(model => model.Enabled) is { } defaultModel
            ? new WorkspaceDefaultsConfiguration
            {
                ModelRoute = new ModelRouteConfiguration
                {
                    PrimaryModelProfileId = defaultModel.ModelProfileId,
                    FallbackModelProfileIds = [],
                    ThinkingLevel = "medium",
                },
                Delegation = new DelegationPolicyConfiguration(),
            }
            : null;
    }

    private async Task<Dictionary<string, string>> ResolveSessionCredentialsAsync(
        IEnumerable<RoleItem> roles,
        CancellationToken cancellationToken)
    {
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            var model = Models.First(item => item.ModelProfileId == role.ModelProfileId);
            var provider = Providers.First(item => item.ProviderProfileId == model.ProviderProfileId);
            if (credentials.ContainsKey(provider.CredentialRef))
            {
                continue;
            }
            var secret = await _credentialStore.ReadAsync(provider.CredentialRef, cancellationToken);
            if (string.IsNullOrEmpty(secret))
            {
                throw new InvalidOperationException($"提供商 {provider.DisplayName} 缺少凭据。");
            }
            credentials.Add(provider.CredentialRef, secret);
        }
        return credentials;
    }

    private RoundtableSessionConfiguration BuildSessionConfiguration(
        SessionItem session,
        IEnumerable<RoleItem> roles,
        string? phaseOverride = null)
    {
        return new RoundtableSessionConfiguration
        {
            SessionId = session.SessionId,
            WorkspaceId = _workspace.WorkspaceId,
            Title = string.IsNullOrWhiteSpace(session.Title) ? "未命名圆桌会议" : session.Title.Trim(),
            Phase = phaseOverride ?? session.Phase,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            Agenda = new SessionAgendaConfiguration
            {
                Subject = string.IsNullOrWhiteSpace(session.Title) ? "待确定议题" : session.Title.Trim(),
                Objectives = [],
                Constraints = [],
            },
            Participants = roles
                .Where(role => !role.IsArchived)
                .Select(BuildParticipantManifest)
                .ToList(),
        };
    }

    private async Task PersistSelectedSessionAsync(CancellationToken cancellationToken)
    {
        var session = SelectedSession;
        if (session is null)
        {
            return;
        }
        var participants = Roles
            .Where(role =>
                !role.IsArchived &&
                !string.IsNullOrWhiteSpace(role.SystemPrompt) &&
                Models.Any(model => model.ModelProfileId == role.ModelProfileId))
            .ToArray();
        var definition = BuildSessionConfiguration(session, participants);
        await _sessionStore.SaveAsync(definition, cancellationToken);
        session.UpdatedAt = definition.UpdatedAt;
    }

    private static ParticipantManifestConfiguration BuildParticipantManifest(RoleItem role)
    {
        var isLongTerm = role.Scope == "long_term";
        var invitedAt = role.InvitedAt ?? DateTimeOffset.UtcNow;
        var inviterId = role.InviterId ?? "user.direct_host";
        return new ParticipantManifestConfiguration
        {
            ParticipantId = role.RoleId,
            Scope = role.Scope,
            RoleProfileId = isLongTerm ? role.RoleId : null,
            DisplayName = role.DisplayName,
            SystemPromptSnapshot = role.SystemPrompt,
            ModelRouteSnapshot = new ModelRouteConfiguration
            {
                PrimaryModelProfileId = role.ModelProfileId,
                FallbackModelProfileIds = [],
                ThinkingLevel = "medium",
            },
            CapabilitiesSnapshot = new CapabilityPolicyConfiguration
            {
                SkillIds = role.SkillIds.Order(StringComparer.Ordinal).ToList(),
                McpGrants = role.McpServerIds
                    .Order(StringComparer.Ordinal)
                    .Select(id => new McpGrantConfiguration
                    {
                        McpServerId = id,
                        ToolAllowlist = [],
                        ApprovalMode = "always",
                        ExecutionMode = "subagent_preferred",
                    })
                    .ToList(),
                ToolGrants = [],
            },
            DelegationSnapshot = new DelegationPolicyConfiguration
            {
                NetworkAccess = role.NetworkAccess,
                ResultMode = "summary_with_citations",
                MaxConcurrentSubagents = role.NetworkAccess == "forbidden" ? 0 : 2,
            },
            MemoryPolicySnapshot = new MemoryPolicyConfiguration
            {
                Mode = isLongTerm ? "selective" : "disabled",
                WriteApproval = isLongTerm ? "meeting_close" : "always",
                PromptEvolution = isLongTerm ? "review_required" : "disabled",
            },
            RetentionPolicy = role.RetentionPolicy,
            Invitation = isLongTerm
                ? null
                : new TemporaryRoleInvitationConfiguration
                {
                    InvitationId = role.InvitationId ?? $"invite.{Guid.NewGuid():N}",
                    InviterType = inviterId.StartsWith("user.", StringComparison.Ordinal)
                    ? "user"
                    : "role",
                    InviterId = inviterId,
                    Purpose = role.InvitationPurpose ?? "本次会话的临时职责",
                    Status = "accepted",
                    CreatedAt = invitedAt,
                    AcceptedAt = invitedAt,
                },
        };
    }

    private static bool TryNormalizeEndpoint(string value, out string? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return false;
        }
        endpoint = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    private static string NormalizeId(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-'
                ? character
                : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrEmpty(normalized) ? "item" : normalized[..Math.Min(normalized.Length, 96)];
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
        OnPropertyChanged(nameof(TranscriptEmptyVisibility));
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
