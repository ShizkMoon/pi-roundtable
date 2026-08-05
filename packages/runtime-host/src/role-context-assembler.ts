import type {
  ApiFamily,
  ModelCapability,
  ParticipantManifest,
  RoleScope,
  ThinkingLevel,
  WorkspaceProfile,
} from "@pi-roundtable/protocol";

import {
  WorkspaceCapabilityResolver,
  type CapabilityResolver,
  type CredentialReferenceResolver,
} from "./capability-resolver.js";
import {
  WorkspaceProviderCapabilityRegistry,
  type ProviderCapabilityRegistry,
} from "./provider-capability-registry.js";
import { RoleCredentialLease } from "./role-credential-lease.js";
import { resolvePiPluginSet } from "./pi-plugin-compatibility.js";

export interface ResolvedRoleRuntimeConfiguration {
  displayName: string;
  providerId: string;
  providerName: string;
  apiFamily: ApiFamily;
  endpoint?: string;
  modelId: string;
  modelName: string;
  modelCapabilities: ModelCapability[];
  contextWindow?: number;
  maxOutputTokens?: number;
  thinkingLevel?: ThinkingLevel;
  systemPrompt: string;
  skillPaths: string[];
  frozenMemory: readonly FrozenRoleMemory[];
  recoveryContext?: string;
  webSearch?: {
    approvalMode: "always" | "on_first_use" | "never";
    executionMode: "direct" | "subagent_preferred" | "subagent_required";
  };
  credentialLease: RoleCredentialLease;
  delegation: {
    networkAccess: "forbidden" | "subagent_required" | "subagent_preferred" | "direct_allowed";
    resultMode: "summary_with_citations" | "summary" | "full";
    maxConcurrentSubagents: number;
  };
}

export interface FrozenRoleMemory {
  readonly memoryId: string;
  readonly revision: number;
  readonly content: string;
}

export interface RoleContextAssemblyRequest {
  workspace: WorkspaceProfile;
  participant: ParticipantManifest;
  roleId: string;
  scope: RoleScope;
  runtimeGeneration: number;
  resolveCredential: CredentialReferenceResolver;
  memoryRecall?: readonly FrozenRoleMemory[];
  recoveryContext?: string;
}

/** Builds one role's private, frozen runtime configuration. */
export interface RoleContextAssembler {
  assemble(request: RoleContextAssemblyRequest): ResolvedRoleRuntimeConfiguration;
}

export interface DefaultRoleContextAssemblerOptions {
  providerCapabilityRegistry?: ProviderCapabilityRegistry;
  capabilityResolver?: CapabilityResolver;
}

export class DefaultRoleContextAssembler implements RoleContextAssembler {
  readonly #providerCapabilityRegistry: ProviderCapabilityRegistry;
  readonly #capabilityResolver: CapabilityResolver;

  constructor(options: DefaultRoleContextAssemblerOptions = {}) {
    this.#providerCapabilityRegistry =
      options.providerCapabilityRegistry ?? new WorkspaceProviderCapabilityRegistry();
    this.#capabilityResolver = options.capabilityResolver ?? new WorkspaceCapabilityResolver();
  }

  assemble({
    workspace,
    participant,
    roleId,
    scope,
    runtimeGeneration,
    resolveCredential,
    memoryRecall = [],
    recoveryContext,
  }: RoleContextAssemblyRequest): ResolvedRoleRuntimeConfiguration {
    if (participant.participantId !== roleId || participant.scope !== scope) {
      throw new Error("Participant identity or scope does not match the role command");
    }
    if (scope === "long_term") {
      if (
        participant.scope !== "long_term" ||
        !workspace.roles.some((role) => role.roleProfileId === participant.roleProfileId) ||
        participant.retentionPolicy !== "retain_profile"
      ) {
        throw new Error("Long-term role manifest is incomplete");
      }
    } else if (
      participant.scope !== "temporary" ||
      participant.invitation.status !== "accepted" ||
      !["delete_after_session", "review_at_close", "promote_candidate"].includes(
        participant.retentionPolicy,
      )
    ) {
      throw new Error("Temporary role invitation is incomplete");
    }

    const providerModel = this.#providerCapabilityRegistry.resolve({
      workspace,
      modelRoute: participant.modelRouteSnapshot,
    });
    const apiKey = resolveCredential(providerModel.credentialRef);
    if (apiKey === undefined || apiKey.length === 0) {
      throw new Error("Provider credential is unavailable");
    }
    const capabilities = this.#capabilityResolver.resolve({
      workspace,
      policy: participant.capabilitiesSnapshot,
      resolveCredential,
      networkAccess: participant.delegationSnapshot.networkAccess,
    });
    const plugins = resolvePiPluginSet(capabilities.skillPaths, capabilities.mcpServers);
    const frozenMemory = freezeMemoryRecall(memoryRecall);
    if (capabilities.webSearch !== undefined &&
        !providerModel.modelCapabilities.includes("tool_calling")) {
      throw new Error("Participant web search grant requires a tool-capable model");
    }
    if (recoveryContext !== undefined &&
        (recoveryContext.includes("\u0000") || Buffer.byteLength(recoveryContext, "utf8") > 192 * 1024)) {
      throw new Error("Role recovery context exceeds the private runtime limit");
    }

    return {
      displayName: participant.displayName,
      providerId: providerModel.providerId,
      providerName: providerModel.providerName,
      apiFamily: providerModel.apiFamily,
      ...(providerModel.endpoint === undefined ? {} : { endpoint: providerModel.endpoint }),
      modelId: providerModel.modelId,
      modelName: providerModel.modelName,
      modelCapabilities: [...providerModel.modelCapabilities],
      ...(providerModel.contextWindow === undefined
        ? {}
        : { contextWindow: providerModel.contextWindow }),
      ...(providerModel.maxOutputTokens === undefined
        ? {}
        : { maxOutputTokens: providerModel.maxOutputTokens }),
      ...(providerModel.thinkingLevel === undefined
        ? {}
        : { thinkingLevel: providerModel.thinkingLevel }),
      // Keep the raw frozen prompt here. The default Pi adapter adds the stable
      // role prefix exactly once at its own boundary.
      systemPrompt: participant.systemPromptSnapshot,
      skillPaths: [...plugins.skillPaths],
      frozenMemory,
      ...(recoveryContext === undefined ? {} : { recoveryContext }),
      ...(capabilities.webSearch === undefined ? {} : { webSearch: { ...capabilities.webSearch } }),
      credentialLease: new RoleCredentialLease({
        roleId,
        runtimeGeneration,
        providerId: providerModel.providerId,
        apiKey,
        mcpServers: plugins.mcpServers,
      }),
      delegation: {
        networkAccess: participant.delegationSnapshot.networkAccess,
        resultMode: participant.delegationSnapshot.resultMode,
        maxConcurrentSubagents: Math.min(
          2,
          participant.delegationSnapshot.maxConcurrentSubagents,
        ),
      },
    };
  }
}

function freezeMemoryRecall(memoryRecall: readonly FrozenRoleMemory[]): readonly FrozenRoleMemory[] {
  if (memoryRecall.length > 4) {
    throw new Error("Role memory recall exceeds the item limit");
  }
  let characters = 0;
  const refs = new Set<string>();
  return Object.freeze(memoryRecall.map((memory) => {
    const memoryId = memory.memoryId.trim();
    if (!/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/u.test(memoryId) ||
        !Number.isSafeInteger(memory.revision) || memory.revision < 1 ||
        memory.content.length === 0 || memory.content.includes("\u0000")) {
      throw new Error("Role memory recall contains an invalid revision");
    }
    characters += memory.content.length;
    if (characters > 6_000 || !refs.add(`${memoryId}@${memory.revision}`)) {
      throw new Error("Role memory recall exceeds its budget or contains duplicates");
    }
    return Object.freeze({ memoryId, revision: memory.revision, content: memory.content });
  }));
}
