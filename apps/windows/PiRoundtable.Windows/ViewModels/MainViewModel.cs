using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IRuntimeHostFactory _runtimeHostFactory;
    private readonly IMeetingCoreFactory _meetingCoreFactory;
    private readonly IMeetingEventStore _eventStore;
    private readonly MeetingEventIngestionQueue _eventQueue;
    private readonly Dictionary<string, TranscriptItem> _streamingMessages = [];
    private readonly Dictionary<string, TranscriptItem> _privateStreamingMessages = [];
    private readonly Dictionary<string, string> _publicPromptsByCommandId = new(StringComparer.Ordinal);
    private readonly ObservableCollection<TranscriptItem> _emptyTranscript = [];
    private readonly ObservableCollection<TranscriptItem> _emptyPrivateThread = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly WorkspaceConfigurationStore _workspaceStore;
    private readonly RoundtableSessionStore _sessionStore;
    private readonly WindowsCredentialStore _credentialStore;
    private readonly ClientSettingsStore _clientSettingsStore;
    private readonly ProviderModelDiscoveryService _providerModelDiscovery;
    private readonly CatalogImportService _catalogImport;
    private readonly LlmCatalogAnalysisService _llmCatalogAnalysis;
    private WorkspaceConfiguration _workspace = new();
    private ClientSettingsConfiguration _clientSettings = new();
    private DiscussionSchedulerStateConfiguration _discussionState = new();
    private IRuntimeHostProcess? _runtime;
    private IRuntimeHostProcess? _startingRuntime;
    private IMeetingCoreSession? _meetingCore;
    private RoleItem? _selectedRole;
    private RoleItem? _selectedPrivateRole;
    private SessionItem? _selectedSession;
    private SessionGroupItem? _selectedSessionGroup;
    private ProviderProfileConfiguration? _selectedProvider;
    private ModelProfileConfiguration? _selectedModel;
    private ModelProfileConfiguration? _selectedRoleModel;
    private ModelProfileConfiguration? _selectedInvitationModel;
    private ModelProfileConfiguration? _selectedImportModel;
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
    private string _mcpSourceLocator = string.Empty;
    private string _mcpTransport = "stdio";
    private string _mcpCommandOrEndpoint = string.Empty;
    private string _mcpToolCatalogText = string.Empty;
    private string _themeMode = "system";
    private string _remoteSyncEndpoint = string.Empty;
    private bool _remoteSyncEnabled;
    private ulong _sequence;
    private ulong _runtimeGeneration;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isSendingPrompt;
    private bool _eventStreamFaulted;
    private bool _initialized;
    private bool _disposed;

    internal MainViewModel(
        IUiDispatcher dispatcher,
        MainViewModelServices services)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(services);
        _runtimeHostFactory = services.RuntimeHostFactory;
        _meetingCoreFactory = services.MeetingCoreFactory;
        _eventStore = services.EventStore;
        _workspaceStore = services.WorkspaceStore;
        _sessionStore = services.SessionStore;
        _credentialStore = services.CredentialStore;
        _clientSettingsStore = services.ClientSettingsStore;
        _providerModelDiscovery = services.ProviderModelDiscovery;
        _catalogImport = services.CatalogImport;
        _llmCatalogAnalysis = services.LlmCatalogAnalysis;
        _eventQueue = services.EventIngestionQueueFactory.Create(
            AcceptMeetingEventAsync,
            ReportEventStreamFaultAsync,
            WriteEventIngestionTrace);
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

    public ObservableCollection<ProviderProfileConfiguration> Providers { get; } = [];

    public ObservableCollection<ModelProfileConfiguration> Models { get; } = [];

    public ObservableCollection<ProviderModelCandidate> DiscoveredModels { get; } = [];

    public ObservableCollection<SkillProfileConfiguration> Skills { get; } = [];

    public ObservableCollection<McpServerProfileConfiguration> McpServers { get; } = [];

    public ObservableCollection<CapabilityGrantItem> AvailableCapabilities { get; } = [];

    public ObservableCollection<CapabilityGrantItem> InvitationCapabilities { get; } = [];

    public ObservableCollection<McpToolGrantItem> AvailableMcpTools { get; } = [];

    public ObservableCollection<McpToolGrantItem> InvitationMcpTools { get; } = [];

    public ObservableCollection<ToolApprovalItem> PendingToolApprovals { get; } = [];

    public ObservableCollection<SubagentRunItem> SubagentRuns { get; } = [];

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
                OnPropertyChanged(nameof(Transcript));
                OnPropertyChanged(nameof(PrivateMessages));
                NotifySummary();
                NotifyLifecycleProperties();
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
            OnPropertyChanged(nameof(Transcript));
            OnPropertyChanged(nameof(PrivateMessages));
            NotifySummary();
            NotifyLifecycleProperties();
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

    public ModelProfileConfiguration? SelectedImportModel
    {
        get => _selectedImportModel;
        set => SetField(ref _selectedImportModel, value);
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
        private set
        {
            if (SetField(ref _statusText, value))
            {
                OnPropertyChanged(nameof(RuntimeStateSummary));
            }
        }
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

    public string McpSourceLocator
    {
        get => _mcpSourceLocator;
        set => SetField(ref _mcpSourceLocator, value);
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

    public string McpToolCatalogText
    {
        get => _mcpToolCatalogText;
        set => SetField(ref _mcpToolCatalogText, value);
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
                NotifyLifecycleProperties();
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
                NotifyLifecycleProperties();
            }
        }
    }

    public bool IsPaused => !IsRunning && SelectedSession?.Phase == "live";

    public bool CanStart =>
        !IsRunning &&
        !IsBusy &&
        SelectedSession?.Phase == "draft" &&
        Providers.Count > 0 &&
        Models.Count > 0;

    public bool CanResume =>
        IsPaused &&
        !IsBusy &&
        Providers.Count > 0 &&
        Models.Count > 0;

    public bool CanPause => CanOperate;

    public bool CanClose => IsRunning && !IsBusy;

    public Visibility StartMeetingVisibility =>
        !IsRunning && SelectedSession?.Phase != "live" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResumeMeetingVisibility => IsPaused ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PauseMeetingVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public bool CanOperate => IsRunning && !IsBusy && !_eventStreamFaulted;

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

    public bool CanSend => CanOperate && !_isSendingPrompt && Roles.Any(role => !role.IsArchived);

    public bool CanSendPrivate => CanOperate && SelectedPrivateRole is { IsArchived: false };

    public bool CanControlDiscussion =>
        CanOperate && _discussionState.Configured && _discussionState.Mode != "completed" && !_isSendingPrompt;

    public bool CanResumeDiscussion =>
        CanControlDiscussion && _discussionState.Mode == "paused";

    public bool CanAdvanceAgenda =>
        CanControlDiscussion && _discussionState.Mode == "agenda";

    public Visibility DiscussionStripVisibility =>
        _discussionState.Configured ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DiscussionResumeVisibility =>
        _discussionState.Configured && _discussionState.Mode == "paused"
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string DiscussionModeLabel => _discussionState.Mode switch
    {
        "agenda" => "议程",
        "free_discussion" => "自由讨论",
        "convergence" => "收敛",
        "paused" => "自动主持已暂停",
        "completed" => "讨论已完成",
        _ => "未配置",
    };

    public string DiscussionAgendaSummary
    {
        get
        {
            var active = _discussionState.AgendaItems.FirstOrDefault(item =>
                item.AgendaItemId == _discussionState.ActiveAgendaItemId || item.Status == "active");
            return active is null ? "开放议题" : active.Title;
        }
    }

    public string DiscussionBudgetSummary =>
        $"第 {Math.Max(1, _discussionState.Counters.Rounds)} 轮 · 发言 {_discussionState.Counters.PublicTurns}/{_discussionState.Limits.HardTurnLimit} · 抢答 {_discussionState.Counters.Interruptions}/{_discussionState.Limits.MaxInterruptionsPerSegment}";

    public string DiscussionQueueSummary => _discussionState.PendingRequests.Count == 0
        ? "发言队列空闲"
        : $"{_discussionState.PendingRequests.Count} 个角色申请发言";

    public bool CanPromoteSelectedRole =>
        !IsBusy && SelectedRole is { Scope: "temporary", IsArchived: false };

    public bool CanArchiveSelectedRole => !IsBusy && SelectedRole is { IsArchived: false };

    public string MeetingSummary => $"{Roles.Count(role => !role.IsArchived)} 个活跃角色 · {StatusText}";

    public string ParticipantSummary => $"{Roles.Count(role => !role.IsArchived)} 个角色";

    public string GenerationSummary => _runtimeGeneration == 0
        ? "尚未启动"
        : $"{_runtimeGeneration} / {_sequence}";

    public string RuntimeStateSummary => _runtimeGeneration == 0
        ? StatusText
        : $"{StatusText} · Gen {_runtimeGeneration} · Seq {_sequence}";

    public bool HasPendingToolApprovals => PendingToolApprovals.Count > 0;

    public string ToolApprovalLabel => PendingToolApprovals.Count == 0
        ? SubagentRuns.Count(item => item.IsActive) == 0
            ? "活动与审批"
            : $"活动 ({SubagentRuns.Count(item => item.IsActive)})"
        : $"待审批 ({PendingToolApprovals.Count})";

    public string ToolApprovalSectionLabel => PendingToolApprovals.Count == 0
        ? "工具审批"
        : $"工具审批 ({PendingToolApprovals.Count})";

    public string SubagentActivityLabel => SubagentRuns.Count == 0
        ? "SubAgent 活动"
        : $"SubAgent 活动 ({SubagentRuns.Count})";

    public Visibility SubagentEmptyVisibility => SubagentRuns.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ToolApprovalEmptyVisibility => PendingToolApprovals.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string DiscoveredModelSummary => DiscoveredModels.Count == 0
        ? "尚未获取模型列表"
        : $"已发现 {DiscoveredModels.Count} 个可用模型";

    public Visibility TranscriptEmptyVisibility => Transcript.Count == 0
        && Providers.Count > 0 ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ProviderSetupVisibility => Transcript.Count == 0 && Providers.Count == 0
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

    public void ReportClientStatus(string message)
    {
        StatusText = message;
        ErrorMessage = string.Empty;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }
        try
        {
            await _eventStore.InitializeAsync(cancellationToken);
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
                        profile.ModelRoute.PrimaryModelProfileId,
                        retentionPolicy: "retain_profile",
                        networkAccess: profile.Delegation.NetworkAccess,
                        modelRoute: profile.ModelRoute);
                    role.SkillIds.UnionWith(profile.Capabilities.SkillIds);
                    foreach (var grant in profile.Capabilities.McpGrants)
                    {
                        role.SetMcpGrant(grant.McpServerId, grant.ToolAllowlist);
                    }
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
                    var checkpoint = await _eventStore.GetCheckpointAsync(
                        definition.SessionId,
                        cancellationToken);
                    var restoredPhase = checkpoint?.IsClosed == true
                        ? "closed"
                        : definition.Phase == "live" && checkpoint is null
                            ? "draft"
                            : definition.Phase;
                    var session = new SessionItem(definition.SessionId, definition.Title)
                    {
                        GroupId = definition.GroupId ?? SessionGroups.First().GroupId,
                        Phase = restoredPhase,
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
                            invitation.CreatedAt,
                            participant.ModelRouteSnapshot);
                        role.SkillIds.UnionWith(participant.CapabilitiesSnapshot.SkillIds);
                        foreach (var grant in participant.CapabilitiesSnapshot.McpGrants)
                        {
                            role.SetMcpGrant(grant.McpServerId, grant.ToolAllowlist);
                        }
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
            else
            {
                var selectedGroupId = SelectedSessionGroup?.GroupId
                    ?? SessionGroups.First().GroupId;
                var draft = Sessions.FirstOrDefault() ?? new SessionItem(
                    $"session-{Guid.NewGuid():N}",
                    _meetingTitle);
                draft.GroupId = selectedGroupId;
                if (!Sessions.Contains(draft))
                {
                    Sessions.Add(draft);
                }
                RefreshVisibleSessions();
                SelectedSession = draft;
            }
            SelectedProvider = Providers.FirstOrDefault(provider => provider.Enabled);
            SelectedModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedInvitationModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedImportModel ??= Models.FirstOrDefault(model => model.Enabled);
            SelectedRoleModel = SelectedRole is null
                ? null
                : Models.FirstOrDefault(model => model.ModelProfileId == SelectedRole.ModelProfileId);
            RefreshInvitationInviters();
            RefreshInvitationCapabilities();
            StatusText = Providers.Count == 0 ? "等待提供商配置" : "配置已加载";
            _initialized = true;
            NotifyLifecycleProperties();
            NotifySummary();
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
        DiscoveredModels.Clear();
        OnPropertyChanged(nameof(DiscoveredModelSummary));
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
                    string.IsNullOrWhiteSpace(RuntimeProviderId))
                {
                    ShowError("提供商名称和 Runtime Provider ID 不能为空。");
                    return;
                }
                if (!TryNormalizeEndpoint(ProviderEndpoint, out var endpoint))
                {
                    ShowError("端点必须使用 HTTPS，或使用本机回环 HTTP，长度不超过 2048，且不能包含凭据、空白、查询参数或片段。");
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
                ModelProfileConfiguration? model = null;
                if (!string.IsNullOrWhiteSpace(ModelId))
                {
                    var modelProfileId = SelectedModel is not null &&
                        SelectedModel.ProviderProfileId == provider.ProviderProfileId
                            ? SelectedModel.ModelProfileId
                            : $"model.{NormalizeId(provider.RuntimeProviderId)}.{NormalizeId(ModelId)}";
                    model = SelectedModel is not null && SelectedModel.ProviderProfileId == provider.ProviderProfileId
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
                }
                if (model is not null)
                {
                    foreach (var role in LongTermRoles.Where(role => string.IsNullOrWhiteSpace(role.ModelProfileId)))
                    {
                        role.ModelProfileId = model.ModelProfileId;
                    }
                }
                SelectedProvider = provider;
                if (model is not null)
                {
                    SelectedModel = model;
                    SelectedInvitationModel ??= model;
                    SelectedImportModel ??= model;
                }
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                await PersistSelectedSessionAsync(cancellationToken);
                ErrorMessage = string.Empty;
                StatusText = model is null ? "提供商配置已保存" : "长期配置已保存";
                OnPropertyChanged(nameof(CanStart));
                NotifySummary();
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

    public async Task DiscoverProviderModelsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                ShowError("会议启动或运行期间不能获取模型列表。");
                return;
            }
            if (string.IsNullOrWhiteSpace(RuntimeProviderId) ||
                !TryNormalizeEndpoint(ProviderEndpoint, out var endpoint))
            {
                ShowError("请填写 Runtime Provider ID，并使用无凭据、查询或片段的 HTTPS 或本机回环 HTTP 端点。");
                return;
            }
            if (ApiFamily == "custom" && endpoint is null)
            {
                ShowError("自定义 API 家族需要填写模型列表所在的基础端点。");
                return;
            }

            IsBusy = true;
            try
            {
                var providerProfileId = SelectedProvider?.ProviderProfileId
                    ?? $"provider.{NormalizeId(RuntimeProviderId)}";
                var credentialReference = SelectedProvider?.CredentialRef
                    ?? $"wincred://PiRoundtable/provider/{providerProfileId}";
                var secret = apiKey;
                if (string.IsNullOrWhiteSpace(secret))
                {
                    secret = await _credentialStore.ReadAsync(credentialReference, cancellationToken) ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(secret))
                {
                    ShowError("获取模型列表需要填写 API Key，或先保存该提供商的凭据。");
                    return;
                }

                var provider = new ProviderProfileConfiguration
                {
                    ProviderProfileId = providerProfileId,
                    DisplayName = string.IsNullOrWhiteSpace(ProviderDisplayName) ? RuntimeProviderId.Trim() : ProviderDisplayName.Trim(),
                    ApiFamily = ApiFamily,
                    RuntimeProviderId = RuntimeProviderId.Trim(),
                    Endpoint = endpoint,
                    CredentialRef = credentialReference,
                };
                IReadOnlyList<ProviderModelCandidate> discovered;
                try
                {
                    discovered = await _providerModelDiscovery.DiscoverAsync(provider, secret, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    ShowError("获取模型列表超时，请检查端点和网络后重试。");
                    return;
                }
                catch (HttpRequestException)
                {
                    ShowError("无法连接模型列表端点，请检查地址、代理和网络。");
                    return;
                }
                catch (System.Text.Json.JsonException)
                {
                    ShowError("提供商返回的模型列表不是有效 JSON。");
                    return;
                }
                catch (InvalidOperationException error)
                {
                    ShowError(error.Message);
                    return;
                }

                DiscoveredModels.Clear();
                foreach (var candidate in discovered)
                {
                    DiscoveredModels.Add(candidate);
                }
                OnPropertyChanged(nameof(DiscoveredModelSummary));
                ErrorMessage = string.Empty;
                StatusText = discovered.Count == 0 ? "未发现可导入模型" : $"已发现 {discovered.Count} 个模型";
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

    public void SelectAllDiscoveredModels()
    {
        foreach (var candidate in DiscoveredModels)
        {
            candidate.IsSelected = true;
        }
        OnPropertyChanged(nameof(DiscoveredModels));
    }

    public async Task ImportSelectedProviderModelsAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                ShowError("会议启动或运行期间不能导入模型。");
                return;
            }
            var provider = SelectedProvider;
            if (provider is null)
            {
                ShowError("请先保存并选择提供商，再导入发现的模型。");
                return;
            }
            var selected = DiscoveredModels.Where(candidate => candidate.IsSelected).ToArray();
            if (selected.Length == 0)
            {
                ShowError("请至少勾选一个要导入的模型。");
                return;
            }

            IsBusy = true;
            try
            {
                ModelProfileConfiguration? firstImported = null;
                foreach (var candidate in selected)
                {
                    var model = Models.FirstOrDefault(item =>
                        item.ProviderProfileId == provider.ProviderProfileId &&
                        item.ModelId == candidate.ModelId);
                    if (model is null)
                    {
                        var baseProfileId = $"model.{NormalizeId(provider.RuntimeProviderId)}.{NormalizeId(candidate.ModelId)}";
                        var profileId = baseProfileId;
                        for (var suffix = 2; Models.Any(item => item.ModelProfileId == profileId); suffix++)
                        {
                            profileId = $"{baseProfileId}.{suffix}";
                        }
                        model = new ModelProfileConfiguration
                        {
                            ModelProfileId = profileId,
                            ProviderProfileId = provider.ProviderProfileId,
                            ModelId = candidate.ModelId,
                        };
                        Models.Add(model);
                    }
                    model.DisplayName = candidate.DisplayName;
                    model.ContextWindow = candidate.ContextWindow;
                    model.Capabilities = candidate.Capabilities.ToList();
                    model.Enabled = true;
                    firstImported ??= model;
                }
                if (firstImported is not null)
                {
                    SelectedModel = firstImported;
                    SelectedInvitationModel ??= firstImported;
                    SelectedImportModel ??= firstImported;
                    foreach (var role in LongTermRoles.Where(role => string.IsNullOrWhiteSpace(role.ModelProfileId)))
                    {
                        role.ModelProfileId = firstImported.ModelProfileId;
                    }
                }
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                await PersistSelectedSessionAsync(cancellationToken);
                ErrorMessage = string.Empty;
                StatusText = $"已导入 {selected.Length} 个模型";
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

    public SessionExportPackage CreateSelectedSessionExport(bool includePrivateMessages)
    {
        var session = SelectedSession ?? throw new InvalidOperationException("当前没有可导出的会话。");
        return SessionTransferService.CreatePackage(session, includePrivateMessages);
    }

    public async Task ImportSessionPackageAsync(
        SessionImportPreflight preflight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再导入会话包。");
            return;
        }

        var source = preflight.Package;
        var session = new SessionItem(
            $"session-{Guid.NewGuid():N}",
            $"导入 · {source.Title}")
        {
            GroupId = SelectedSessionGroup?.GroupId ?? SessionGroups.First().GroupId,
            Phase = "draft",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var message in source.Messages)
        {
            var item = new TranscriptItem(
                message.SpeakerId,
                message.SpeakerName,
                message.Text,
                ToDisplayState(message.State),
                message.Kind,
                message.Visibility,
                message.AudienceRoleIds,
                message.MessageId,
                message.OccurredAt);
            if (message.Visibility == "private")
            {
                var privateRoleId = message.AudienceRoleIds.FirstOrDefault(value => value != "user.direct_host")
                    ?? message.AudienceRoleIds[0];
                session.GetPrivateThread(privateRoleId).Add(item);
            }
            else
            {
                session.Transcript.Add(item);
            }
        }

        Sessions.Insert(0, session);
        RefreshVisibleSessions();
        SelectedSession = session;
        Roles.Clear();
        foreach (var role in LongTermRoles.Where(role => !role.IsArchived))
        {
            Roles.Add(role);
        }
        SelectedRole = Roles.FirstOrDefault();
        await PersistSelectedSessionAsync(cancellationToken);
        StatusText = $"已从预检包创建新草稿：{session.Title}";
        ErrorMessage = string.Empty;
        NotifySummary();
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
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        await PersistSelectedSessionAsync(cancellationToken);
        StatusText = $"已创建长期角色 {role.DisplayName}";
    }

    public async Task SaveSkillCatalogEntryAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                ShowError("请先结束当前会话，再导入公共 Skill。");
                return;
            }
            if (!Uri.TryCreate(SkillSourceLocator.Trim(), UriKind.Absolute, out var source))
            {
                ShowError("请填写受支持 Git 平台的 HTTPS Skill 地址。");
                return;
            }
            var importRuntime = await ResolveImportRuntimeAsync(cancellationToken);
            if (importRuntime is null)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await using var checkout = await _catalogImport.PrepareAsync(source, cancellationToken);
                if (checkout.Snapshot.SkillRoots.Count == 0)
                {
                    ShowError("仓库导入范围内没有找到 SKILL.md。");
                    return;
                }
                var analysis = await _llmCatalogAnalysis.AnalyzeAsync(
                    "skill",
                    checkout.Snapshot,
                    importRuntime.Value.Provider,
                    importRuntime.Value.Model,
                    importRuntime.Value.ApiKey,
                    cancellationToken);
                var displayName = string.IsNullOrWhiteSpace(SkillDisplayName)
                    ? analysis.DisplayName
                    : SkillDisplayName.Trim();
                var skillId = $"skill.{NormalizeId(displayName)}";
                var install = await _catalogImport.InstallAsync(
                    checkout,
                    "skill",
                    skillId,
                    analysis.RelativeRoot,
                    cancellationToken);
                var enabled = analysis.Recommended && analysis.Risk != "high";
                var skill = new SkillProfileConfiguration
                {
                    SkillId = skillId,
                    DisplayName = displayName,
                    Description = string.IsNullOrWhiteSpace(SkillDescription) ? analysis.Description : SkillDescription.Trim(),
                    Source = new SkillSourceConfiguration
                    {
                        Kind = "git",
                        Locator = source.AbsoluteUri,
                        ContentDigest = install.ContentDigest,
                    },
                    Risk = analysis.Risk,
                    ImportStatus = enabled ? "installed" : "review_required",
                    InstallDirectory = install.InstallDirectory,
                    AuditSummary = analysis.AuditSummary,
                    AuditedAt = DateTimeOffset.UtcNow,
                    Enabled = enabled,
                };
                ReplaceSkill(skill);
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                RefreshCapabilityGrants();
                RefreshInvitationCapabilities();
                SkillDisplayName = string.Empty;
                SkillDescription = string.Empty;
                SkillSourceLocator = string.Empty;
                ErrorMessage = string.Empty;
                StatusText = enabled
                    ? $"Skill {skill.DisplayName} 已审阅并安装"
                    : $"Skill {skill.DisplayName} 已隔离安装，等待人工审核";
            }
            catch (Exception error) when (HandleCatalogImportError(error, cancellationToken))
            {
                // The exception filter reports a sanitized, actionable error.
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

    public async Task ImportMcpCatalogEntryAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || IsRunning || IsBusy)
            {
                ShowError("请先结束当前会话，再导入公共 MCP。");
                return;
            }
            if (!Uri.TryCreate(McpSourceLocator.Trim(), UriKind.Absolute, out var source))
            {
                ShowError("请填写受支持 Git 平台的 HTTPS MCP 地址。");
                return;
            }
            var importRuntime = await ResolveImportRuntimeAsync(cancellationToken);
            if (importRuntime is null)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await using var checkout = await _catalogImport.PrepareAsync(source, cancellationToken);
                var analysis = await _llmCatalogAnalysis.AnalyzeAsync(
                    "mcp",
                    checkout.Snapshot,
                    importRuntime.Value.Provider,
                    importRuntime.Value.Model,
                    importRuntime.Value.ApiKey,
                    cancellationToken);
                if (analysis.Transport != "stdio" ||
                    string.IsNullOrWhiteSpace(analysis.Command) ||
                    !IsAllowedImportedMcpCommand(analysis.Command) ||
                    analysis.Arguments.Any(ContainsPotentialSecret))
                {
                    ShowError("Git 导入只接受受限启动器、无敏感参数的 stdio MCP 配置。");
                    return;
                }
                var displayName = string.IsNullOrWhiteSpace(McpDisplayName)
                    ? analysis.DisplayName
                    : McpDisplayName.Trim();
                var mcpId = $"mcp.{NormalizeId(displayName)}";
                var install = await _catalogImport.InstallAsync(
                    checkout,
                    "mcp",
                    mcpId,
                    analysis.RelativeRoot,
                    cancellationToken);
                var workingDirectory = ResolveInstalledSubpath(
                    install.InstallDirectory,
                    analysis.WorkingDirectory ?? ".");
                var server = new McpServerProfileConfiguration
                {
                    McpServerId = mcpId,
                    DisplayName = displayName,
                    Source = new SkillSourceConfiguration
                    {
                        Kind = "git",
                        Locator = source.AbsoluteUri,
                        ContentDigest = install.ContentDigest,
                    },
                    Risk = analysis.Risk,
                    ImportStatus = "review_required",
                    InstallDirectory = install.InstallDirectory,
                    ContentDigest = install.ContentDigest,
                    AuditSummary = analysis.AuditSummary,
                    AuditedAt = DateTimeOffset.UtcNow,
                    Transport = "stdio",
                    Command = analysis.Command,
                    Arguments = analysis.Arguments,
                    WorkingDirectory = workingDirectory,
                    Enabled = false,
                };
                ReplaceMcpServer(server);
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                RefreshCapabilityGrants();
                RefreshInvitationCapabilities();
                McpDisplayName = string.Empty;
                McpSourceLocator = string.Empty;
                ErrorMessage = string.Empty;
                StatusText = $"MCP {server.DisplayName} 已隔离安装，等待人工审核启用";
            }
            catch (Exception error) when (HandleCatalogImportError(error, cancellationToken))
            {
                // The exception filter reports a sanitized, actionable error.
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
            ToolCatalog = ParseMcpToolCatalog(McpToolCatalogText),
            Risk = "medium",
            ImportStatus = "review_required",
            AuditSummary = "手动登记，尚未经过 LLM 仓库审阅；必须由用户明确批准后才会进入角色能力列表。",
            AuditedAt = DateTimeOffset.UtcNow,
            Enabled = false,
        };
        string? normalizedEndpoint = null;
        if (server.Transport != "stdio" && !TryNormalizeEndpoint(server.Endpoint ?? string.Empty, out normalizedEndpoint))
        {
            ShowError("远端 MCP 端点必须使用无凭据、查询或片段的 HTTPS 或本机回环 HTTP。");
            return;
        }
        if (server.Transport != "stdio")
        {
            server.Endpoint = normalizedEndpoint;
        }
        ReplaceMcpServer(server);
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        RefreshCapabilityGrants();
        RefreshInvitationCapabilities();
        McpDisplayName = string.Empty;
        McpCommandOrEndpoint = string.Empty;
        McpToolCatalogText = string.Empty;
        StatusText = "MCP 已登记并保持禁用，等待人工审核";
    }

    public string GetMcpToolCatalogText(string mcpServerId)
    {
        var server = McpServers.FirstOrDefault(item => item.McpServerId == mcpServerId);
        return server is null
            ? string.Empty
            : string.Join(Environment.NewLine, server.ToolCatalog
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(tool => tool.Name));
    }

    public async Task UpdateMcpToolCatalogAsync(
        string mcpServerId,
        string toolNames,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先暂停或结束当前会话，再修改 MCP 工具清单。");
            return;
        }
        var server = McpServers.FirstOrDefault(item => item.McpServerId == mcpServerId);
        if (server is null)
        {
            ShowError("找不到要编辑的 MCP 服务器。");
            return;
        }
        var reviewedTools = ParseMcpToolCatalog(toolNames);
        server.ToolCatalog = reviewedTools;
        var reviewedNames = reviewedTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var role in Roles.Concat(LongTermRoles).Distinct())
        {
            if (!role.McpServerIds.Contains(server.McpServerId))
            {
                continue;
            }
            role.SetMcpGrant(
                server.McpServerId,
                role.GetMcpToolAllowlist(server.McpServerId).Where(reviewedNames.Contains));
        }
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        await PersistSelectedSessionAsync(cancellationToken);
        RefreshCapabilityGrants();
        RefreshInvitationCapabilities();
        ErrorMessage = string.Empty;
        StatusText = reviewedTools.Count == 0
            ? $"{server.DisplayName} 当前授权零工具"
            : $"{server.DisplayName} 已复核 {reviewedTools.Count} 个工具身份";
    }

    public async Task ApproveSkillAsync(string skillId, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再批准 Skill。");
            return;
        }
        var skill = Skills.FirstOrDefault(item => item.SkillId == skillId);
        if (skill is null || skill.InstallDirectory is null || skill.Source.ContentDigest is null)
        {
            ShowError("该 Skill 没有可验证的本地安装，无法批准。");
            return;
        }
        skill.Enabled = true;
        skill.ImportStatus = "installed";
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        RefreshCapabilityGrants();
        RefreshInvitationCapabilities();
        StatusText = $"已批准 Skill {skill.DisplayName}";
    }

    public async Task ApproveMcpAsync(string mcpServerId, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsRunning || IsBusy)
        {
            ShowError("请先结束当前会话，再批准 MCP。");
            return;
        }
        var server = McpServers.FirstOrDefault(item => item.McpServerId == mcpServerId);
        if (server is null)
        {
            ShowError("找不到要批准的 MCP 条目。");
            return;
        }
        server.Enabled = true;
        server.ImportStatus = server.InstallDirectory is null ? "registered" : "installed";
        SynchronizeWorkspaceConfiguration();
        await _workspaceStore.SaveAsync(_workspace, cancellationToken);
        RefreshCapabilityGrants();
        RefreshInvitationCapabilities();
        StatusText = $"已批准 MCP {server.DisplayName}";
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
            ShowError("远端同步服务器必须使用无凭据、查询或片段的 HTTPS 或本机回环 HTTP。");
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
            IRuntimeHostProcess? runtime = null;
            try
            {
                SynchronizeWorkspaceConfiguration();
                await _workspaceStore.SaveAsync(_workspace, cancellationToken);
                var credentials = await ResolveSessionCredentialsAsync(activeRoles, cancellationToken);
                var meetingId = SelectedSession.SessionId;
                var checkpoint = await _eventStore.GetCheckpointAsync(meetingId, cancellationToken);
                if (checkpoint?.IsClosed == true)
                {
                    ShowError("该会议已经结束，不能恢复；请新建会议继续讨论。");
                    StatusText = "会议已结束";
                    return;
                }
                IReadOnlyList<RuntimeMeetingEvent> historicalEvents = checkpoint is null
                    ? []
                    : await _eventStore.LoadEventsAsync(meetingId, cancellationToken);
                ValidateRecoveryHistory(checkpoint, historicalEvents);
                var isRecovery = historicalEvents.Any(meetingEvent => meetingEvent.Kind == "meeting.opened");

                _meetingCore?.Dispose();
                var meetingCore = _meetingCoreFactory.Create();
                _meetingCore = meetingCore;
                var unsupportedEvent = historicalEvents.FirstOrDefault(
                    meetingEvent => !meetingCore.SupportsEventKind(meetingEvent.Kind));
                if (unsupportedEvent is not null)
                {
                    var supportedPrefix = historicalEvents
                        .TakeWhile(meetingEvent => meetingCore.SupportsEventKind(meetingEvent.Kind))
                        .ToArray();
                    foreach (var historicalEvent in supportedPrefix)
                    {
                        meetingCore.Apply(historicalEvent);
                    }
                    _sequence = checkpoint?.LastSequence ?? unsupportedEvent.Sequence;
                    _runtimeGeneration = checkpoint?.RuntimeGeneration ?? unsupportedEvent.RuntimeGeneration;
                    _eventStreamFaulted = true;
                    if (supportedPrefix.Length > 0)
                    {
                        RebuildProjectionFromEvents(supportedPrefix);
                    }
                    meetingCore.Dispose();
                    _meetingCore = null;
                    ShowError(
                        $"本地历史包含当前客户端尚不支持的事件 {unsupportedEvent.Kind}（序号 {unsupportedEvent.Sequence}）。游标已保留，请升级客户端后恢复会议。");
                    StatusText = "会议需要升级客户端后恢复";
                    return;
                }
                foreach (var historicalEvent in historicalEvents)
                {
                    meetingCore.Apply(historicalEvent);
                }
                _sequence = checkpoint?.LastSequence ?? 0;
                _runtimeGeneration = checkpoint?.RuntimeGeneration ?? 0;
                _eventStreamFaulted = false;
                if (historicalEvents.Count > 0)
                {
                    RebuildProjectionFromEvents(historicalEvents);
                }

                var sessionDefinition = BuildSessionConfiguration(
                    SelectedSession,
                    activeRoles,
                    isRecovery ? "live" : "draft");
                await _sessionStore.SaveAsync(sessionDefinition, cancellationToken);
                var nextGeneration = checked(_runtimeGeneration + 1);
                _runtimeGeneration = nextGeneration;
                await _eventQueue.ResetAsync(nextGeneration, _sequence);
                // Create the process only after every preflight gate that can
                // return without entering runtime ownership has succeeded.
                runtime = _runtimeHostFactory.Create(_eventStore);
                _startingRuntime = runtime;
                runtime.MeetingEventReceived += OnMeetingEventReceived;
                runtime.DiagnosticReceived += OnDiagnosticReceived;
                runtime.EventStreamFaulted += OnEventStreamFaulted;
                await runtime.StartAsync(
                    new RuntimeHostStartOptions(
                        meetingId,
                        $"runtime-windows-{Environment.ProcessId}",
                        nextGeneration,
                        _sequence,
                        _workspace,
                        sessionDefinition,
                        credentials,
                        isRecovery && _discussionState.Configured
                            ? CloneDiscussionState(_discussionState)
                            : null),
                    cancellationToken);
                if (_disposed)
                {
                    _startingRuntime = null;
                    try
                    {
                        await DisposeRuntimeAndDrainEventsAsync(runtime);
                    }
                    catch
                    {
                        runtime.Terminate();
                    }
                    finally
                    {
                        _meetingCore?.Dispose();
                        _meetingCore = null;
                    }
                    return;
                }
                _runtime = runtime;
                _startingRuntime = null;

                if (!isRecovery)
                {
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
                }
                if (!_discussionState.Configured)
                {
                    var agendaItems = new[] { sessionDefinition.Agenda.Subject }
                        .Concat(sessionDefinition.Agenda.Objectives)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    await EnsureAcceptedAsync(runtime.SendCommandAsync(
                        "discussion.configure",
                        "user.direct_host",
                        null,
                        new Dictionary<string, object?>
                        {
                            ["agendaItems"] = agendaItems,
                            ["limits"] = BuildDefaultDiscussionLimitsPayload(),
                        },
                        cancellationToken));
                }
                IsRunning = true;
                StatusText = isRecovery ? "会议已从本地检查点恢复" : "本地会议运行中";
                if (SelectedSession is not null)
                {
                    SelectedSession.Phase = "live";
                    SelectedSession.UpdatedAt = DateTimeOffset.Now;
                    NotifyLifecycleProperties();
                    await PersistSelectedSessionAsync(cancellationToken);
                }
            }
            catch
            {
                _runtime = null;
                _startingRuntime = null;
                try
                {
                    if (runtime is not null)
                    {
                        await DisposeRuntimeAndDrainEventsAsync(runtime);
                    }
                }
                catch
                {
                    runtime?.Terminate();
                }
                finally
                {
                    _meetingCore?.Dispose();
                    _meetingCore = null;
                    ShowError("启动失败：请检查 Runtime Host、角色模型路由和 Credential Manager 中的提供商凭据。");
                    StatusText = "启动失败";
                }
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

    private static void ValidateRecoveryHistory(
        MeetingStoreCheckpoint? checkpoint,
        IReadOnlyList<RuntimeMeetingEvent> historicalEvents)
    {
        if (checkpoint is null)
        {
            if (historicalEvents.Count != 0)
            {
                throw new InvalidDataException("会议事件存在，但恢复检查点缺失。");
            }
            return;
        }

        if (historicalEvents.Count == 0)
        {
            if (checkpoint.LastSequence != 0 || checkpoint.RuntimeGeneration != 0)
            {
                throw new InvalidDataException("会议恢复检查点与空事件历史不一致。");
            }
            return;
        }

        var tail = historicalEvents[^1];
        if (tail.Sequence != checkpoint.LastSequence ||
            tail.RuntimeGeneration != checkpoint.RuntimeGeneration)
        {
            throw new InvalidDataException("会议恢复检查点与事件历史末尾不一致。");
        }
    }

    public async Task<bool> SendPromptAsync(string message, CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        if (_isSendingPrompt)
        {
            return false;
        }
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
        var normalizedMessage = message.Trim();
        var parsedMentions = RoleMentionParser.Parse(normalizedMessage, Roles);
        if (parsedMentions.UnknownMentions.Count > 0)
        {
            ShowError($"未找到点名角色：{string.Join("、", parsedMentions.UnknownMentions.Select(name => $"@{name}"))}。请检查正文中的角色名。");
            return false;
        }
        if (parsedMentions.AmbiguousMentions.Count > 0)
        {
            ShowError($"角色名不唯一：{string.Join("、", parsedMentions.AmbiguousMentions.Select(name => $"@{name}"))}。请先为重名角色设置不同名称。");
            return false;
        }
        var mentions = parsedMentions.RoleIds.ToArray();
        _isSendingPrompt = true;
        StatusText = "正在发送公开发言";
        NotifySummary();
        try
        {
            var receipt = await runtime.SendCommandAsync(
                "speech.broadcast",
                "user.direct_host",
                null,
                new Dictionary<string, object?>
                {
                    ["message"] = normalizedMessage,
                    ["mentions"] = mentions,
                },
                cancellationToken);
            if (!receipt.Accepted)
            {
                StatusText = "公开发言发送失败";
                ShowReceiptError(receipt);
                return false;
            }
            StatusText = mentions.Length == 0
                ? "公开发言已发送；全部活跃角色参与本轮回应"
                : $"公开发言已发送；已自动安排 {mentions.Length} 个被点名角色回应";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "公开发言发送已取消";
            throw;
        }
        catch
        {
            StatusText = "公开发言发送失败";
            throw;
        }
        finally
        {
            _isSendingPrompt = false;
            NotifySummary();
        }
    }

    public async Task<bool> RetryTranscriptAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        var item = Transcript.FirstOrDefault(candidate => candidate.MessageId == messageId);
        var role = item is null
            ? null
            : Roles.FirstOrDefault(candidate => candidate.RoleId == item.RoleId && !candidate.IsArchived);
        if (runtime is null || !IsRunning || item is null || role is null ||
            !item.CanRetry || string.IsNullOrWhiteSpace(item.RetryPrompt))
        {
            ShowError("该回合当前不能重试；请确认会议仍在运行且角色仍然有效。");
            return false;
        }

        item.CanRetry = false;
        var priorState = item.State;
        item.State = "正在重新排队";
        var receipt = await runtime.SendCommandAsync(
            "speech.broadcast",
            "user.direct_host",
            null,
            new Dictionary<string, object?>
            {
                ["message"] = item.RetryPrompt,
                ["mentions"] = new[] { role.RoleId },
            },
            cancellationToken);
        if (!receipt.Accepted)
        {
            item.State = priorState;
            item.CanRetry = true;
            ShowReceiptError(receipt);
            return false;
        }
        item.State = "已重新排队";
        ErrorMessage = string.Empty;
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

    public Task SetDiscussionModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (mode is not ("agenda" or "free_discussion" or "convergence" or "paused"))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return SendDiscussionCommandAsync(
            "discussion.mode.set",
            new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["reason"] = "host_control",
            },
            cancellationToken);
    }

    public Task ResumeDiscussionAsync(CancellationToken cancellationToken = default) =>
        SendDiscussionCommandAsync(
            "discussion.resume",
            new Dictionary<string, object?> { ["reason"] = "host_resume" },
            cancellationToken);

    public Task AdvanceAgendaAsync(CancellationToken cancellationToken = default) =>
        SendDiscussionCommandAsync(
            "agenda.advance",
            new Dictionary<string, object?> { ["reason"] = "host_advanced" },
            cancellationToken);

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
            new Dictionary<string, object?>
            {
                ["message"] = message.Trim(),
                ["hostAuthorized"] = true,
            },
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

    private async Task SendDiscussionCommandAsync(
        string kind,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var runtime = _runtime;
        if (runtime is null || !CanOperate || !_discussionState.Configured)
        {
            ShowError("请先启动并配置自动主持。 ");
            return;
        }
        var receipt = await runtime.SendCommandAsync(
            kind,
            "user.direct_host",
            null,
            payload,
            cancellationToken);
        if (!receipt.Accepted)
        {
            ShowReceiptError(receipt);
            return;
        }
        ErrorMessage = string.Empty;
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

    public async Task ResolveToolApprovalAsync(
        string approvalId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        var runtime = _runtime;
        var approval = PendingToolApprovals.FirstOrDefault(item => item.ApprovalId == approvalId);
        if (runtime is null || approval is null || approval.IsResolving)
        {
            return;
        }
        approval.IsResolving = true;
        try
        {
            var receipt = await runtime.SendCommandAsync(
                "tool.approval.resolve",
                "user.direct_host",
                approval.RoleId,
                new Dictionary<string, object?>
                {
                    ["approvalId"] = approval.ApprovalId,
                    ["approved"] = approved,
                },
                cancellationToken);
            if (!receipt.Accepted)
            {
                approval.IsResolving = false;
                ShowReceiptError(receipt);
            }
        }
        catch
        {
            approval.IsResolving = false;
            throw;
        }
    }

    public void RefreshToolApprovalDeadlines(DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        foreach (var approval in PendingToolApprovals)
        {
            approval.RefreshDeadline(effectiveNow);
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
        foreach (var serverId in InvitationCapabilities
                     .Where(grant => grant.Kind == "MCP" && grant.IsGranted)
                     .Select(grant => grant.CapabilityId))
        {
            role.SetMcpGrant(
                serverId,
                InvitationMcpTools
                    .Where(tool => tool.ServerId == serverId && tool.IsGranted)
                    .Select(tool => tool.ToolName));
        }
        Roles.Add(role);
        SelectedSession?.TemporaryRoles.Add(role);
        SelectedRole = role;
        SelectedPrivateRole = role;
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
        InvitationMcpTools.Clear();
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
            RemoveArchivedRoleFromActiveViews(role);
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

    private void RemoveArchivedRoleFromActiveViews(RoleItem role)
    {
        Roles.Remove(role);
        LongTermRoles.Remove(role);
        SelectedSession?.TemporaryRoles.Remove(role);
        if (ReferenceEquals(SelectedRole, role))
        {
            SelectedRole = Roles.FirstOrDefault();
        }
        if (ReferenceEquals(SelectedPrivateRole, role))
        {
            SelectedPrivateRole = Roles.FirstOrDefault();
        }
    }

    public async Task SuspendMeetingAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var runtime = _runtime;
            _runtime = null;
            var startingRuntime = _startingRuntime;
            _startingRuntime = null;
            IsRunning = false;
            if (runtime is null)
            {
                try
                {
                    if (startingRuntime is not null)
                    {
                        startingRuntime.Terminate();
                        await DisposeRuntimeAndDrainEventsAsync(startingRuntime);
                    }
                    else
                    {
                        await WaitForEventQueueAsync();
                    }
                }
                finally
                {
                    _meetingCore?.Dispose();
                    _meetingCore = null;
                    await PersistSelectedSessionAsync(CancellationToken.None);
                }
                return;
            }

            IsBusy = true;
            try
            {
                await runtime.StopAsync(RuntimeHostShutdownMode.Suspend, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                ShowError($"暂停会议时出现问题：{error.Message}");
                runtime.Terminate();
            }
            finally
            {
                try
                {
                    await DisposeRuntimeAndDrainEventsAsync(runtime);
                }
                finally
                {
                    _meetingCore?.Dispose();
                    _meetingCore = null;
                    _streamingMessages.Clear();
                    _privateStreamingMessages.Clear();
                    IsBusy = false;
                    StatusText = _eventStreamFaulted ? "会议因事件故障暂停" : "会议已暂停，可从本地检查点恢复";
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
                try
                {
                    if (startingRuntime is not null)
                    {
                        startingRuntime.Terminate();
                        await DisposeRuntimeAndDrainEventsAsync(startingRuntime);
                    }
                    else
                    {
                        await WaitForEventQueueAsync();
                    }
                }
                finally
                {
                    _meetingCore?.Dispose();
                    _meetingCore = null;
                    await PersistSelectedSessionAsync(CancellationToken.None);
                }
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
                try
                {
                    await runtime.StopAsync(RuntimeHostShutdownMode.Close, CancellationToken.None);
                }
                catch
                {
                    runtime.Terminate();
                }
                try
                {
                    await DisposeRuntimeAndDrainEventsAsync(runtime);
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
                        NotifyLifecycleProperties();
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
        await SuspendMeetingAsync();
        await PersistSelectedSessionAsync(CancellationToken.None);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _disposed = true;
        await SuspendMeetingAsync(cancellationToken);
    }

    public void TerminateRuntimeForAppExit()
    {
        _runtime?.Terminate();
        _startingRuntime?.Terminate();
    }

    private void OnMeetingEventReceived(object? sender, RuntimeMeetingEvent meetingEvent)
    {
        _eventQueue.Enqueue(meetingEvent);
    }

    private void OnDiagnosticReceived(object? sender, string message)
    {
        _dispatcher.TryEnqueue(() => ShowError(message));
    }

    private void OnEventStreamFaulted(object? sender, string message)
    {
        _ = ReportEventStreamFaultAsync(message);
    }

    private async Task AcceptMeetingEventAsync(RuntimeMeetingEvent meetingEvent)
    {
        if (_meetingCore is not null && !_meetingCore.SupportsEventKind(meetingEvent.Kind))
        {
            await _eventStore.AppendAsync(meetingEvent, CancellationToken.None);
            await DispatchAsync(() => _sequence = meetingEvent.Sequence);
            throw new InvalidOperationException(
                $"Runtime Host 返回了当前客户端尚不支持的事件 {meetingEvent.Kind}（序号 {meetingEvent.Sequence}）。游标已保留，会议已安全暂停；请升级客户端后恢复。");
        }
        _meetingCore?.Apply(meetingEvent);
        await _eventStore.AppendAsync(meetingEvent, CancellationToken.None);

        await DispatchAsync(() =>
        {
            _sequence = meetingEvent.Sequence;
            ProjectMeetingEvent(meetingEvent);
        });
    }

    private void ProjectMeetingEvent(RuntimeMeetingEvent meetingEvent, bool isReplay = false)
    {
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
            case "discussion.configured":
                if (TryReadDiscussionState(meetingEvent.Payload, out var configuredDiscussion))
                {
                    _discussionState = configuredDiscussion;
                    StatusText = "自动主持已就绪";
                }
                else
                {
                    ShowError("自动主持状态无效；已停止使用该状态，避免错误恢复。");
                    _discussionState = new DiscussionSchedulerStateConfiguration();
                }
                break;
            case "discussion.mode_changed":
                if (TryReadString(meetingEvent.Payload, "mode", out var mode))
                {
                    var previousMode = TryReadString(meetingEvent.Payload, "previousMode", out var previous)
                        ? previous
                        : _discussionState.Mode;
                    _discussionState.Mode = mode;
                    if (mode == "paused")
                    {
                        if (previousMode is "agenda" or "free_discussion" or "convergence")
                        {
                            _discussionState.ResumeMode = previousMode;
                        }
                        _discussionState.PauseReason = TryReadString(
                            meetingEvent.Payload,
                            "reason",
                            out var pauseReason)
                                ? pauseReason
                                : "host_control";
                    }
                    else
                    {
                        if (mode is "agenda" or "free_discussion" or "convergence")
                        {
                            _discussionState.ResumeMode = mode;
                        }
                        _discussionState.PauseReason = null;
                    }
                    UpdateDiscussionCounters(meetingEvent.Payload);
                    StatusText = mode switch
                    {
                        "agenda" => "自动主持：议程模式",
                        "free_discussion" => "自动主持：自由讨论",
                        "convergence" => "自动主持正在收敛",
                        "paused" => "自动主持已暂停，等待你的决定",
                        "completed" => "讨论已完成",
                        _ => StatusText,
                    };
                }
                break;
            case "agenda.item_changed":
                ProjectAgendaItem(meetingEvent.Payload);
                break;
            case "floor.requested":
                ProjectFloorRequest(meetingEvent);
                break;
            case "floor.granted":
            case "floor.rejected":
                if (TryReadString(meetingEvent.Payload, "requestId", out var terminalRequestId))
                {
                    _discussionState.PendingRequests.RemoveAll(item =>
                        item.RequestId == terminalRequestId);
                }
                break;
            case "discussion.budget_updated":
                UpdateDiscussionCounters(meetingEvent.Payload);
                break;
            case "convergence.recorded":
                StatusText = meetingEvent.Payload.TryGetProperty("complete", out var complete) &&
                    complete.ValueKind == JsonValueKind.True
                        ? "收敛结果已记录"
                        : "收敛结果已更新";
                break;
            case "message.published":
                if (meetingEvent.Payload.TryGetProperty("message", out var publicMessage))
                {
                    if (!string.IsNullOrWhiteSpace(meetingEvent.CausationId))
                    {
                        _publicPromptsByCommandId[meetingEvent.CausationId] =
                            publicMessage.GetString() ?? string.Empty;
                    }
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
                    RemoveArchivedRoleFromActiveViews(role);
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
                        meetingEvent.OccurredAt,
                        ResolveRetryPrompt(meetingEvent.CausationId));
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
                        if (meetingEvent.Kind == "speech.completed")
                        {
                            finished.State = "已完成";
                        }
                        else
                        {
                            var failed = meetingEvent.Payload.TryGetProperty("reason", out var reason) &&
                                reason.GetString() == "failed";
                            finished.State = failed ? "失败 · 可重试" : "已取消 · 可重试";
                            finished.CanRetry = finished.Visibility == "public" &&
                                !string.IsNullOrWhiteSpace(finished.RetryPrompt);
                        }
                    }
                }
                if (!isReplay)
                {
                    PersistCurrentSessionInBackground();
                }
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
            case "tool.approval_requested":
                if (role is not null &&
                    meetingEvent.Payload.TryGetProperty("approvalId", out var approvalIdElement) &&
                    approvalIdElement.GetString() is { Length: > 0 } approvalId &&
                    PendingToolApprovals.All(item => item.ApprovalId != approvalId))
                {
                    var serverName = meetingEvent.Payload.TryGetProperty("serverDisplayName", out var server)
                        ? server.GetString() ?? "MCP"
                        : "MCP";
                    var toolName = meetingEvent.Payload.TryGetProperty("toolLabel", out var toolLabel)
                        ? toolLabel.GetString() ?? "工具"
                        : "工具";
                    var expiresAt = meetingEvent.Payload.TryGetProperty("expiresAt", out var expiresAtElement) &&
                        expiresAtElement.GetString() is { Length: > 0 } expiresAtText &&
                        DateTimeOffset.TryParse(expiresAtText, out var parsedExpiresAt)
                            ? parsedExpiresAt
                            : meetingEvent.OccurredAt.AddMinutes(2);
                    PendingToolApprovals.Add(new ToolApprovalItem(
                        approvalId,
                        role.RoleId,
                        role.DisplayName,
                        serverName,
                        toolName,
                        meetingEvent.OccurredAt,
                        expiresAt));
                    role.ActivitySummary = $"等待你审批 {serverName} · {toolName}；到期自动拒绝，未显示参数或结果";
                    NotifyToolApprovals();
                }
                break;
            case "tool.approval_resolved":
                if (meetingEvent.Payload.TryGetProperty("approvalId", out var resolvedIdElement) &&
                    resolvedIdElement.GetString() is { Length: > 0 } resolvedId)
                {
                    var resolved = PendingToolApprovals.FirstOrDefault(item => item.ApprovalId == resolvedId);
                    if (resolved is not null)
                    {
                        PendingToolApprovals.Remove(resolved);
                        NotifyToolApprovals();
                    }
                    if (role is not null)
                    {
                        var approved = meetingEvent.Payload.TryGetProperty("approved", out var approvedElement) &&
                            approvedElement.ValueKind == System.Text.Json.JsonValueKind.True;
                        var expired = meetingEvent.Payload.TryGetProperty("reason", out var reasonElement) &&
                            reasonElement.GetString() == "expired";
                        role.ActivitySummary = approved
                            ? "工具调用已获批准；未显示参数或结果"
                            : expired
                                ? "工具审批已到期并由 Runtime 自动拒绝；没有执行外部副作用"
                                : "工具调用已拒绝；没有执行外部副作用";
                    }
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
            case "subagent.spawned":
                if (role is not null &&
                    meetingEvent.Payload.TryGetProperty("subagentId", out var spawnedIdElement) &&
                    spawnedIdElement.GetString() is { Length: > 0 } spawnedId &&
                    SubagentRuns.All(item => item.SubagentId != spawnedId))
                {
                    TrimCompletedSubagentRuns();
                    SubagentRuns.Insert(0, new SubagentRunItem(
                        spawnedId,
                        role.RoleId,
                        role.DisplayName,
                        meetingEvent.OccurredAt));
                    role.ActivitySummary = "SubAgent 正在执行受限任务；任务正文和结果仅对父角色可见";
                    NotifyActivityPanel();
                }
                break;
            case "subagent.progress":
                if (meetingEvent.Payload.TryGetProperty("subagentId", out var progressIdElement) &&
                    progressIdElement.GetString() is { Length: > 0 } progressId)
                {
                    var run = SubagentRuns.FirstOrDefault(item => item.SubagentId == progressId);
                    if (run is not null &&
                        meetingEvent.Payload.TryGetProperty("updateCount", out var updateCountElement) &&
                        updateCountElement.TryGetInt32(out var updateCount))
                    {
                        run.UpdateCount = updateCount;
                    }
                }
                break;
            case "subagent.completed":
            case "subagent.failed":
                if (meetingEvent.Payload.TryGetProperty("subagentId", out var terminalIdElement) &&
                    terminalIdElement.GetString() is { Length: > 0 } terminalId)
                {
                    var run = SubagentRuns.FirstOrDefault(item => item.SubagentId == terminalId);
                    if (run is not null)
                    {
                        run.Status = meetingEvent.Kind == "subagent.completed" ? "已完成" : "失败";
                    }
                    if (role is not null)
                    {
                        role.ActivitySummary = meetingEvent.Kind == "subagent.completed"
                            ? "SubAgent 已完成，结果已私下交回父角色等待续答"
                            : "SubAgent 执行失败，父角色将收到不编造结果的继续指令";
                    }
                    NotifyActivityPanel();
                }
                break;
            case "interruption.requested":
                var interruptorName = role?.DisplayName ?? meetingEvent.ActorId ?? "未知角色";
                var targetName = Roles.FirstOrDefault(item => item.RoleId == meetingEvent.TargetId)?.DisplayName
                    ?? meetingEvent.TargetId
                    ?? "当前发言者";
                var interruptionMessage = meetingEvent.Payload.TryGetProperty("message", out var interruptionMessageElement)
                    ? interruptionMessageElement.GetString()?.Trim()
                    : null;
                if (role is not null)
                {
                    role.Status = "等待接管";
                    role.ActivitySummary = $"已打断 {targetName}，等待取消完成后接管公开发言";
                }
                var interruptedRole = Roles.FirstOrDefault(item => item.RoleId == meetingEvent.TargetId);
                if (interruptedRole is not null)
                {
                    interruptedRole.ActivitySummary = $"发言被 {interruptorName} 打断，正在停止生成";
                }
                Transcript.Add(new TranscriptItem(
                    "system",
                    "会议控制",
                    string.IsNullOrWhiteSpace(interruptionMessage)
                        ? $"{interruptorName} 打断 {targetName}，正在交接发言权。"
                        : $"{interruptorName} 打断 {targetName}：{interruptionMessage}",
                    "处理中"));
                break;
        }
        NotifySummary();
    }

    private void RebuildProjectionFromEvents(IReadOnlyList<RuntimeMeetingEvent> events)
    {
        if (SelectedSession is null)
        {
            return;
        }
        SelectedSession.Transcript.Clear();
        SelectedSession.PrivateThreads.Clear();
        _streamingMessages.Clear();
        _privateStreamingMessages.Clear();
        _publicPromptsByCommandId.Clear();
        _discussionState = new DiscussionSchedulerStateConfiguration();
        PendingToolApprovals.Clear();
        SubagentRuns.Clear();
        foreach (var role in Roles.Where(role => !role.IsArchived))
        {
            role.Status = "未连接";
            role.ActivitySummary = "等待 Runtime Owner";
        }
        foreach (var meetingEvent in events)
        {
            ProjectMeetingEvent(meetingEvent, isReplay: true);
        }
        var staleApprovalRoleIds = PendingToolApprovals.Select(item => item.RoleId).Distinct().ToArray();
        if (PendingToolApprovals.Count > 0)
        {
            PendingToolApprovals.Clear();
            foreach (var roleId in staleApprovalRoleIds)
            {
                var role = Roles.FirstOrDefault(item => item.RoleId == roleId);
                if (role is not null)
                {
                    role.ActivitySummary = "上次工具审批因 Runtime 中断而失效；外部副作用保持未执行";
                }
            }
        }
        foreach (var run in SubagentRuns.Where(item => item.IsActive))
        {
            run.Status = "已中断";
            var role = Roles.FirstOrDefault(item => item.RoleId == run.ParentRoleId);
            if (role is not null)
            {
                role.ActivitySummary = "上次 SubAgent 随 Runtime 中断；需要父角色按当前上下文重新委派";
            }
        }
        OnPropertyChanged(nameof(Transcript));
        OnPropertyChanged(nameof(PrivateMessages));
        NotifyActivityPanel();
    }

    private Task DispatchAsync(Action callback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    callback();
                    completion.TrySetResult();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("Windows UI 调度器已停止。"));
        }
        return completion.Task;
    }

    private async Task ReportEventStreamFaultAsync(string message)
    {
        _eventStreamFaulted = true;
        _runtime?.Terminate();
        try
        {
            await DispatchAsync(() =>
            {
                StatusText = "事件流已中断";
                ShowError(message);
                OnPropertyChanged(nameof(CanOperate));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanSendPrivate));
                OnPropertyChanged(nameof(CanControlDiscussion));
                _ = SuspendAfterStreamFaultAsync();
            });
        }
        catch
        {
            // Process termination is the final safety boundary if the UI is already gone.
        }
    }

    private void RefreshCapabilityGrants()
    {
        AvailableCapabilities.Clear();
        AvailableMcpTools.Clear();
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
        RefreshMcpToolGrants();
    }

    private void RefreshInvitationCapabilities()
    {
        InvitationCapabilities.Clear();
        InvitationMcpTools.Clear();
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
            var grant = new CapabilityGrantItem(
                server.McpServerId,
                server.DisplayName,
                "MCP",
                false);
            grant.PropertyChanged += OnInvitationCapabilityChanged;
            InvitationCapabilities.Add(grant);
        }
    }

    private void RefreshMcpToolGrants()
    {
        AvailableMcpTools.Clear();
        var role = SelectedRole;
        if (role is null)
        {
            return;
        }
        foreach (var server in McpServers.Where(server =>
                     server.Enabled && role.McpServerIds.Contains(server.McpServerId)))
        {
            var allowlist = role.GetMcpToolAllowlist(server.McpServerId);
            foreach (var tool in server.ToolCatalog.OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                var item = new McpToolGrantItem(
                    server.McpServerId,
                    server.DisplayName,
                    tool.Name,
                    tool.DisplayName,
                    tool.Description,
                    allowlist.Contains(tool.Name, StringComparer.Ordinal));
                item.PropertyChanged += OnMcpToolGrantChanged;
                AvailableMcpTools.Add(item);
            }
        }
    }

    private void RefreshInvitationMcpTools()
    {
        var selected = InvitationMcpTools
            .Where(item => item.IsGranted)
            .Select(item => $"{item.ServerId}\0{item.ToolName}")
            .ToHashSet(StringComparer.Ordinal);
        InvitationMcpTools.Clear();
        var grantedServers = InvitationCapabilities
            .Where(item => item.Kind == "MCP" && item.IsGranted)
            .Select(item => item.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var server in McpServers.Where(server =>
                     server.Enabled && grantedServers.Contains(server.McpServerId)))
        {
            foreach (var tool in server.ToolCatalog.OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                InvitationMcpTools.Add(new McpToolGrantItem(
                    server.McpServerId,
                    server.DisplayName,
                    tool.Name,
                    tool.DisplayName,
                    tool.Description,
                    selected.Contains($"{server.McpServerId}\0{tool.Name}")));
            }
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
        if (grant.Kind == "Skill")
        {
            if (grant.IsGranted)
            {
                role.SkillIds.Add(grant.CapabilityId);
            }
            else
            {
                role.SkillIds.Remove(grant.CapabilityId);
            }
            role.NotifyCapabilitiesChanged();
            return;
        }
        if (grant.IsGranted)
        {
            role.SetMcpGrant(grant.CapabilityId, []);
        }
        else
        {
            role.RemoveMcpGrant(grant.CapabilityId);
        }
        RefreshMcpToolGrants();
    }

    private void OnMcpToolGrantChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(McpToolGrantItem.IsGranted) ||
            sender is not McpToolGrantItem tool ||
            SelectedRole is not { } role ||
            !role.McpServerIds.Contains(tool.ServerId))
        {
            return;
        }
        var allowlist = role.GetMcpToolAllowlist(tool.ServerId).ToHashSet(StringComparer.Ordinal);
        if (tool.IsGranted)
        {
            allowlist.Add(tool.ToolName);
        }
        else
        {
            allowlist.Remove(tool.ToolName);
        }
        role.SetMcpGrant(tool.ServerId, allowlist);
    }

    private void OnInvitationCapabilityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CapabilityGrantItem.IsGranted) &&
            sender is CapabilityGrantItem { Kind: "MCP" })
        {
            RefreshInvitationMcpTools();
        }
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
                    FallbackModelProfileIds = [.. role.FallbackModelProfileIds],
                    ThinkingLevel = role.ThinkingLevel,
                    MaxOutputTokens = role.MaxOutputTokens,
                },
                Capabilities = new CapabilityPolicyConfiguration
                {
                    SkillIds = role.SkillIds.Order(StringComparer.Ordinal).ToList(),
                    McpGrants = role.McpServerIds
                        .Order(StringComparer.Ordinal)
                        .Select(id => new McpGrantConfiguration
                        {
                            McpServerId = id,
                            ToolAllowlist = role.GetMcpToolAllowlist(id)
                                .Order(StringComparer.Ordinal)
                                .ToList(),
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
            var secret = EphemeralE2eCredentialPipe.CanResolve(provider.CredentialRef)
                ? await EphemeralE2eCredentialPipe.ReadOnceAsync(provider.CredentialRef, cancellationToken)
                : await _credentialStore.ReadAsync(provider.CredentialRef, cancellationToken);
            if (string.IsNullOrEmpty(secret))
            {
                throw new InvalidOperationException($"提供商 {provider.DisplayName} 缺少凭据。");
            }
            credentials.Add(provider.CredentialRef, secret);
        }
        foreach (var server in roles
                     .SelectMany(role => role.McpServerIds)
                     .Distinct(StringComparer.Ordinal)
                     .Select(serverId => McpServers.FirstOrDefault(server =>
                         server.McpServerId == serverId && server.Enabled))
                     .OfType<McpServerProfileConfiguration>())
        {
            var references = (server.EnvironmentCredentialRefs ?? [])
                .Concat(server.HeaderCredentialRefs ?? [])
                .Select(pair => pair.Value)
                .Distinct(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                if (credentials.ContainsKey(reference))
                {
                    continue;
                }
                var secret = await _credentialStore.ReadAsync(reference, cancellationToken);
                if (string.IsNullOrEmpty(secret))
                {
                    throw new InvalidOperationException($"MCP {server.DisplayName} 缺少安全存储凭据。");
                }
                credentials.Add(reference, secret);
            }
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

    internal static ParticipantManifestConfiguration BuildParticipantManifest(RoleItem role)
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
                FallbackModelProfileIds = [.. role.FallbackModelProfileIds],
                ThinkingLevel = role.ThinkingLevel,
                MaxOutputTokens = role.MaxOutputTokens,
            },
            CapabilitiesSnapshot = new CapabilityPolicyConfiguration
            {
                SkillIds = role.SkillIds.Order(StringComparer.Ordinal).ToList(),
                McpGrants = role.McpServerIds
                    .Order(StringComparer.Ordinal)
                    .Select(id => new McpGrantConfiguration
                    {
                        McpServerId = id,
                        ToolAllowlist = role.GetMcpToolAllowlist(id)
                            .Order(StringComparer.Ordinal)
                            .ToList(),
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

    private async Task<(ProviderProfileConfiguration Provider, ModelProfileConfiguration Model, string ApiKey)?>
        ResolveImportRuntimeAsync(CancellationToken cancellationToken)
    {
        var model = SelectedImportModel ?? Models.FirstOrDefault(item => item.Enabled);
        if (model is null)
        {
            ShowError("LLM 辅助导入需要先选择一个长期配置模型。");
            return null;
        }
        var provider = Providers.FirstOrDefault(item =>
            item.ProviderProfileId == model.ProviderProfileId && item.Enabled);
        if (provider is null)
        {
            ShowError("导入审阅模型的提供商不可用。");
            return null;
        }
        var apiKey = await _credentialStore.ReadAsync(provider.CredentialRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowError("Credential Manager 中缺少导入审阅模型的提供商凭据。");
            return null;
        }
        return (provider, model, apiKey);
    }

    private bool HandleCatalogImportError(Exception error, CancellationToken cancellationToken)
    {
        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        var message = error switch
        {
            OperationCanceledException => "导入下载或 LLM 审阅超时，临时目录已进入清理流程。",
            System.ComponentModel.Win32Exception => "未找到 Git，或 Git 无法在当前 Windows 环境启动。",
            HttpRequestException => "无法连接 LLM 端点，请检查提供商地址、代理和网络。",
            System.Text.Json.JsonException => "LLM 返回的导入审阅不是有效 JSON。",
            InvalidOperationException => error.Message,
            _ => "导入失败，临时文件已进入清理流程；请检查来源仓库与提供商配置。",
        };
        ShowError(message);
        return true;
    }

    private void ReplaceSkill(SkillProfileConfiguration skill)
    {
        var existing = Skills.FirstOrDefault(item => item.SkillId == skill.SkillId);
        if (existing is not null)
        {
            Skills.Remove(existing);
        }
        Skills.Add(skill);
    }

    private void ReplaceMcpServer(McpServerProfileConfiguration server)
    {
        var existing = McpServers.FirstOrDefault(item => item.McpServerId == server.McpServerId);
        if (existing is not null)
        {
            McpServers.Remove(existing);
        }
        McpServers.Add(server);
    }

    private static string ResolveInstalledSubpath(string installDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Replace('/', Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => part == ".."))
        {
            throw new InvalidOperationException("LLM 返回了越界的 MCP 工作目录。");
        }
        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(installDirectory, relativePath));
        if ((!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
             !candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) ||
            !Directory.Exists(candidate))
        {
            throw new InvalidOperationException("LLM 返回的 MCP 工作目录不存在或越界。");
        }
        return candidate;
    }

    private static bool IsAllowedImportedMcpCommand(string command)
    {
        if (Path.IsPathRooted(command) ||
            command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }
        return command.ToLowerInvariant() is
            "node" or "node.exe" or
            "python" or "python.exe" or "python3" or
            "uv" or "uv.exe" or "uvx" or "uvx.exe" or
            "npx" or "npx.cmd" or "npm" or "npm.cmd" or "pnpm" or "pnpm.cmd" or
            "bun" or "bun.exe" or "deno" or "deno.exe" or
            "dotnet" or "dotnet.exe" or "cargo" or "cargo.exe";
    }

    private static bool ContainsPotentialSecret(string argument)
    {
        var normalized = argument.ToLowerInvariant();
        return normalized.Contains("token=") ||
               normalized.Contains("api-key=") ||
               normalized.Contains("apikey=") ||
               normalized.Contains("secret=") ||
               normalized.Contains("password=") ||
               normalized.StartsWith("sk-", StringComparison.Ordinal);
    }

    private static bool TryNormalizeEndpoint(string value, out string? endpoint)
    {
        return NetworkEndpointPolicy.TryNormalize(value, out endpoint);
    }

    private static string ToStorageState(string state) =>
        state.StartsWith("已取消", StringComparison.Ordinal) ||
        state.StartsWith("失败", StringComparison.Ordinal)
            ? "cancelled"
            : state switch
            {
                "已发送" or "已提交" => "submitted",
                "生成中" or "处理中" or "正在重新排队" => "streaming",
                _ => "completed",
            };

    private static string ToDisplayState(string state) => state switch
    {
        "submitted" => "已发送",
        "streaming" => "生成中",
        "cancelled" => "已取消",
        _ => "已完成",
    };

    private string? ResolveRetryPrompt(string? causationId)
    {
        if (string.IsNullOrWhiteSpace(causationId))
        {
            return null;
        }
        var separator = causationId.LastIndexOf(':');
        var commandId = separator > 0 ? causationId[..separator] : causationId;
        return _publicPromptsByCommandId.TryGetValue(commandId, out var prompt) &&
            !string.IsNullOrWhiteSpace(prompt)
                ? prompt
                : null;
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

    private static List<McpToolProfileConfiguration> ParseMcpToolCatalog(string value)
    {
        var tools = value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0)
            .ToArray();
        if (tools.Length > 256)
        {
            throw new InvalidDataException("单个 MCP 服务器最多复核 256 个工具。");
        }
        if (tools.Any(name => name.Length > 256 || name.Any(char.IsControl)))
        {
            throw new InvalidDataException("MCP 工具名称必须为不超过 256 字符的单行文本。");
        }
        return tools
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => new McpToolProfileConfiguration
            {
                Name = name,
                DisplayName = name,
            })
            .ToList();
    }

    private static void WriteEventIngestionTrace(string message)
    {
        var tracePath = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_EVENT_TRACE_FILE");
        if (string.IsNullOrWhiteSpace(tracePath) || !Path.IsPathFullyQualified(tracePath))
        {
            return;
        }
        File.AppendAllText(
            tracePath,
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }

    private static async Task EnsureAcceptedAsync(Task<RuntimeCommandReceipt> receiptTask)
    {
        var receipt = await receiptTask;
        if (!receipt.Accepted)
        {
            throw new InvalidOperationException(receipt.Message ?? receipt.ErrorCode ?? "Runtime Host rejected a command.");
        }
    }

    private Task WaitForEventQueueAsync()
    {
        return _eventQueue.DrainAsync();
    }

    private async Task DisposeRuntimeAndDrainEventsAsync(IRuntimeHostProcess runtime)
    {
        runtime.MeetingEventReceived -= OnMeetingEventReceived;
        runtime.DiagnosticReceived -= OnDiagnosticReceived;
        runtime.EventStreamFaulted -= OnEventStreamFaulted;
        try
        {
            await runtime.DisposeAsync();
        }
        finally
        {
            // Dispose waits for the stdout reader to finish. Draining afterward
            // therefore fences callbacks that were already in flight when the
            // event handlers were detached.
            await WaitForEventQueueAsync();
        }
    }

    private async Task SuspendAfterStreamFaultAsync()
    {
        try
        {
            await SuspendMeetingAsync();
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

    private static readonly JsonSerializerOptions DiscussionJsonOptions = new(JsonSerializerDefaults.Web);

    private static bool TryReadDiscussionState(
        JsonElement payload,
        out DiscussionSchedulerStateConfiguration state)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<DiscussionSchedulerStateConfiguration>(
                payload.GetRawText(),
                DiscussionJsonOptions);
            if (candidate is null || !candidate.Configured ||
                candidate.Mode is not ("agenda" or "free_discussion" or "convergence" or "paused" or "completed") ||
                candidate.ResumeMode is not ("agenda" or "free_discussion" or "convergence") ||
                candidate.ParticipantCount is < 1 or > 64 ||
                candidate.AgendaItems.Count > 32 ||
                candidate.PendingRequests.Count > 64 ||
                candidate.Limits.SoftTurnLimit < 1 ||
                candidate.Limits.SoftTurnLimit >= candidate.Limits.HardTurnLimit ||
                candidate.Limits.SoftRoundLimit < 1 ||
                candidate.Limits.SoftRoundLimit >= candidate.Limits.HardRoundLimit ||
                candidate.Counters.PublicTurns < 0 ||
                candidate.Counters.PublicTurns > candidate.Limits.HardTurnLimit ||
                candidate.Counters.Rounds < 0 ||
                candidate.Counters.Rounds > candidate.Limits.HardRoundLimit ||
                candidate.PendingRequests.Any(request =>
                    string.IsNullOrWhiteSpace(request.RequestId) ||
                    string.IsNullOrWhiteSpace(request.RoleId) ||
                    string.IsNullOrWhiteSpace(request.Reason) ||
                    string.IsNullOrWhiteSpace(request.Prompt)) ||
                candidate.PendingRequests.Select(request => request.RequestId).Distinct(StringComparer.Ordinal).Count() !=
                    candidate.PendingRequests.Count)
            {
                state = new DiscussionSchedulerStateConfiguration();
                return false;
            }
            state = candidate;
            return true;
        }
        catch (JsonException)
        {
            state = new DiscussionSchedulerStateConfiguration();
            return false;
        }
    }

    private static DiscussionSchedulerStateConfiguration CloneDiscussionState(
        DiscussionSchedulerStateConfiguration state) =>
        JsonSerializer.Deserialize<DiscussionSchedulerStateConfiguration>(
            JsonSerializer.Serialize(state, DiscussionJsonOptions),
            DiscussionJsonOptions)
        ?? throw new InvalidOperationException("自动主持状态无法复制。");

    private static IReadOnlyDictionary<string, object?> BuildDefaultDiscussionLimitsPayload() =>
        new Dictionary<string, object?>
        {
            ["softTurnLimit"] = 8,
            ["hardTurnLimit"] = 12,
            ["softRoundLimit"] = 2,
            ["hardRoundLimit"] = 3,
            ["maxConsecutiveTurnsPerRole"] = 2,
            ["maxInterruptionsPerSegment"] = 2,
            ["maxInterruptionsPerRole"] = 1,
            ["noProgressTurnLimit"] = 2,
            ["maxObserverProbesPerSegment"] = 12,
        };

    private void ProjectAgendaItem(JsonElement payload)
    {
        if (!TryReadString(payload, "agendaItemId", out var agendaItemId) ||
            !TryReadString(payload, "status", out var status) ||
            status is not ("pending" or "active" or "completed"))
        {
            return;
        }
        var item = _discussionState.AgendaItems.FirstOrDefault(candidate =>
            candidate.AgendaItemId == agendaItemId);
        if (item is null)
        {
            item = new DiscussionAgendaItemConfiguration
            {
                AgendaItemId = agendaItemId,
                Title = TryReadString(payload, "title", out var title) ? title : agendaItemId,
            };
            _discussionState.AgendaItems.Add(item);
        }
        else if (TryReadString(payload, "title", out var updatedTitle))
        {
            item.Title = updatedTitle;
        }
        item.Status = status;
        if (status == "active")
        {
            foreach (var other in _discussionState.AgendaItems.Where(candidate =>
                         candidate.AgendaItemId != agendaItemId && candidate.Status == "active"))
            {
                other.Status = "pending";
            }
            _discussionState.ActiveAgendaItemId = agendaItemId;
        }
        else if (_discussionState.ActiveAgendaItemId == agendaItemId)
        {
            _discussionState.ActiveAgendaItemId = null;
        }
    }

    private void ProjectFloorRequest(RuntimeMeetingEvent meetingEvent)
    {
        if (meetingEvent.ActorId is null ||
            !TryReadString(meetingEvent.Payload, "requestId", out var requestId) ||
            !TryReadString(meetingEvent.Payload, "kind", out var kind) ||
            !TryReadString(meetingEvent.Payload, "reason", out var reason) ||
            !TryReadString(meetingEvent.Payload, "prompt", out var prompt) ||
            _discussionState.PendingRequests.Any(item => item.RequestId == requestId))
        {
            return;
        }
        var requestedAtSequence = meetingEvent.Payload.TryGetProperty(
                "requestedAtSequence",
                out var sequenceElement) && sequenceElement.TryGetUInt64(out var parsedSequence)
            ? parsedSequence
            : meetingEvent.Sequence;
        _discussionState.PendingRequests.Add(new DiscussionFloorRequestConfiguration
        {
            RequestId = requestId,
            RoleId = meetingEvent.ActorId,
            Kind = kind,
            Reason = reason,
            Prompt = prompt,
            RequestedAtSequence = requestedAtSequence,
            RespondsToRoleId = TryReadString(meetingEvent.Payload, "respondsToRoleId", out var respondsTo)
                ? respondsTo
                : null,
            AgendaItemId = TryReadString(meetingEvent.Payload, "agendaItemId", out var agendaItemId)
                ? agendaItemId
                : null,
        });
    }

    private void UpdateDiscussionCounters(JsonElement payload)
    {
        SetCounter(payload, "publicTurns", value => _discussionState.Counters.PublicTurns = value);
        SetCounter(payload, "rounds", value => _discussionState.Counters.Rounds = value);
        SetCounter(payload, "noProgressTurns", value => _discussionState.Counters.NoProgressTurns = value);
        SetCounter(payload, "interruptions", value => _discussionState.Counters.Interruptions = value);
        SetCounter(payload, "observerProbes", value => _discussionState.Counters.ObserverProbes = value);
        SetCounter(payload, "consecutiveTurns", value => _discussionState.Counters.ConsecutiveTurns = value);
        _discussionState.Counters.ConsecutiveRoleId = TryReadString(
            payload,
            "consecutiveRoleId",
            out var consecutiveRoleId)
                ? consecutiveRoleId
                : null;
        if (payload.TryGetProperty("interruptionsByRole", out var interruptionCounts) &&
            interruptionCounts.ValueKind == JsonValueKind.Object)
        {
            _discussionState.Counters.InterruptionsByRole = interruptionCounts
                .EnumerateObject()
                .Where(property => property.Value.TryGetInt32(out var count) && count >= 0)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetInt32(),
                    StringComparer.Ordinal);
        }
    }

    private static void SetCounter(JsonElement payload, string name, Action<int> setter)
    {
        if (payload.TryGetProperty(name, out var element) &&
            element.TryGetInt32(out var value) && value >= 0)
        {
            setter(value);
        }
    }

    private static bool TryReadString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        var candidate = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }
        value = candidate;
        return true;
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(MeetingSummary));
        OnPropertyChanged(nameof(ParticipantSummary));
        OnPropertyChanged(nameof(GenerationSummary));
        OnPropertyChanged(nameof(RuntimeStateSummary));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSendPrivate));
        OnPropertyChanged(nameof(CanControlDiscussion));
        OnPropertyChanged(nameof(CanResumeDiscussion));
        OnPropertyChanged(nameof(CanAdvanceAgenda));
        OnPropertyChanged(nameof(DiscussionStripVisibility));
        OnPropertyChanged(nameof(DiscussionResumeVisibility));
        OnPropertyChanged(nameof(DiscussionModeLabel));
        OnPropertyChanged(nameof(DiscussionAgendaSummary));
        OnPropertyChanged(nameof(DiscussionBudgetSummary));
        OnPropertyChanged(nameof(DiscussionQueueSummary));
        OnPropertyChanged(nameof(CanArchiveSelectedRole));
        OnPropertyChanged(nameof(CanPromoteSelectedRole));
        OnPropertyChanged(nameof(TranscriptEmptyVisibility));
        OnPropertyChanged(nameof(ProviderSetupVisibility));
    }

    private void NotifyLifecycleProperties()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(StartMeetingVisibility));
        OnPropertyChanged(nameof(ResumeMeetingVisibility));
        OnPropertyChanged(nameof(PauseMeetingVisibility));
    }

    private void NotifyToolApprovals()
    {
        OnPropertyChanged(nameof(HasPendingToolApprovals));
        OnPropertyChanged(nameof(ToolApprovalLabel));
        OnPropertyChanged(nameof(ToolApprovalSectionLabel));
        OnPropertyChanged(nameof(ToolApprovalEmptyVisibility));
    }

    private void NotifyActivityPanel()
    {
        NotifyToolApprovals();
        OnPropertyChanged(nameof(SubagentActivityLabel));
        OnPropertyChanged(nameof(SubagentEmptyVisibility));
    }

    private void TrimCompletedSubagentRuns()
    {
        while (SubagentRuns.Count >= 12)
        {
            var completed = SubagentRuns.LastOrDefault(item => !item.IsActive);
            if (completed is null)
            {
                return;
            }
            SubagentRuns.Remove(completed);
        }
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
