using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class RoleItem : INotifyPropertyChanged
{
    private string _scope;
    private string _status = "未连接";
    private string _systemPrompt;
    private string _modelProfileId = string.Empty;
    private string _retentionPolicy;
    private string _networkAccess;
    private bool _isArchived;
    private string _activitySummary = "空闲；未公开模型私有推理";

    public RoleItem(
        string roleId,
        string displayName,
        string scope,
        string? systemPrompt = null,
        string? modelProfileId = null,
        string? invitationPurpose = null,
        string? inviterId = null,
        string? retentionPolicy = null,
        string? networkAccess = null,
        string? invitationId = null,
        DateTimeOffset? invitedAt = null)
    {
        RoleId = roleId;
        DisplayName = displayName;
        _scope = scope;
        _systemPrompt = systemPrompt ?? $"你是圆桌会议中的{displayName}。只在职责范围内工作，并明确说明不确定性。";
        _modelProfileId = modelProfileId ?? string.Empty;
        _retentionPolicy = retentionPolicy ?? (scope == "long_term" ? "retain_profile" : "review_at_close");
        _networkAccess = networkAccess ?? "subagent_required";
        InvitationId = scope == "temporary" ? invitationId ?? $"invite.{Guid.NewGuid():N}" : null;
        InvitationPurpose = invitationPurpose;
        InviterId = inviterId;
        InvitedAt = scope == "temporary" ? invitedAt ?? DateTimeOffset.UtcNow : null;
    }

    public string RoleId { get; }

    public string DisplayName { get; }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetField(ref _systemPrompt, value);
    }

    public string ModelProfileId
    {
        get => _modelProfileId;
        set
        {
            if (SetField(ref _modelProfileId, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CapabilityGrantSummary)));
            }
        }
    }

    public string RetentionPolicy
    {
        get => _retentionPolicy;
        set => SetField(ref _retentionPolicy, value);
    }

    public string NetworkAccess
    {
        get => _networkAccess;
        set
        {
            if (SetField(ref _networkAccess, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CapabilityGrantSummary)));
            }
        }
    }

    public HashSet<string> SkillIds { get; } = new(StringComparer.Ordinal);

    public HashSet<string> McpServerIds { get; } = new(StringComparer.Ordinal);

    public string? InvitationId { get; }

    public string? InvitationPurpose { get; }

    public string? InviterId { get; }

    public DateTimeOffset? InvitedAt { get; }

    public string Scope
    {
        get => _scope;
        set => SetField(ref _scope, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsArchived
    {
        get => _isArchived;
        set => SetField(ref _isArchived, value);
    }

    public string ActivitySummary
    {
        get => _activitySummary;
        set => SetField(ref _activitySummary, value);
    }

    public string ScopeLabel => Scope == "long_term" ? "长期角色" : "临时角色";

    public string Summary => $"{ScopeLabel} · {Status}";

    public string CapabilitiesSummary => $"{SkillIds.Count} Skills · {McpServerIds.Count} MCP";

    public string CapabilityGrantSummary =>
        $"{(string.IsNullOrWhiteSpace(ModelProfileId) ? "未路由模型" : ModelProfileId)} · " +
        $"{SkillIds.Count} Skill · {McpServerIds.Count} MCP · 工具逐次审批 · {NetworkAccessLabel}";

    private string NetworkAccessLabel => NetworkAccess switch
    {
        "direct_allowed" => "允许直连",
        "forbidden" => "禁止联网",
        "subagent_preferred" => "优先委派",
        _ => "必须委派",
    };

    public void NotifyCapabilitiesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CapabilitiesSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CapabilityGrantSummary)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => DisplayName;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Scope) or nameof(Status))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScopeLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
        return true;
    }
}
