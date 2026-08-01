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
    private readonly Dictionary<string, TranscriptItem> _privateStreamingMessages = [];
    private readonly ObservableCollection<TranscriptItem> _emptyTranscript = [];
    private readonly ObservableCollection<TranscriptItem> _emptyPrivateThread = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly WorkspaceConfigurationStore _workspaceStore = new();
    private readonly RoundtableSessionStore _sessionStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly ClientSettingsStore _clientSettingsStore = new();
    private WorkspaceConfiguration _workspace = new();
    private ClientSettingsConfiguration _clientSettings = new();
    private RuntimeHostProcess? _runtime;
    private RuntimeHostProcess? _startingRuntime;
    private MeetingCoreSession? _meetingCore;
    private RoleItem? _selectedRole;
    private RoleItem? _selectedPrivateRole;
    private SessionItem? _selectedSession;
    private SessionGroupItem? _selectedSessionGroup;
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
    private string _skillDisplayName = string.Empty;
    private string _skillDescription = string.Empty;
    private string _skillSourceLocator = string.Empty;
    private string _mcpDisplayName = string.Empty;
    private string _mcpTransport = "stdio";
    private string _mcpCommandOrEndpoint = string.Empty;
    private string _themeMode = "system";
    private string _remoteSyncEndpoint = string.Empty;
    private bool _remoteSyncEnabled;
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
        LongTermRoles.Add(new RoleItem(
            "role.host",
            "主持人",
            "long_term",
            "你是圆桌会议主持人。维护议程、分配发言权、澄清决策，并在需要时邀请临时角色。"));
        LongTermRoles.Add(new RoleItem(
            "role.secretary",
            "秘书",
            "long_term",
            "你是圆桌会议秘书。维护上下文、记录决定与未决问题，只提出可审核的长期记忆和提示词改进建议。"));
        foreach (var role in LongTermRoles)
        {
            Roles.Add(role);
        }
        RefreshInvitationInviters();
        var defaultGroup = new SessionGroupItem("group.general", "常规会话", "folder");
        SessionGroups.Add(defaultGroup);
        SelectedSessionGroup = defaultGroup;
        var session = new SessionItem($"session-{Guid.NewGuid():N}", _meetingTitle)
        {
            GroupId = defaultGroup.GroupId,
        };
        Sessions.Add(session);
        RefreshVisibleSessions();
        SelectedSession = session;
        SelectedRole = Roles[0];
    }

    public ObservableCollection<RoleItem> Roles { get; } = [];

    public ObservableCollection<TranscriptItem> Transcript => SelectedSession?.Transcript ?? _emptyTranscript;

    public ObservableCollection<TranscriptItem> PrivateMessages =>
        SelectedSession is not null && SelectedPrivateRole is not null
            ? SelectedSession.GetPrivateThread(SelectedPrivateRole.RoleId)
            : _emptyPrivateThread;

    public ObservableCollection<SessionItem> Sessions { get; } = [];

    public ObservableCollection<SessionItem> VisibleSessions { get; } = [];

    public ObservableCollection<SessionGroupItem> SessionGroups { get; } = [];

    public ObservableCollection<RoleItem> LongTermRoles { get; } = [];

    public ObservableCollection<MentionTargetItem> MentionTargets { get; } = [];

    public ObservableCollection<ProviderProfileConfiguration> Providers { get; } = [];

    public ObservableCollection<ModelProfileConfiguration> Models { get; } = [];

    public ObservableCollection<SkillProfileConfiguration> Skills { get; } = [];

    public ObservableCollection<McpServerProfileConfiguration> McpServers { get; } = [];

    public ObservableCollection<CapabilityGrantItem> AvailableCapabilities { get; } = [];

    public ObservableCollection<CapabilityGrantItem> InvitationCapabilities { get; } = [];

    public ObservableCollection<SelectionOptionItem> InvitationInviterOptions { get; } = [];

    public IReadOnlyList<string> ApiFamilies { get; } =
        ["openai_responses", "openai_chat_completions", "anthropic_messages", "google_generate_content", "custom"];

    public IReadOnlyList<string> McpTransports { get; } = ["stdio", "streamable_http", "sse"];

    public IReadOnlyList<SelectionOptionItem> ThemeOptions { get; } =
    [
        new("system", "跟随系统"),
        new("light", "浅色"),
        new("dark", "深色"),
    ];

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
            if (IsRunning || !SetField(ref _selectedSession, value))
            {
                return;
            }
            if (value is null)
            {
                Roles.Clear();
                SelectedRole = null;
                SelectedPrivateRole = null;
                RefreshMentionTargets();
                OnPropertyChanged(nameof(Transcript));
                OnPropertyChanged(nameof(PrivateMessages));
                NotifySummary();
                return;
            }
            _meetingTitle = value.Title;
            OnPropertyChanged(nameof(MeetingTitle));
            Roles.Clear();
            foreach (var role in LongTermRoles.Where(role => !role.IsArchived))
            {
                Roles.Add(role);
            }
            foreach (var role in value.TemporaryRoles.Where(role => !role.IsArchived))
            {
                Roles.Add(role);
            }
            SelectedRole = Roles.FirstOrDefault();
            SelectedPrivateRole = Roles.FirstOrDefault();
            RefreshMentionTargets();
            OnPropertyChanged(nameof(Transcript));
            OnPropertyChanged(nameof(PrivateMessages));
            NotifySummary();
        }
    }

    public SessionGroupItem? SelectedSessionGroup
    {
        get => _selectedSessionGroup;
        set
        {
            if (SetField(ref _selectedSessionGroup, value))
            {
                RefreshVisibleSessions();
            }
        }
    }

    public RoleItem? SelectedPrivateRole
    {
        get => _selectedPrivateRole;
        set
        {
            if (SetField(ref _selectedPrivateRole, value))
            {
                OnPropertyChanged(nameof(PrivateMessages));
                OnPropertyChanged(nameof(CanSendPrivate));
            }
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
                OnPropertyChanged(nameof(CanSendPrivate));
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

    public string SkillDisplayName
    {
        get => _skillDisplayName;
        set => SetField(ref _skillDisplayName, value);
    }

    public string SkillDescription
    {
        get => _skillDescription;
        set => SetField(ref _skillDescription, value);
    }

    public string SkillSourceLocator
    {
        get => _skillSourceLocator;
        set => SetField(ref _skillSourceLocator, value);
    }

    public string McpDisplayName
    {
        get => _mcpDisplayName;
        set => SetField(ref _mcpDisplayName, value);
    }

    public string McpTransport
    {
        get => _mcpTransport;
        set => SetField(ref _mcpTransport, value);
    }

    public string McpCommandOrEndpoint
    {
        get => _mcpCommandOrEndpoint;
        set => SetField(ref _mcpCommandOrEndpoint, value);
    }

    public string ThemeMode
    {
        get => _themeMode;
        set => SetField(ref _themeMode, value);
    }

    public string RemoteSyncEndpoint
    {
        get => _remoteSyncEndpoint;
        set => SetField(ref _remoteSyncEndpoint, value);
    }

    public bool RemoteSyncEnabled
    {
        get => _remoteSyncEnabled;
        set => SetField(ref _remoteSyncEnabled, value);
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
                OnPropertyChanged(nameof(CanSendPrivate));
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
                OnPropertyChanged(nameof(CanSendPrivate));
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

    public bool CanSend => CanOperate && Roles.Any(role => !role.IsArchived);

    public bool CanSendPrivate => CanOperate && SelectedPrivateRole is { IsArchived: false };

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
            _clientSettings = await _clientSettingsStore.LoadAsync(cancellationToken);
            ThemeMode = _clientSettings.ThemeMode;
            RemoteSyncEnabled = _clientSettings.RemoteSyncEnabled;
            RemoteSyncEndpoint = _clientSettings.RemoteSyncEndpoint ?? string.Empty;
            Providers.Clear();
            Models.Clear();
            Skills.Clear();
            McpServers.Clear();
            SessionGroups.Clear();
            foreach (var provider in _workspace.Providers)
            {
                Providers.Add(provider);
            }
            foreach (var model in _workspace.Models)
            {
                Models.Add(model);
            }
            foreach (var skill in _workspace.Skills)
            {
                Skills.Add(skill);
            }
            foreach (var server in _workspace.McpServers)
            {
                McpServers.Add(server);
            }
            var configuredGroups = _workspace.SessionGroups.Count > 0
                ? _workspace.SessionGroups
                : [new SessionGroupProfileConfiguration
                {
                    GroupId = "group.general",
                    DisplayName = "常规会话",
                    Kind = "folder",
                    SortOrder = 0,
                }];
            foreach (var group in configuredGroups.OrderBy(group => group.SortOrder))
            {
                SessionGroups.Add(new SessionGroupItem(
                    group.GroupId,
                    group.DisplayName,
                    group.Kind,
                    group.SortOrder));
            }
            SelectedSessionGroup = SessionGroups.FirstOrDefault();
            if (_workspace.Roles.Count > 0)
            {
                LongTermRoles.Clear();
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
                    LongTermRoles.Add(role);
                }
                Roles.Clear();
                foreach (var role in LongTermRoles)
                {
                    Roles.Add(role);
                }
                SelectedRole = Roles.FirstOrDefault();
            }
            var defaultModel = Models.FirstOrDefault(model => model.Enabled);
            foreach (var role in LongTermRoles.Where(role => string.IsNullOrWhiteSpace(role.ModelProfileId)))
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
                        GroupId = definition.GroupId ?? SessionGroups.First().GroupId,
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
                    foreach (var message in definition.Messages)
                    {
                        var audienceRoleIds = message.Visibility == "private"
                            ? message.AudienceRoleIds
                            : [];
                        var item = new TranscriptItem(
                            message.SpeakerId,
                            message.SpeakerName,
                            message.Text,
                            ToDisplayState(message.State),
                            message.Kind,
                            message.Visibility,
                            audienceRoleIds,
                            message.MessageId,
                            message.OccurredAt);
                        var privateRoleId = audienceRoleIds.FirstOrDefault(
                            roleId => roleId != "user.direct_host");
                        if (message.Visibility == "private" && privateRoleId is not null)
                        {
                            session.GetPrivateThread(privateRoleId).Add(item);
                        }
                        else if (message.Visibility == "public")
                        {
                            session.Transcript.Add(item);
                        }
                    }
                    Sessions.Add(session);
                }
                RefreshVisibleSessions();
            }
            SelectedProvider = Providers.FirstOrDefault(provider => provider.Enabled);
            SelectedModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedInvitationModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedRoleModel = SelectedRole is null
                ? null
                : Models.FirstOrDefault(model => model.ModelProfileId == SelectedRole.ModelProfileId);
            RefreshInvitationInviters();
            RefreshInvitationCapabilities();
            RefreshMentionTargets();
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
                foreach (var role in LongTermRoles.Where(role => string.IsNullOrWhiteSpace(role.ModelProfileId)))
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
        var session = new SessionItem($"session-{Guid.NewGuid():N}", "新圆桌会议")
        {
            GroupId = SelectedSessionGroup?.GroupId ?? SessionGroups.First().GroupId,
        };
        Sessions.Insert(0, session);
        RefreshVisibleSessions();
        SelectedSession = session;
        Roles.Clear();
        foreach (var role in LongTermRoles.Where(role => !role.IsArchived))
        {
            Roles.Add(role);
        }
        SelectedRole = Roles.FirstOrDefault();
        StatusText = "新会话草稿";
        NotifySummary();
        await PersistSelectedSessionAsync(cancellationToken);
    }

    public async Task CreateSessionGroupAsync(
        string displayName,
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再修改会话分组。");
            return;
        }
        if (string.IsNullOrWhiteSpace(displayName) || kind is not ("project" or "folder"))
        {
            ShowError("会话分组需要名称，并且类型必须是项目或文件夹。");
            return;
        }
        var baseId = $"group.{NormalizeId(displayName)}";
        var groupId = baseId;
        var suffix = 2;
        while (SessionGroups.Any(group => group.GroupId == groupId))
        {
            groupId = $"{baseId}.{suffix++}";
        }
        var group = new SessionGroupItem(groupId, displayName.Trim(), kind, SessionGroups.Count);
        SessionGroups.Add(group);
        SelectedSessionGroup = group;
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        StatusText = $"已创建{group.KindLabel}分组";
    }

    public async Task CreateLongTermRoleAsync(string displayName, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再创建长期角色。");
            return;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ShowError("长期角色需要名称。");
            return;
        }
        var roleId = $"role.{NormalizeId(displayName)}.{Guid.NewGuid():N}";
        roleId = roleId[..Math.Min(roleId.Length, 120)];
        var role = new RoleItem(
            roleId,
            displayName.Trim(),
            "long_term",
            $"你是圆桌会议中的{displayName.Trim()}。明确职责、权限边界、交付格式与禁止事项。",
            Models.FirstOrDefault(model => model.Enabled)?.ModelProfileId);
        LongTermRoles.Add(role);
        Roles.Add(role);
        SelectedRole = role;
        SelectedPrivateRole ??= role;
        RefreshInvitationInviters();
        RefreshMentionTargets();
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        await PersistSelectedSessionAsync(cancellationToken);
        StatusText = $"已创建长期角色 {role.DisplayName}";
    }

    public async Task SaveSkillCatalogEntryAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再修改公共 Skill 目录。");
            return;
        }
        if (string.IsNullOrWhiteSpace(SkillDisplayName) ||
            string.IsNullOrWhiteSpace(SkillDescription) ||
            !Uri.TryCreate(SkillSourceLocator.Trim(), UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps)
        {
            ShowError("Skill 导入需要名称、说明和 HTTPS 来源地址。");
            return;
        }
        var skill = new SkillProfileConfiguration
        {
            SkillId = $"skill.{NormalizeId(SkillDisplayName)}",
            DisplayName = SkillDisplayName.Trim(),
            Description = SkillDescription.Trim(),
            Source = new SkillSourceConfiguration
            {
                Kind = "git",
                Locator = source.AbsoluteUri,
            },
            Risk = "medium",
            Enabled = true,
        };
        var existing = Skills.FirstOrDefault(item => item.SkillId == skill.SkillId);
        if (existing is not null)
        {
            Skills.Remove(existing);
        }
        Skills.Add(skill);
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        RefreshCapabilityGrants();
        RefreshInvitationCapabilities();
        SkillDisplayName = string.Empty;
        SkillDescription = string.Empty;
        SkillSourceLocator = string.Empty;
        StatusText = "Skill 已登记；安装与 LLM 安全审计仍待 Runtime Host 执行";
    }

    public async Task SaveMcpCatalogEntryAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再修改公共 MCP 目录。");
            return;
        }
        if (string.IsNullOrWhiteSpace(McpDisplayName) || string.IsNullOrWhiteSpace(McpCommandOrEndpoint))
        {
            ShowError("MCP 导入需要名称以及命令或端点。");
            return;
        }
        var server = new McpServerProfileConfiguration
        {
            McpServerId = $"mcp.{NormalizeId(McpDisplayName)}",
            DisplayName = McpDisplayName.Trim(),
            Transport = McpTransport,
            Command = McpTransport == "stdio" ? McpCommandOrEndpoint.Trim() : null,
            Endpoint = McpTransport == "stdio" ? null : McpCommandOrEndpoint.Trim(),
            Arguments = McpTransport == "stdio" ? [] : null,
            Enabled = true,
        };
        string? normalizedEndpoint = null;
        if (server.Transport != "stdio" && !TryNormalizeEndpoint(server.Endpoint ?? string.Empty, out normalizedEndpoint))
        {
            ShowError("远端 MCP 端点必须使用 HTTPS 或本机回环 HTTP。");
            return;
        }
        if (server.Transport != "stdio")
        {
            server.Endpoint = normalizedEndpoint;
        }
        var existing = McpServers.FirstOrDefault(item => item.McpServerId == server.McpServerId);
        if (existing is not null)
        {
            McpServers.Remove(existing);
        }
        McpServers.Add(server);
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        RefreshCapabilityGrants();
        RefreshInvitationCapabilities();
        McpDisplayName = string.Empty;
        McpCommandOrEndpoint = string.Empty;
        StatusText = "MCP 已登记；LLM 配置解析和执行器接入仍为 planned";
    }

    public async Task SaveClientSettingsAsync(
        string syncCredential,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再修改客户端与同步设置。");
            return;
        }
        string? normalizedEndpoint = null;
        if (!string.IsNullOrWhiteSpace(RemoteSyncEndpoint) &&
            !TryNormalizeEndpoint(RemoteSyncEndpoint, out normalizedEndpoint))
        {
            ShowError("远端同步服务器必须使用 HTTPS 或本机回环 HTTP。");
            return;
        }
        else
        {
            RemoteSyncEndpoint = normalizedEndpoint ?? string.Empty;
        }
        _clientSettings.ThemeMode = ThemeOptions.Any(option => option.Value == ThemeMode)
            ? ThemeMode
            : "system";
        _clientSettings.RemoteSyncEnabled = RemoteSyncEnabled;
        _clientSettings.RemoteSyncEndpoint = string.IsNullOrWhiteSpace(RemoteSyncEndpoint)
            ? null
            : RemoteSyncEndpoint;
        if (!string.IsNullOrWhiteSpace(syncCredential))
        {
            await _credentialStore.SaveAsync(
                _clientSettings.RemoteSyncCredentialRef,
                syncCredential,
                cancellationToken);
        }
        await _clientSettingsStore.SaveAsync(_clientSettings, cancellationToken);
        StatusText = "客户端设置已保存；远端同步连接仍为 pending";
        ErrorMessage = string.Empty;
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
        if (runtime is null || !IsRunning || Roles.All(role => role.IsArchived))
        {
            ShowError("请先启动会议并保留至少一个活跃角色。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowError("请输入要交给角色的议题或约束。");
            return false;
        }

        ErrorMessage = string.Empty;
        var mentions = MentionTargets
            .Where(target => target.IsMentioned && !target.Role.IsArchived)
            .Select(target => target.RoleId)
            .ToArray();
        var receipt = await runtime.SendCommandAsync(
            "speech.broadcast",
            "user.direct_host",
            null,
            new Dictionary<string, object?>
            {
                ["message"] = message.Trim(),
                ["mentions"] = mentions,
            },
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
            return false;
        }
        foreach (var target in MentionTargets)
        {
            target.IsMentioned = false;
        }
        NotifySummary();
        return true;
    }

    public async Task<bool> SendPrivateMessageAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        var role = SelectedPrivateRole;
        if (runtime is null || !IsRunning || role is null || role.IsArchived)
        {
            ShowError("请先启动会议并选择一个私聊角色。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowError("请输入私聊内容。");
            return false;
        }
        ErrorMessage = string.Empty;
        var receipt = await runtime.SendCommandAsync(
            "speech.direct",
            "user.direct_host",
            role.RoleId,
            new Dictionary<string, object?> { ["message"] = message.Trim() },
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
            return false;
        }
        role.Status = "私聊回应中";
        role.ActivitySummary = "正在处理仅对你可见的私聊；未公开模型私有推理";
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
        SelectedPrivateRole = role;
        RefreshMentionTargets();
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
                RefreshMentionTargets();
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
            if (!LongTermRoles.Contains(role))
            {
                LongTermRoles.Add(role);
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
        if (!LongTermRoles.Contains(role))
        {
            LongTermRoles.Add(role);
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
        RefreshMentionTargets();
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
                    _privateStreamingMessages.Clear();
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
            case "message.published":
                if (meetingEvent.Payload.TryGetProperty("message", out var publicMessage))
                {
                    Transcript.Add(new TranscriptItem(
                        "user.direct_host",
                        "我",
                        publicMessage.GetString() ?? string.Empty,
                        "已发送",
                        "host",
                        "public",
                        [],
                        $"message.{meetingEvent.EventId.Replace('-', '.')}",
                        meetingEvent.OccurredAt));
                }
                break;
            case "message.direct_sent":
                var privateTarget = meetingEvent.TargetId is null
                    ? null
                    : Roles.FirstOrDefault(item => item.RoleId == meetingEvent.TargetId);
                if (privateTarget is not null && meetingEvent.Payload.TryGetProperty("message", out var privateMessage))
                {
                    SelectedSession?.GetPrivateThread(privateTarget.RoleId).Add(new TranscriptItem(
                        "user.direct_host",
                        "我",
                        privateMessage.GetString() ?? string.Empty,
                        "已发送",
                        "host",
                        "private",
                        [privateTarget.RoleId],
                        $"message.{meetingEvent.EventId.Replace('-', '.')}",
                        meetingEvent.OccurredAt));
                    if (SelectedPrivateRole?.RoleId == privateTarget.RoleId)
                    {
                        OnPropertyChanged(nameof(PrivateMessages));
                    }
                }
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
                    if (!LongTermRoles.Contains(role))
                    {
                        LongTermRoles.Add(role);
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
                    if (meetingEvent.Visibility == "private")
                    {
                        role.Status = "私聊中";
                        role.ActivitySummary = "正在生成仅对你可见的答复；未公开模型私有推理";
                        var privateTranscript = new TranscriptItem(
                            role.RoleId,
                            role.DisplayName,
                            string.Empty,
                            "生成中",
                            "role",
                            "private",
                            [role.RoleId],
                            $"message.{meetingEvent.EventId.Replace('-', '.')}",
                            meetingEvent.OccurredAt);
                        SelectedSession?.GetPrivateThread(role.RoleId).Add(privateTranscript);
                        _privateStreamingMessages[role.RoleId] = privateTranscript;
                        if (SelectedPrivateRole?.RoleId == role.RoleId)
                        {
                            OnPropertyChanged(nameof(PrivateMessages));
                        }
                        break;
                    }
                    foreach (var item in Roles.Where(item => item.Status == "发言中"))
                    {
                        item.Status = "空闲";
                    }
                    role.Status = "发言中";
                    role.ActivitySummary = "正在公开发言；未公开模型私有推理";
                    var transcript = new TranscriptItem(
                        role.RoleId,
                        role.DisplayName,
                        string.Empty,
                        "生成中",
                        "role",
                        "public",
                        null,
                        $"message.{meetingEvent.EventId.Replace('-', '.')}",
                        meetingEvent.OccurredAt);
                    Transcript.Add(transcript);
                    _streamingMessages[role.RoleId] = transcript;
                }
                break;
            case "speech.delta":
                var streamingMessages = meetingEvent.Visibility == "private"
                    ? _privateStreamingMessages
                    : _streamingMessages;
                if (role is not null &&
                    streamingMessages.TryGetValue(role.RoleId, out var streaming) &&
                    meetingEvent.Payload.TryGetProperty("delta", out var delta))
                {
                    streaming.Text += delta.GetString() ?? string.Empty;
                }
                break;
            case "speech.completed":
            case "speech.cancelled":
                var finishedRole = meetingEvent.Visibility != "private" &&
                    meetingEvent.Kind == "speech.cancelled" && meetingEvent.TargetId is not null
                    ? Roles.FirstOrDefault(item => item.RoleId == meetingEvent.TargetId)
                    : role;
                if (finishedRole is not null)
                {
                    finishedRole.Status = "空闲";
                    finishedRole.ActivitySummary = "空闲；未公开模型私有推理";
                    var completedMessages = meetingEvent.Visibility == "private"
                        ? _privateStreamingMessages
                        : _streamingMessages;
                    if (completedMessages.Remove(finishedRole.RoleId, out var finished))
                    {
                        finished.State = meetingEvent.Kind == "speech.completed" ? "已完成" : "已取消";
                    }
                }
                PersistCurrentSessionInBackground();
                break;
            case "tool.started":
                if (role is not null)
                {
                    var toolName = meetingEvent.Payload.TryGetProperty("toolName", out var tool)
                        ? tool.GetString() ?? "工具"
                        : "工具";
                    role.ActivitySummary = $"正在调用 {toolName}；参数和结果不在角色状态页公开";
                }
                break;
            case "tool.completed":
            case "tool.failed":
                if (role is not null)
                {
                    role.ActivitySummary = meetingEvent.Kind == "tool.completed"
                        ? "工具调用已完成；未公开参数、结果或模型私有推理"
                        : "工具调用失败；未公开参数、结果或模型私有推理";
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
        foreach (var skill in Skills.Where(item => item.Enabled))
        {
            AddCapabilityGrant(new CapabilityGrantItem(
                skill.SkillId,
                skill.DisplayName,
                "Skill",
                role.SkillIds.Contains(skill.SkillId)));
        }
        foreach (var server in McpServers.Where(item => item.Enabled))
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
        foreach (var skill in Skills.Where(item => item.Enabled))
        {
            InvitationCapabilities.Add(new CapabilityGrantItem(
                skill.SkillId,
                skill.DisplayName,
                "Skill",
                false));
        }
        foreach (var server in McpServers.Where(item => item.Enabled))
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
        foreach (var role in LongTermRoles.Where(role => !role.IsArchived))
        {
            InvitationInviterOptions.Add(new SelectionOptionItem(role.RoleId, role.DisplayName));
        }
        if (InvitationInviterOptions.All(option => option.Value != InvitationInviterId))
        {
            InvitationInviterId = "user.direct_host";
        }
        OnPropertyChanged(nameof(CanInviteTemporaryRole));
    }

    private void RefreshMentionTargets()
    {
        var selected = MentionTargets
            .Where(target => target.IsMentioned)
            .Select(target => target.RoleId)
            .ToHashSet(StringComparer.Ordinal);
        MentionTargets.Clear();
        foreach (var role in Roles.Where(role => !role.IsArchived))
        {
            MentionTargets.Add(new MentionTargetItem(role)
            {
                IsMentioned = selected.Contains(role.RoleId),
            });
        }
    }

    private void RefreshVisibleSessions()
    {
        VisibleSessions.Clear();
        var groupId = SelectedSessionGroup?.GroupId;
        foreach (var session in Sessions
            .Where(session => groupId is null || session.GroupId == groupId)
            .OrderByDescending(session => session.UpdatedAt))
        {
            VisibleSessions.Add(session);
        }
        if (SelectedSession is null || !VisibleSessions.Contains(SelectedSession))
        {
            SelectedSession = VisibleSessions.FirstOrDefault();
        }
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
        _workspace.Skills = Skills.ToList();
        _workspace.McpServers = McpServers.ToList();
        _workspace.SessionGroups = SessionGroups
            .Select(group => new SessionGroupProfileConfiguration
            {
                GroupId = group.GroupId,
                DisplayName = group.DisplayName,
                Kind = group.Kind,
                SortOrder = group.SortOrder,
            })
            .ToList();
        _workspace.Roles = LongTermRoles
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
            GroupId = session.GroupId,
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
            Messages = session.Transcript
                .Concat(session.PrivateThreads.Values.SelectMany(thread => thread))
                .OrderBy(message => message.OccurredAt)
                .Select(message => new SessionMessageConfiguration
                {
                    MessageId = message.MessageId,
                    Kind = message.Kind,
                    SpeakerId = message.RoleId,
                    SpeakerName = message.Speaker,
                    Visibility = message.Visibility,
                    AudienceRoleIds = message.AudienceRoleIds.ToList(),
                    Text = message.Text,
                    State = ToStorageState(message.State),
                    OccurredAt = message.OccurredAt,
                })
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

    private void PersistCurrentSessionInBackground()
    {
        _ = PersistCurrentSessionSafelyAsync();
    }

    private async Task PersistCurrentSessionSafelyAsync()
    {
        try
        {
            await PersistSelectedSessionAsync(CancellationToken.None);
        }
        catch
        {
            _dispatcher.TryEnqueue(() => ShowError("会议记录暂时无法写入本地会话文件。"));
        }
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

    private static string ToStorageState(string state) => state switch
    {
        "已发送" or "已提交" => "submitted",
        "生成中" or "处理中" => "streaming",
        "已取消" => "cancelled",
        _ => "completed",
    };

    private static string ToDisplayState(string state) => state switch
    {
        "submitted" => "已发送",
        "streaming" => "生成中",
        "cancelled" => "已取消",
        _ => "已完成",
    };

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
        OnPropertyChanged(nameof(CanSendPrivate));
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
