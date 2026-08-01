import type {
  CapabilityPolicy,
  McpServerProfile,
  ModelRoute,
  ParticipantManifest,
  RoundtableSession,
  WorkspaceProfile,
} from "./configuration.js";

export interface ConfigurationValidationIssue {
  path: string;
  code:
    | "duplicate_id"
    | "invalid_id"
    | "invalid_timestamp"
    | "invalid_credential_ref"
    | "invalid_endpoint"
    | "invalid_transport_fields"
    | "invalid_participant_count"
    | "missing_reference"
    | "disabled_reference"
    | "workspace_mismatch"
    | "invalid_invitation"
    | "invalid_retention"
    | "invalid_audience";
  message: string;
}

const CREDENTIAL_REF_PATTERN = /^[a-z][a-z0-9+.-]*:\/\/\S+$/;
const ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;

function validateId(
  value: string,
  path: string,
  issues: ConfigurationValidationIssue[],
): void {
  if (!ID_PATTERN.test(value)) {
    issues.push({ path, code: "invalid_id", message: `Invalid identifier '${value}'.` });
  }
}

function addDuplicateIssues<T>(
  values: readonly T[],
  getId: (value: T) => string,
  path: string,
  issues: ConfigurationValidationIssue[],
): void {
  const seen = new Set<string>();
  for (const [index, value] of values.entries()) {
    const id = getId(value);
    if (seen.has(id)) {
      issues.push({
        path: `${path}[${index}]`,
        code: "duplicate_id",
        message: `Duplicate identifier '${id}'.`,
      });
    }
    seen.add(id);
  }
}

function validateCredentialRef(
  value: string,
  path: string,
  issues: ConfigurationValidationIssue[],
): void {
  if (!CREDENTIAL_REF_PATTERN.test(value)) {
    issues.push({
      path,
      code: "invalid_credential_ref",
      message: "Credential references must be opaque URI-like secure-store references.",
    });
  }
}

function validateEndpoint(
  value: string | undefined,
  path: string,
  issues: ConfigurationValidationIssue[],
): void {
  if (value === undefined) return;
  try {
    const endpoint = new URL(value);
    const isLoopback = ["localhost", "127.0.0.1", "[::1]", "::1"].includes(endpoint.hostname);
    if (
      endpoint.username !== "" ||
      endpoint.password !== "" ||
      (endpoint.protocol !== "https:" && !(endpoint.protocol === "http:" && isLoopback))
    ) {
      throw new Error("unsafe endpoint");
    }
  } catch {
    issues.push({
      path,
      code: "invalid_endpoint",
      message: "Endpoints must use HTTPS, or HTTP on loopback, and cannot contain URI userinfo.",
    });
  }
}

function validateMcpTransport(
  server: McpServerProfile,
  path: string,
  issues: ConfigurationValidationIssue[],
): void {
  const stdioOnlyFieldsPresent =
    server.command !== undefined ||
    server.arguments !== undefined ||
    server.workingDirectory !== undefined ||
    server.environmentCredentialRefs !== undefined;
  if (server.transport === "stdio") {
    if (!server.command || server.endpoint !== undefined || server.headerCredentialRefs !== undefined) {
      issues.push({
        path,
        code: "invalid_transport_fields",
        message: "stdio MCP servers require command and cannot define remote endpoint/header fields.",
      });
    }
  } else if (!server.endpoint || stdioOnlyFieldsPresent) {
    issues.push({
      path,
      code: "invalid_transport_fields",
      message: "Remote MCP servers require endpoint and cannot define stdio process fields.",
    });
  }
}

function validateModelRoute(
  route: ModelRoute,
  path: string,
  workspace: WorkspaceProfile,
  issues: ConfigurationValidationIssue[],
): void {
  const models = new Map(workspace.models.map((model) => [model.modelProfileId, model]));
  for (const [index, id] of [route.primaryModelProfileId, ...route.fallbackModelProfileIds].entries()) {
    const model = models.get(id);
    const referencePath = index === 0 ? `${path}.primaryModelProfileId` : `${path}.fallbackModelProfileIds[${index - 1}]`;
    validateId(id, referencePath, issues);
    if (!model) {
      issues.push({ path: referencePath, code: "missing_reference", message: `Unknown model profile '${id}'.` });
    } else if (!model.enabled) {
      issues.push({ path: referencePath, code: "disabled_reference", message: `Model profile '${id}' is disabled.` });
    }
  }
}

function validateCapabilities(
  policy: CapabilityPolicy,
  path: string,
  workspace: WorkspaceProfile,
  issues: ConfigurationValidationIssue[],
): void {
  const skills = new Map(workspace.skills.map((skill) => [skill.skillId, skill]));
  const servers = new Map(workspace.mcpServers.map((server) => [server.mcpServerId, server]));
  addDuplicateIssues(policy.skillIds, (id) => id, `${path}.skillIds`, issues);
  addDuplicateIssues(policy.mcpGrants, (grant) => grant.mcpServerId, `${path}.mcpGrants`, issues);
  addDuplicateIssues(policy.toolGrants, (grant) => grant.toolId, `${path}.toolGrants`, issues);
  for (const [index, id] of policy.skillIds.entries()) {
    validateId(id, `${path}.skillIds[${index}]`, issues);
    const skill = skills.get(id);
    if (!skill) {
      issues.push({ path: `${path}.skillIds[${index}]`, code: "missing_reference", message: `Unknown skill '${id}'.` });
    } else if (!skill.enabled) {
      issues.push({ path: `${path}.skillIds[${index}]`, code: "disabled_reference", message: `Skill '${id}' is disabled.` });
    }
  }
  for (const [index, grant] of policy.mcpGrants.entries()) {
    validateId(grant.mcpServerId, `${path}.mcpGrants[${index}].mcpServerId`, issues);
    const server = servers.get(grant.mcpServerId);
    if (!server) {
      issues.push({ path: `${path}.mcpGrants[${index}]`, code: "missing_reference", message: `Unknown MCP server '${grant.mcpServerId}'.` });
    } else if (!server.enabled) {
      issues.push({ path: `${path}.mcpGrants[${index}]`, code: "disabled_reference", message: `MCP server '${grant.mcpServerId}' is disabled.` });
    }
  }
  for (const [index, grant] of policy.toolGrants.entries()) {
    validateId(grant.toolId, `${path}.toolGrants[${index}].toolId`, issues);
  }
}

export function validateWorkspaceProfile(profile: WorkspaceProfile): ConfigurationValidationIssue[] {
  const issues: ConfigurationValidationIssue[] = [];
  validateId(profile.workspaceId, "workspaceId", issues);
  if (!Number.isFinite(Date.parse(profile.updatedAt))) {
    issues.push({ path: "updatedAt", code: "invalid_timestamp", message: "Workspace updatedAt must be a valid timestamp." });
  }
  addDuplicateIssues(profile.providers, (value) => value.providerProfileId, "providers", issues);
  addDuplicateIssues(profile.models, (value) => value.modelProfileId, "models", issues);
  addDuplicateIssues(profile.skills, (value) => value.skillId, "skills", issues);
  addDuplicateIssues(profile.mcpServers, (value) => value.mcpServerId, "mcpServers", issues);
  addDuplicateIssues(profile.roles, (value) => value.roleProfileId, "roles", issues);
  addDuplicateIssues(profile.sessionGroups ?? [], (value) => value.groupId, "sessionGroups", issues);

  for (const [index, group] of (profile.sessionGroups ?? []).entries()) {
    validateId(group.groupId, `sessionGroups[${index}].groupId`, issues);
  }

  for (const [index, skill] of profile.skills.entries()) {
    validateId(skill.skillId, `skills[${index}].skillId`, issues);
  }

  const providers = new Map(profile.providers.map((provider) => [provider.providerProfileId, provider]));
  for (const [index, provider] of profile.providers.entries()) {
    validateId(provider.providerProfileId, `providers[${index}].providerProfileId`, issues);
    validateId(provider.runtimeProviderId, `providers[${index}].runtimeProviderId`, issues);
    validateCredentialRef(provider.credentialRef, `providers[${index}].credentialRef`, issues);
    validateEndpoint(provider.endpoint, `providers[${index}].endpoint`, issues);
  }
  for (const [index, model] of profile.models.entries()) {
    validateId(model.modelProfileId, `models[${index}].modelProfileId`, issues);
    validateId(model.providerProfileId, `models[${index}].providerProfileId`, issues);
    const provider = providers.get(model.providerProfileId);
    if (!provider) {
      issues.push({ path: `models[${index}].providerProfileId`, code: "missing_reference", message: `Unknown provider profile '${model.providerProfileId}'.` });
    } else if (!provider.enabled) {
      issues.push({ path: `models[${index}].providerProfileId`, code: "disabled_reference", message: `Provider profile '${model.providerProfileId}' is disabled.` });
    }
  }
  for (const [index, server] of profile.mcpServers.entries()) {
    validateId(server.mcpServerId, `mcpServers[${index}].mcpServerId`, issues);
    validateMcpTransport(server, `mcpServers[${index}]`, issues);
    validateEndpoint(server.endpoint, `mcpServers[${index}].endpoint`, issues);
    for (const [name, reference] of Object.entries(server.environmentCredentialRefs ?? {})) {
      validateCredentialRef(reference, `mcpServers[${index}].environmentCredentialRefs.${name}`, issues);
    }
    for (const [name, reference] of Object.entries(server.headerCredentialRefs ?? {})) {
      validateCredentialRef(reference, `mcpServers[${index}].headerCredentialRefs.${name}`, issues);
    }
  }
  for (const [index, role] of profile.roles.entries()) {
    validateId(role.roleProfileId, `roles[${index}].roleProfileId`, issues);
    validateModelRoute(role.modelRoute, `roles[${index}].modelRoute`, profile, issues);
    validateCapabilities(role.capabilities, `roles[${index}].capabilities`, profile, issues);
  }
  if (profile.defaults?.modelRoute) {
    validateModelRoute(profile.defaults.modelRoute, "defaults.modelRoute", profile, issues);
  }
  return issues;
}

function validateParticipant(
  participant: ParticipantManifest,
  index: number,
  session: RoundtableSession,
  workspace: WorkspaceProfile,
  issues: ConfigurationValidationIssue[],
): void {
  const path = `participants[${index}]`;
  validateId(participant.participantId, `${path}.participantId`, issues);
  validateModelRoute(participant.modelRouteSnapshot, `${path}.modelRouteSnapshot`, workspace, issues);
  validateCapabilities(participant.capabilitiesSnapshot, `${path}.capabilitiesSnapshot`, workspace, issues);
  if (participant.scope === "long_term") {
    if (!workspace.roles.some((role) => role.roleProfileId === participant.roleProfileId)) {
      issues.push({ path: `${path}.roleProfileId`, code: "missing_reference", message: `Unknown long-term role '${participant.roleProfileId}'.` });
    }
    validateId(participant.roleProfileId, `${path}.roleProfileId`, issues);
    if (participant.retentionPolicy !== "retain_profile") {
      issues.push({ path: `${path}.retentionPolicy`, code: "invalid_retention", message: "Long-term roles must retain their role profile." });
    }
    return;
  }

  const invitation = participant.invitation;
  validateId(invitation.invitationId, `${path}.invitation.invitationId`, issues);
  validateId(invitation.inviterId, `${path}.invitation.inviterId`, issues);
  const roleInviter = session.participants.find(
    (candidate) => candidate.participantId === invitation.inviterId,
  );
  if (invitation.inviterType === "role" && roleInviter?.scope !== "long_term") {
    issues.push({ path: `${path}.invitation.inviterId`, code: "invalid_invitation", message: "Role inviter must identify a long-term participant in this session." });
  }
  if (
    (invitation.inviterType === "role" && invitation.inviterId === participant.participantId) ||
    (invitation.inviterType === "user" && invitation.inviterId !== "user.direct_host")
  ) {
    issues.push({ path: `${path}.invitation.inviterId`, code: "invalid_invitation", message: "Invitation provenance does not match inviterType." });
  }
  const createdAt = Date.parse(invitation.createdAt);
  const acceptedAt = Date.parse(invitation.acceptedAt);
  const expiresAt = invitation.expiresAt === undefined ? undefined : Date.parse(invitation.expiresAt);
  if (!Number.isFinite(createdAt) || !Number.isFinite(acceptedAt) || acceptedAt < createdAt || (expiresAt !== undefined && (!Number.isFinite(expiresAt) || acceptedAt > expiresAt))) {
    issues.push({ path: `${path}.invitation`, code: "invalid_invitation", message: "Invitation timestamps must be ordered createdAt <= acceptedAt <= expiresAt." });
  }
}

export function validateRoundtableSession(
  session: RoundtableSession,
  workspace: WorkspaceProfile,
): ConfigurationValidationIssue[] {
  const issues: ConfigurationValidationIssue[] = [];
  validateId(session.sessionId, "sessionId", issues);
  validateId(session.workspaceId, "workspaceId", issues);
  if (session.groupId !== undefined) {
    validateId(session.groupId, "groupId", issues);
    if (!(workspace.sessionGroups ?? []).some((group) => group.groupId === session.groupId)) {
      issues.push({ path: "groupId", code: "missing_reference", message: `Unknown session group '${session.groupId}'.` });
    }
  }
  const createdAt = Date.parse(session.createdAt);
  const updatedAt = Date.parse(session.updatedAt);
  if (!Number.isFinite(createdAt) || !Number.isFinite(updatedAt) || updatedAt < createdAt) {
    issues.push({ path: "updatedAt", code: "invalid_timestamp", message: "Session timestamps must be valid and ordered createdAt <= updatedAt." });
  }
  if (session.workspaceId !== workspace.workspaceId) {
    issues.push({ path: "workspaceId", code: "workspace_mismatch", message: "Session and workspace identifiers do not match." });
  }
  if (session.phase !== "draft" && session.participants.length === 0) {
    issues.push({
      path: "participants",
      code: "invalid_participant_count",
      message: "Live and closed sessions require at least one participant.",
    });
  }
  addDuplicateIssues(session.participants, (value) => value.participantId, "participants", issues);
  addDuplicateIssues(session.messages ?? [], (value) => value.messageId, "messages", issues);
  const participantIds = new Set(session.participants.map((participant) => participant.participantId));
  for (const [index, message] of (session.messages ?? []).entries()) {
    validateId(message.messageId, `messages[${index}].messageId`, issues);
    validateId(message.speakerId, `messages[${index}].speakerId`, issues);
    for (const [audienceIndex, roleId] of message.audienceRoleIds.entries()) {
      validateId(roleId, `messages[${index}].audienceRoleIds[${audienceIndex}]`, issues);
      if (!participantIds.has(roleId)) {
        issues.push({
          path: `messages[${index}].audienceRoleIds[${audienceIndex}]`,
          code: "missing_reference",
          message: `Unknown participant audience '${roleId}'.`,
        });
      }
    }
    if (!Number.isFinite(Date.parse(message.occurredAt))) {
      issues.push({ path: `messages[${index}].occurredAt`, code: "invalid_timestamp", message: "Message occurredAt must be a valid timestamp." });
    }
    if (message.visibility === "private" && message.audienceRoleIds.length === 0) {
      issues.push({ path: `messages[${index}].audienceRoleIds`, code: "missing_reference", message: "Private messages require at least one audience role." });
    }
    if (message.visibility === "public" && message.audienceRoleIds.length > 0) {
      issues.push({ path: `messages[${index}].audienceRoleIds`, code: "invalid_audience", message: "Public messages must not carry a private audience." });
    }
  }
  for (const [index, participant] of session.participants.entries()) {
    validateParticipant(participant, index, session, workspace, issues);
  }
  return issues;
}
