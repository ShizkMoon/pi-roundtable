import type {
  ApiFamily,
  ModelCapability,
  ModelRoute,
  ThinkingLevel,
  WorkspaceProfile,
} from "@pi-roundtable/protocol";

/**
 * Provider/model metadata that the runtime host may pass to an adapter.
 * Credentials are represented only by reference until a role lease is built.
 */
export interface ResolvedProviderModelRoute {
  providerId: string;
  providerName: string;
  apiFamily: ApiFamily;
  endpoint?: string;
  credentialRef: string;
  modelId: string;
  modelName: string;
  modelCapabilities: ModelCapability[];
  contextWindow?: number;
  maxOutputTokens?: number;
  thinkingLevel?: ThinkingLevel;
}

export interface ProviderCapabilityResolutionRequest {
  workspace: WorkspaceProfile;
  modelRoute: ModelRoute;
}

/**
 * Offline provider capability lookup. Implementations must not read secrets,
 * start runtimes, or infer capabilities from a live provider response.
 */
export interface ProviderCapabilityRegistry {
  resolve(request: ProviderCapabilityResolutionRequest): ResolvedProviderModelRoute;
}

/** Resolves the currently supported primary workspace model route. */
export class WorkspaceProviderCapabilityRegistry implements ProviderCapabilityRegistry {
  resolve({
    workspace,
    modelRoute,
  }: ProviderCapabilityResolutionRequest): ResolvedProviderModelRoute {
    // Fallback routes are intentionally not activated in v0.4. Preserving that
    // boundary avoids silently changing provider selection or retry behavior.
    const model = workspace.models.find(
      (candidate) =>
        candidate.modelProfileId === modelRoute.primaryModelProfileId && candidate.enabled,
    );
    const provider = model === undefined
      ? undefined
      : workspace.providers.find(
          (candidate) =>
            candidate.providerProfileId === model.providerProfileId && candidate.enabled,
        );
    if (model === undefined || provider === undefined) {
      throw new Error("Participant model route cannot be resolved");
    }

    return {
      providerId: provider.runtimeProviderId,
      providerName: provider.displayName,
      apiFamily: provider.apiFamily,
      ...(provider.endpoint === undefined ? {} : { endpoint: provider.endpoint }),
      credentialRef: provider.credentialRef,
      modelId: model.modelId,
      modelName: model.displayName,
      modelCapabilities: [...model.capabilities],
      ...(model.contextWindow === undefined ? {} : { contextWindow: model.contextWindow }),
      ...(modelRoute.maxOutputTokens === undefined
        ? {}
        : { maxOutputTokens: modelRoute.maxOutputTokens }),
      thinkingLevel: modelRoute.thinkingLevel,
    };
  }
}
