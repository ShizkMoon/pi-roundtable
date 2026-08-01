using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal static class ConfigurationNormalizer
{
    internal const string DefaultSyncCredentialReference = "wincred://PiRoundtable/sync/default";

    public static WorkspaceConfiguration Normalize(WorkspaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.WorkspaceId = NonEmpty(configuration.WorkspaceId, "workspace.default");
        configuration.DisplayName = NonEmpty(configuration.DisplayName, "个人工作区");

        configuration.Providers = Items(configuration.Providers);
        foreach (var provider in configuration.Providers)
        {
            provider.ProviderProfileId = Identifier(provider.ProviderProfileId);
            provider.DisplayName = Text(provider.DisplayName);
            provider.ApiFamily = NonEmpty(provider.ApiFamily, "openai_responses");
            provider.RuntimeProviderId = Identifier(provider.RuntimeProviderId);
            provider.CredentialRef = NonEmpty(
                provider.CredentialRef,
                $"wincred://PiRoundtable/provider/{NonEmpty(provider.ProviderProfileId, "unconfigured")}");
        }

        configuration.Models = Items(configuration.Models);
        foreach (var model in configuration.Models)
        {
            model.ModelProfileId = Identifier(model.ModelProfileId);
            model.ProviderProfileId = Identifier(model.ProviderProfileId);
            model.ModelId = Identifier(model.ModelId);
            model.DisplayName = Text(model.DisplayName);
            model.Capabilities = Strings(model.Capabilities);
        }

        configuration.Skills = Items(configuration.Skills);
        foreach (var skill in configuration.Skills)
        {
            skill.SkillId = Identifier(skill.SkillId);
            skill.DisplayName = Text(skill.DisplayName);
            skill.Description = Text(skill.Description);
            skill.Source ??= new SkillSourceConfiguration();
            skill.Source.Kind = NonEmpty(skill.Source.Kind, "local");
            skill.Source.Locator = Text(skill.Source.Locator);
            skill.ImportStatus = ImportStatus(skill.ImportStatus);
            skill.InstallDirectory = OptionalText(skill.InstallDirectory);
            skill.AuditSummary = OptionalText(skill.AuditSummary);
        }

        configuration.McpServers = Items(configuration.McpServers);
        foreach (var server in configuration.McpServers)
        {
            server.McpServerId = Identifier(server.McpServerId);
            server.DisplayName = Text(server.DisplayName);
            if (server.Source is not null)
            {
                server.Source.Kind = NonEmpty(server.Source.Kind, "git");
                server.Source.Locator = Text(server.Source.Locator);
            }
            server.ImportStatus = ImportStatus(server.ImportStatus);
            server.InstallDirectory = OptionalText(server.InstallDirectory);
            server.ContentDigest = OptionalText(server.ContentDigest);
            server.AuditSummary = OptionalText(server.AuditSummary);
            server.Transport = NonEmpty(server.Transport, "stdio");
            server.Arguments = server.Arguments is null ? null : Strings(server.Arguments);
            server.EnvironmentCredentialRefs = CredentialReferences(server.EnvironmentCredentialRefs);
            server.HeaderCredentialRefs = CredentialReferences(server.HeaderCredentialRefs);
        }

        configuration.Roles = Items(configuration.Roles);
        foreach (var role in configuration.Roles)
        {
            role.RoleProfileId = Identifier(role.RoleProfileId);
            role.DisplayName = Text(role.DisplayName);
            role.Description = Text(role.Description);
            role.SystemPrompt = Text(role.SystemPrompt);
            role.Responsibilities = Strings(role.Responsibilities);
            role.ModelRoute = Normalize(role.ModelRoute);
            role.Capabilities = Normalize(role.Capabilities);
            role.Delegation ??= new DelegationPolicyConfiguration();
            role.Memory ??= new MemoryPolicyConfiguration();
        }

        configuration.SessionGroups = Items(configuration.SessionGroups);
        foreach (var group in configuration.SessionGroups)
        {
            group.GroupId = Identifier(group.GroupId);
            group.DisplayName = Text(group.DisplayName);
            group.Kind = group.Kind == "project" ? "project" : "folder";
        }

        if (configuration.Defaults is not null)
        {
            configuration.Defaults.ModelRoute = configuration.Defaults.ModelRoute is null
                ? null
                : Normalize(configuration.Defaults.ModelRoute);
            configuration.Defaults.Delegation ??= new DelegationPolicyConfiguration();
        }
        return configuration;
    }

    public static RoundtableSessionConfiguration Normalize(RoundtableSessionConfiguration session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.SessionId = Identifier(session.SessionId);
        if (session.SessionId.Length == 0)
        {
            throw new InvalidDataException("会话 ID 缺失，无法安全加载。");
        }
        session.WorkspaceId = NonEmpty(session.WorkspaceId, "workspace.default");
        session.Title = NonEmpty(session.Title, "未命名圆桌会议");
        session.GroupId = string.IsNullOrWhiteSpace(session.GroupId) ? null : session.GroupId.Trim();
        session.Phase = session.Phase is "draft" or "live" or "closed" ? session.Phase : "draft";
        session.Agenda ??= new SessionAgendaConfiguration();
        session.Agenda.Subject = NonEmpty(session.Agenda.Subject, "待确定议题");
        session.Agenda.Objectives = Strings(session.Agenda.Objectives);
        session.Agenda.Constraints = Strings(session.Agenda.Constraints);

        session.Participants = Items(session.Participants);
        foreach (var participant in session.Participants)
        {
            participant.ParticipantId = Identifier(participant.ParticipantId);
            participant.Scope = participant.Scope == "temporary" ? "temporary" : "long_term";
            participant.DisplayName = Text(participant.DisplayName);
            participant.SystemPromptSnapshot = Text(participant.SystemPromptSnapshot);
            participant.ModelRouteSnapshot = Normalize(participant.ModelRouteSnapshot);
            participant.CapabilitiesSnapshot = Normalize(participant.CapabilitiesSnapshot);
            participant.DelegationSnapshot ??= new DelegationPolicyConfiguration();
            participant.MemoryPolicySnapshot ??= new MemoryPolicyConfiguration();
            participant.RetentionPolicy = NonEmpty(participant.RetentionPolicy, "retain_profile");
            if (participant.Invitation is not null)
            {
                participant.Invitation.InvitationId = Identifier(participant.Invitation.InvitationId);
                participant.Invitation.InviterType = NonEmpty(participant.Invitation.InviterType, "user");
                participant.Invitation.InviterId = NonEmpty(
                    participant.Invitation.InviterId,
                    "user.direct_host");
                participant.Invitation.Purpose = Text(participant.Invitation.Purpose);
                participant.Invitation.Status = NonEmpty(participant.Invitation.Status, "accepted");
            }
        }
        var participantIds = session.Participants
            .Select(participant => participant.ParticipantId)
            .Where(participantId => participantId.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        session.Messages = Items(session.Messages);
        foreach (var message in session.Messages)
        {
            message.MessageId = Identifier(message.MessageId);
            message.Kind = NonEmpty(message.Kind, "role");
            message.SpeakerId = Identifier(message.SpeakerId);
            message.SpeakerName = Text(message.SpeakerName);
            message.Visibility = message.Visibility == "private" ? "private" : "public";
            message.AudienceRoleIds = message.Visibility == "private"
                ? Strings(message.AudienceRoleIds)
                : [];
            if (message.Visibility == "private" && message.AudienceRoleIds.Count == 0)
            {
                throw new InvalidDataException($"私聊消息 {message.MessageId} 缺少角色受众，已阻止公开加载。");
            }
            var unknownAudience = message.AudienceRoleIds.FirstOrDefault(
                roleId => !participantIds.Contains(roleId));
            if (unknownAudience is not null)
            {
                throw new InvalidDataException(
                    $"私聊消息 {message.MessageId} 引用了未知角色 {unknownAudience}，已阻止加载。");
            }
            message.Text = Text(message.Text);
            message.State = NonEmpty(message.State, "completed");
        }
        return session;
    }

    public static ClientSettingsConfiguration Normalize(ClientSettingsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.ConfigurationVersion = 1;
        configuration.ThemeMode = configuration.ThemeMode is "light" or "dark" ? configuration.ThemeMode : "system";
        configuration.RemoteSyncEndpoint = string.IsNullOrWhiteSpace(configuration.RemoteSyncEndpoint)
            ? null
            : configuration.RemoteSyncEndpoint.Trim();
        configuration.RemoteSyncCredentialRef = NonEmpty(
            configuration.RemoteSyncCredentialRef,
            DefaultSyncCredentialReference);
        return configuration;
    }

    private static ModelRouteConfiguration Normalize(ModelRouteConfiguration? route)
    {
        route ??= new ModelRouteConfiguration();
        route.PrimaryModelProfileId = Identifier(route.PrimaryModelProfileId);
        route.FallbackModelProfileIds = Strings(route.FallbackModelProfileIds);
        route.ThinkingLevel = NonEmpty(route.ThinkingLevel, "medium");
        return route;
    }

    private static CapabilityPolicyConfiguration Normalize(CapabilityPolicyConfiguration? policy)
    {
        policy ??= new CapabilityPolicyConfiguration();
        policy.SkillIds = Strings(policy.SkillIds);
        policy.McpGrants = Items(policy.McpGrants);
        foreach (var grant in policy.McpGrants)
        {
            grant.McpServerId = Identifier(grant.McpServerId);
            grant.ToolAllowlist = Strings(grant.ToolAllowlist);
            grant.ApprovalMode = NonEmpty(grant.ApprovalMode, "always");
            grant.ExecutionMode = NonEmpty(grant.ExecutionMode, "subagent_preferred");
        }
        policy.ToolGrants = Items(policy.ToolGrants);
        foreach (var grant in policy.ToolGrants)
        {
            grant.ToolId = Identifier(grant.ToolId);
            grant.ApprovalMode = NonEmpty(grant.ApprovalMode, "always");
            grant.ExecutionMode = NonEmpty(grant.ExecutionMode, "subagent_required");
        }
        return policy;
    }

    private static List<T> Items<T>(IEnumerable<T>? values) where T : class =>
        values?.OfType<T>().ToList() ?? [];

    private static List<string> Strings(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    private static Dictionary<string, string>? CredentialReferences(
        Dictionary<string, string>? values) =>
        values?
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value.Trim(),
                StringComparer.Ordinal);

    private static string Text(string? value) => value ?? string.Empty;

    private static string? OptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ImportStatus(string? value) => value is "installed" or "review_required" or "blocked"
        ? value
        : "registered";

    private static string Identifier(string? value) => value?.Trim() ?? string.Empty;

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
