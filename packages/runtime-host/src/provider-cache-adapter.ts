import type { ProviderCapabilityProfileV1 } from "./provider-capability-profile.js";
import type { ProviderUsageSampleV1 } from "./provider-usage.js";

export type CacheRetention = "none" | "short" | "long";
export type PrefixInvalidationCause =
  | "initial_session"
  | "role_prompt_changed"
  | "memory_changed"
  | "provider_changed"
  | "model_changed"
  | "policy_changed"
  | "session_reset"
  | "manual";

export interface ProviderCacheRequestPolicyV1 {
  readonly policyVersion: 1;
  readonly mode: ProviderCapabilityProfileV1["cacheMode"];
  readonly cacheRetention: CacheRetention;
  readonly sessionId?: string;
  readonly sendsExplicitCacheHints: boolean;
  readonly safeNoOp: boolean;
}

export interface ProviderCacheDiagnosticV1 {
  readonly diagnosticVersion: 1;
  readonly kind: "provider_cache";
  readonly providerId: string;
  readonly modelId: string;
  readonly mode: ProviderCapabilityProfileV1["cacheMode"];
  readonly supported: boolean;
  readonly eligible: boolean;
  readonly stablePrefixBytes: number;
  readonly invalidationCause: PrefixInvalidationCause;
  readonly inputTokens?: number;
  readonly cacheReadTokens?: number;
  readonly cacheWriteTokens?: number;
  readonly hitRate?: number;
  readonly tokenSavingsEstimate?: number;
}

export function resolveProviderCacheRequestPolicy(
  profile: ProviderCapabilityProfileV1,
  requestedRetention: CacheRetention,
  sessionId: string,
): ProviderCacheRequestPolicyV1 {
  if (sessionId.trim().length === 0) {
    throw new TypeError("sessionId must not be empty");
  }
  const supportsExplicitPiHints = profile.cacheMode === "automatic_prefix" ||
    profile.cacheMode === "explicit_breakpoints";
  const cacheRetention = supportsExplicitPiHints ? requestedRetention : "none";
  return Object.freeze({
    policyVersion: 1 as const,
    mode: profile.cacheMode,
    cacheRetention,
    ...(cacheRetention === "none" ? {} : { sessionId }),
    sendsExplicitCacheHints: cacheRetention !== "none",
    safeNoOp: profile.cacheMode === "unsupported",
  });
}

export function createProviderCacheDiagnostic(
  profile: ProviderCapabilityProfileV1,
  stablePrefixBytes: number,
  invalidationCause: PrefixInvalidationCause,
  usage?: ProviderUsageSampleV1,
): ProviderCacheDiagnosticV1 {
  if (!Number.isSafeInteger(stablePrefixBytes) || stablePrefixBytes < 0) {
    throw new RangeError("stablePrefixBytes must be a non-negative safe integer");
  }
  const supported = profile.supportsPromptCache;
  const eligible = supported && stablePrefixBytes > 0;
  const inputTokens = usage?.inputTokens;
  const cacheReadTokens = usage?.cacheReadTokens;
  const cacheWriteTokens = usage?.cacheWriteTokens;
  const cacheEligibleInputTokens = resolveCacheEligibleInputTokens(
    profile,
    usage,
    inputTokens,
    cacheReadTokens,
    cacheWriteTokens,
  );
  const hitRate = cacheEligibleInputTokens !== undefined && cacheEligibleInputTokens > 0 &&
      cacheReadTokens !== undefined
    ? Math.min(1, cacheReadTokens / cacheEligibleInputTokens)
    : undefined;
  return Object.freeze({
    diagnosticVersion: 1 as const,
    kind: "provider_cache" as const,
    providerId: profile.providerId,
    modelId: profile.modelId,
    mode: profile.cacheMode,
    supported,
    eligible,
    stablePrefixBytes,
    invalidationCause,
    ...(inputTokens === undefined ? {} : { inputTokens }),
    ...(cacheReadTokens === undefined ? {} : { cacheReadTokens }),
    ...(cacheWriteTokens === undefined ? {} : { cacheWriteTokens }),
    ...(hitRate === undefined ? {} : { hitRate }),
    ...(cacheReadTokens === undefined ? {} : { tokenSavingsEstimate: cacheReadTokens }),
  });
}

function resolveCacheEligibleInputTokens(
  profile: ProviderCapabilityProfileV1,
  usage: ProviderUsageSampleV1 | undefined,
  inputTokens: number | undefined,
  cacheReadTokens: number | undefined,
  cacheWriteTokens: number | undefined,
): number | undefined {
  if (usage === undefined || inputTokens === undefined || cacheReadTokens === undefined) {
    return undefined;
  }
  // Pi's normalized Usage.input excludes cache reads and writes. Anthropic's
  // raw input count has the same accounting. Missing components remain unknown
  // because assuming zero would inflate the displayed hit rate.
  if (usage.source === "sdk_normalized" || profile.family === "anthropic") {
    if (cacheWriteTokens === undefined) {
      return undefined;
    }
    return inputTokens + cacheReadTokens + cacheWriteTokens;
  }
  // OpenAI and DeepSeek raw prompt-token counts include the cached subset.
  if (usage.source === "raw_provider" &&
      (profile.family === "openai" || profile.family === "deepseek")) {
    return inputTokens;
  }
  // Compatible and synthetic shapes have no portable accounting guarantee.
  return undefined;
}
