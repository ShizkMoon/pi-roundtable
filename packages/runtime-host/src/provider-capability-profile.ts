import type { ApiFamily } from "@pi-roundtable/protocol";

export type ProviderFamily = "openai" | "anthropic" | "deepseek" | "compatible" | "unknown";
export type ProviderCacheMode =
  | "automatic_prefix"
  | "explicit_breakpoints"
  | "automatic_disk"
  | "unsupported";
export type ContextWindowSource =
  | "runtime_metadata"
  | "declared"
  | "conservative_min"
  | "conservative_fallback";

export interface ProviderCapabilityProfileV1 {
  readonly profileVersion: 1;
  readonly providerId: string;
  readonly modelId: string;
  readonly apiFamily: ApiFamily;
  readonly family: ProviderFamily;
  readonly cacheMode: ProviderCacheMode;
  readonly supportsPromptCache: boolean;
  readonly supportsCacheReadUsage: boolean;
  readonly supportsCacheWriteUsage: boolean;
  readonly supportsStreamingUsage: boolean;
  readonly resolvedContextWindow: number;
  readonly contextWindowSource: ContextWindowSource;
}

export interface ProviderCapabilityResolutionInput {
  readonly providerId: string;
  readonly modelId: string;
  readonly apiFamily: ApiFamily;
  readonly endpoint?: string;
  readonly declaredContextWindow?: number;
  readonly runtimeContextWindow?: number;
  /** Explicitly reviewed capability for a compatible endpoint; never inferred from a display name. */
  readonly compatibleCacheMode?: Exclude<ProviderCacheMode, "unsupported">;
}

const CONSERVATIVE_CONTEXT_WINDOW = 32_768;

export function resolveProviderCapabilityProfile(
  input: ProviderCapabilityResolutionInput,
): ProviderCapabilityProfileV1 {
  const providerId = requireIdentity(input.providerId, "providerId");
  const modelId = requireIdentity(input.modelId, "modelId");
  const officialFamily = resolveOfficialFamily(providerId, input.endpoint);
  const family: ProviderFamily = officialFamily ??
    (input.apiFamily === "custom" ? "unknown" : "compatible");
  const cacheMode = resolveCacheMode(family, input.compatibleCacheMode);
  const { resolvedContextWindow, contextWindowSource } = resolveContextWindow(
    input.declaredContextWindow,
    input.runtimeContextWindow,
  );

  return Object.freeze({
    profileVersion: 1 as const,
    providerId,
    modelId,
    apiFamily: input.apiFamily,
    family,
    cacheMode,
    supportsPromptCache: cacheMode !== "unsupported",
    supportsCacheReadUsage: cacheMode !== "unsupported",
    supportsCacheWriteUsage: cacheMode === "automatic_prefix" || cacheMode === "explicit_breakpoints",
    supportsStreamingUsage: family === "openai" || family === "anthropic" || family === "deepseek",
    resolvedContextWindow,
    contextWindowSource,
  });
}

function resolveOfficialFamily(providerId: string, endpoint: string | undefined):
  "openai" | "anthropic" | "deepseek" | undefined {
  const normalized = providerId.toLowerCase();
  if (normalized === "openai" && isOfficialEndpoint(endpoint, ["api.openai.com"])) {
    return "openai";
  }
  if (normalized === "anthropic" && isOfficialEndpoint(endpoint, ["api.anthropic.com"])) {
    return "anthropic";
  }
  if (normalized === "deepseek" && isOfficialEndpoint(endpoint, ["api.deepseek.com"])) {
    return "deepseek";
  }
  return undefined;
}

function isOfficialEndpoint(endpoint: string | undefined, hostnames: readonly string[]): boolean {
  if (endpoint === undefined) {
    return true;
  }
  try {
    const parsed = new URL(endpoint);
    return parsed.protocol === "https:" && hostnames.includes(parsed.hostname.toLowerCase());
  } catch {
    return false;
  }
}

function resolveCacheMode(
  family: ProviderFamily,
  compatibleMode: ProviderCapabilityResolutionInput["compatibleCacheMode"],
): ProviderCacheMode {
  switch (family) {
    case "openai":
      return "automatic_prefix";
    case "anthropic":
      return "explicit_breakpoints";
    case "deepseek":
      return "automatic_disk";
    case "compatible":
      return compatibleMode ?? "unsupported";
    case "unknown":
      return "unsupported";
  }
}

function resolveContextWindow(
  declared: number | undefined,
  runtime: number | undefined,
): Pick<ProviderCapabilityProfileV1, "resolvedContextWindow" | "contextWindowSource"> {
  const declaredWindow = optionalPositiveSafeInteger(declared, "declaredContextWindow");
  const runtimeWindow = optionalPositiveSafeInteger(runtime, "runtimeContextWindow");
  if (declaredWindow !== undefined && runtimeWindow !== undefined) {
    return declaredWindow === runtimeWindow
      ? { resolvedContextWindow: runtimeWindow, contextWindowSource: "runtime_metadata" }
      : {
          resolvedContextWindow: Math.min(declaredWindow, runtimeWindow),
          contextWindowSource: "conservative_min",
        };
  }
  if (runtimeWindow !== undefined) {
    return { resolvedContextWindow: runtimeWindow, contextWindowSource: "runtime_metadata" };
  }
  if (declaredWindow !== undefined) {
    return { resolvedContextWindow: declaredWindow, contextWindowSource: "declared" };
  }
  return {
    resolvedContextWindow: CONSERVATIVE_CONTEXT_WINDOW,
    contextWindowSource: "conservative_fallback",
  };
}

function optionalPositiveSafeInteger(value: number | undefined, name: string): number | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (!Number.isSafeInteger(value) || value < 1) {
    throw new RangeError(`${name} must be a positive safe integer`);
  }
  return value;
}

function requireIdentity(value: string, name: string): string {
  const normalized = value.trim();
  if (normalized.length === 0 || normalized.length > 256 || /[\u0000\r\n]/.test(normalized)) {
    throw new TypeError(`${name} must be a bounded non-empty identity`);
  }
  return normalized;
}
