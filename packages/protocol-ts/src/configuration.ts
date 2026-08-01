export const WORKSPACE_CONFIGURATION_VERSION = 1 as const;
export const ROUNDTABLE_SESSION_VERSION = 1 as const;

export type WorkspaceConfigurationVersion = typeof WORKSPACE_CONFIGURATION_VERSION;
export type RoundtableSessionVersion = typeof ROUNDTABLE_SESSION_VERSION;

export const API_FAMILIES = [
  "openai_responses",
  "openai_chat_completions",
  "anthropic_messages",
  "google_generate_content",
  "custom",
] as const;
export type ApiFamily = (typeof API_FAMILIES)[number];

export const MODEL_CAPABILITIES = [
  "text",
  "vision",
  "reasoning",
  "tool_calling",
  "audio",
] as const;
export type ModelCapability = (typeof MODEL_CAPABILITIES)[number];

export const THINKING_LEVELS = ["off", "minimal", "low", "medium", "high", "xhigh"] as const;
export type ThinkingLevel = (typeof THINKING_LEVELS)[number];

export type ApprovalMode = "always" | "on_first_use" | "never";
export type ExecutionMode = "direct" | "subagent_preferred" | "subagent_required";
export type CredentialRef = string;

export interface ProviderProfile {
  providerProfileId: string;
  displayName: string;
  apiFamily: ApiFamily;
  runtimeProviderId: string;
  endpoint?: string;
  credentialRef: CredentialRef;
  enabled: boolean;
}

export interface ModelProfile {
  modelProfileId: string;
  providerProfileId: string;
  modelId: string;
  displayName: string;
  capabilities: ModelCapability[];
  contextWindow?: number;
  enabled: boolean;
}

export interface SkillSource {
  kind: "builtin" | "local" | "git";
  locator: string;
  version?: string;
  contentDigest?: string;
}

export interface SkillProfile {
  skillId: string;
  displayName: string;
  description: string;
  source: SkillSource;
  risk?: "low" | "medium" | "high";
  importStatus?: "registered" | "installed" | "review_required" | "blocked";
  installDirectory?: string;
  auditSummary?: string;
  auditedAt?: string;
  enabled: boolean;
}

export interface McpServerProfile {
  mcpServerId: string;
  displayName: string;
  source?: SkillSource;
  risk?: "low" | "medium" | "high";
  importStatus?: "registered" | "installed" | "review_required" | "blocked";
  installDirectory?: string;
  contentDigest?: string;
  auditSummary?: string;
  auditedAt?: string;
  transport: "stdio" | "streamable_http" | "sse";
  command?: string;
  arguments?: string[];
  workingDirectory?: string;
  endpoint?: string;
  environmentCredentialRefs?: Record<string, CredentialRef>;
  headerCredentialRefs?: Record<string, CredentialRef>;
  enabled: boolean;
}

export interface ModelRoute {
  primaryModelProfileId: string;
  fallbackModelProfileIds: string[];
  thinkingLevel: ThinkingLevel;
  maxOutputTokens?: number;
}

export interface McpGrant {
  mcpServerId: string;
  toolAllowlist: string[];
  approvalMode: ApprovalMode;
  executionMode: ExecutionMode;
}

export interface ToolGrant {
  toolId: string;
  approvalMode: ApprovalMode;
  executionMode: ExecutionMode;
}

export interface CapabilityPolicy {
  skillIds: string[];
  mcpGrants: McpGrant[];
  toolGrants: ToolGrant[];
}

export interface DelegationPolicy {
  networkAccess: "forbidden" | "subagent_required" | "subagent_preferred" | "direct_allowed";
  resultMode: "summary_with_citations" | "summary" | "full";
  maxConcurrentSubagents: number;
}

export interface MemoryPolicy {
  mode: "disabled" | "selective" | "continuous";
  writeApproval: "always" | "meeting_close" | "automatic";
  promptEvolution: "disabled" | "propose" | "review_required" | "automatic";
}

export interface RoleProfile {
  roleProfileId: string;
  displayName: string;
  description: string;
  systemPrompt: string;
  responsibilities: string[];
  autoJoin: boolean;
  modelRoute: ModelRoute;
  capabilities: CapabilityPolicy;
  delegation: DelegationPolicy;
  memory: MemoryPolicy;
}

export interface WorkspaceDefaults {
  modelRoute?: ModelRoute;
  delegation?: DelegationPolicy;
}

export interface SessionGroupProfile {
  groupId: string;
  displayName: string;
  kind: "project" | "folder";
  sortOrder: number;
}

export interface WorkspaceProfile {
  configurationVersion: WorkspaceConfigurationVersion;
  workspaceId: string;
  displayName: string;
  updatedAt: string;
  providers: ProviderProfile[];
  models: ModelProfile[];
  skills: SkillProfile[];
  mcpServers: McpServerProfile[];
  roles: RoleProfile[];
  sessionGroups?: SessionGroupProfile[];
  defaults?: WorkspaceDefaults;
}

export interface SessionAgenda {
  subject: string;
  objectives: string[];
  constraints: string[];
}

export interface TemporaryRoleInvitation {
  invitationId: string;
  inviterType: "user" | "role";
  inviterId: string;
  purpose: string;
  status: "accepted";
  createdAt: string;
  expiresAt?: string;
  acceptedAt: string;
}

interface ParticipantManifestBase {
  participantId: string;
  displayName: string;
  systemPromptSnapshot: string;
  modelRouteSnapshot: ModelRoute;
  capabilitiesSnapshot: CapabilityPolicy;
  delegationSnapshot: DelegationPolicy;
  memoryPolicySnapshot: MemoryPolicy;
}

export interface LongTermParticipantManifest extends ParticipantManifestBase {
  scope: "long_term";
  roleProfileId: string;
  retentionPolicy: "retain_profile";
  invitation?: never;
}

export interface TemporaryParticipantManifest extends ParticipantManifestBase {
  scope: "temporary";
  roleProfileId?: never;
  invitation: TemporaryRoleInvitation;
  retentionPolicy: "delete_after_session" | "review_at_close" | "promote_candidate";
}

export type ParticipantManifest = LongTermParticipantManifest | TemporaryParticipantManifest;

export interface RoundtableSession {
  sessionVersion: RoundtableSessionVersion;
  sessionId: string;
  workspaceId: string;
  title: string;
  groupId?: string;
  phase: "draft" | "live" | "closed";
  createdAt: string;
  updatedAt: string;
  agenda: SessionAgenda;
  participants: ParticipantManifest[];
  messages?: SessionMessage[];
}

export interface SessionMessage {
  messageId: string;
  kind: "host" | "role" | "system";
  speakerId: string;
  speakerName: string;
  visibility: "public" | "private";
  audienceRoleIds: string[];
  text: string;
  state: "submitted" | "streaming" | "completed" | "cancelled";
  occurredAt: string;
}
