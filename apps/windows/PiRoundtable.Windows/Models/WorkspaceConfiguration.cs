namespace PiRoundtable.Windows.Models;

public sealed class WorkspaceConfiguration
{
    public int ConfigurationVersion { get; set; } = 1;
    public string WorkspaceId { get; set; } = "workspace.default";
    public string DisplayName { get; set; } = "个人工作区";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ProviderProfileConfiguration> Providers { get; set; } = [];
    public List<ModelProfileConfiguration> Models { get; set; } = [];
    public List<SkillProfileConfiguration> Skills { get; set; } = [];
    public List<McpServerProfileConfiguration> McpServers { get; set; } = [];
    public List<RoleProfileConfiguration> Roles { get; set; } = [];
    public List<SessionGroupProfileConfiguration> SessionGroups { get; set; } = [];
    public WorkspaceDefaultsConfiguration? Defaults { get; set; }
}

public sealed class SessionGroupProfileConfiguration
{
    public string GroupId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = "folder";
    public int SortOrder { get; set; }
}

public sealed class ProviderProfileConfiguration
{
    public string ProviderProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ApiFamily { get; set; } = "openai_responses";
    public string RuntimeProviderId { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string CredentialRef { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public override string ToString() => DisplayName;
}

public sealed class ModelProfileConfiguration
{
    public string ModelProfileId { get; set; } = string.Empty;
    public string ProviderProfileId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = ["text", "reasoning", "tool_calling"];
    public int? ContextWindow { get; set; }
    public bool Enabled { get; set; } = true;

    public override string ToString() => DisplayName;
}

public sealed class SkillProfileConfiguration
{
    public string SkillId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SkillSourceConfiguration Source { get; set; } = new();
    public string? Risk { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class SkillSourceConfiguration
{
    public string Kind { get; set; } = "local";
    public string Locator { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? ContentDigest { get; set; }
}

public sealed class McpServerProfileConfiguration
{
    public string McpServerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string>? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Endpoint { get; set; }
    public Dictionary<string, string>? EnvironmentCredentialRefs { get; set; }
    public Dictionary<string, string>? HeaderCredentialRefs { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ModelRouteConfiguration
{
    public string PrimaryModelProfileId { get; set; } = string.Empty;
    public List<string> FallbackModelProfileIds { get; set; } = [];
    public string ThinkingLevel { get; set; } = "medium";
    public int? MaxOutputTokens { get; set; }
}

public sealed class CapabilityPolicyConfiguration
{
    public List<string> SkillIds { get; set; } = [];
    public List<McpGrantConfiguration> McpGrants { get; set; } = [];
    public List<ToolGrantConfiguration> ToolGrants { get; set; } = [];
}

public sealed class McpGrantConfiguration
{
    public string McpServerId { get; set; } = string.Empty;
    public List<string> ToolAllowlist { get; set; } = [];
    public string ApprovalMode { get; set; } = "always";
    public string ExecutionMode { get; set; } = "subagent_preferred";
}

public sealed class ToolGrantConfiguration
{
    public string ToolId { get; set; } = string.Empty;
    public string ApprovalMode { get; set; } = "always";
    public string ExecutionMode { get; set; } = "subagent_required";
}

public sealed class DelegationPolicyConfiguration
{
    public string NetworkAccess { get; set; } = "subagent_required";
    public string ResultMode { get; set; } = "summary_with_citations";
    public int MaxConcurrentSubagents { get; set; } = 2;
}

public sealed class MemoryPolicyConfiguration
{
    public string Mode { get; set; } = "selective";
    public string WriteApproval { get; set; } = "meeting_close";
    public string PromptEvolution { get; set; } = "review_required";
}

public sealed class RoleProfileConfiguration
{
    public string RoleProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public List<string> Responsibilities { get; set; } = [];
    public bool AutoJoin { get; set; } = true;
    public ModelRouteConfiguration ModelRoute { get; set; } = new();
    public CapabilityPolicyConfiguration Capabilities { get; set; } = new();
    public DelegationPolicyConfiguration Delegation { get; set; } = new();
    public MemoryPolicyConfiguration Memory { get; set; } = new();
}

public sealed class WorkspaceDefaultsConfiguration
{
    public ModelRouteConfiguration? ModelRoute { get; set; }
    public DelegationPolicyConfiguration? Delegation { get; set; }
}

public sealed class RoundtableSessionConfiguration
{
    public int SessionVersion { get; set; } = 1;
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public string Phase { get; set; } = "draft";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public SessionAgendaConfiguration Agenda { get; set; } = new();
    public List<ParticipantManifestConfiguration> Participants { get; set; } = [];
    public List<SessionMessageConfiguration> Messages { get; set; } = [];
}

public sealed class SessionMessageConfiguration
{
    public string MessageId { get; set; } = string.Empty;
    public string Kind { get; set; } = "role";
    public string SpeakerId { get; set; } = string.Empty;
    public string SpeakerName { get; set; } = string.Empty;
    public string Visibility { get; set; } = "public";
    public List<string> AudienceRoleIds { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public string State { get; set; } = "completed";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class SessionAgendaConfiguration
{
    public string Subject { get; set; } = "待确定议题";
    public List<string> Objectives { get; set; } = [];
    public List<string> Constraints { get; set; } = [];
}

public sealed class ParticipantManifestConfiguration
{
    public string ParticipantId { get; set; } = string.Empty;
    public string Scope { get; set; } = "long_term";
    public string? RoleProfileId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string SystemPromptSnapshot { get; set; } = string.Empty;
    public ModelRouteConfiguration ModelRouteSnapshot { get; set; } = new();
    public CapabilityPolicyConfiguration CapabilitiesSnapshot { get; set; } = new();
    public DelegationPolicyConfiguration DelegationSnapshot { get; set; } = new();
    public MemoryPolicyConfiguration MemoryPolicySnapshot { get; set; } = new();
    public TemporaryRoleInvitationConfiguration? Invitation { get; set; }
    public string RetentionPolicy { get; set; } = "retain_profile";
}

public sealed class TemporaryRoleInvitationConfiguration
{
    public string InvitationId { get; set; } = string.Empty;
    public string InviterType { get; set; } = "user";
    public string InviterId { get; set; } = "user.direct_host";
    public string Purpose { get; set; } = string.Empty;
    public string Status { get; set; } = "accepted";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
}
