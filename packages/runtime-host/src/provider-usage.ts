export type ProviderUsageSource = "raw_provider" | "sdk_normalized" | "synthetic_fixture";

export interface ProviderUsageSampleV1 {
  readonly sampleVersion: 1;
  readonly kind: "provider_usage";
  readonly providerId: string;
  readonly modelId: string;
  readonly observedAt: string;
  readonly source: ProviderUsageSource;
  readonly requestId?: string;
  readonly inputTokens?: number;
  readonly outputTokens?: number;
  readonly cacheReadTokens?: number;
  readonly cacheWriteTokens?: number;
  readonly totalTokens?: number;
  readonly partial: boolean;
}

export interface ProviderUsageParseContext {
  readonly providerId: string;
  readonly modelId: string;
  readonly observedAt: string;
  readonly source: ProviderUsageSource;
  readonly requestId?: string;
  readonly partial?: boolean;
}

const MAX_TOKEN_COUNT = 1_000_000_000_000;

export function parseProviderUsageSample(
  input: unknown,
  context: ProviderUsageParseContext,
): ProviderUsageSampleV1 | undefined {
  if (!isRecord(input)) {
    return undefined;
  }
  const inputTokens = readToken(input, [
    ["input_tokens"], ["prompt_tokens"], ["inputTokens"], ["promptTokens"], ["input"],
  ], context.source);
  const outputTokens = readToken(input, [
    ["output_tokens"], ["completion_tokens"], ["outputTokens"], ["completionTokens"], ["output"],
  ], context.source);
  const cacheReadTokens = readToken(input, [
    ["cache_read_input_tokens"], ["cache_read_tokens"], ["prompt_cache_hit_tokens"],
    ["cacheRead"], ["cacheReadTokens"], ["prompt_tokens_details", "cached_tokens"],
    ["promptTokensDetails", "cachedTokens"],
  ], context.source);
  const cacheWriteTokens = readToken(input, [
    ["cache_creation_input_tokens"], ["cache_write_tokens"], ["prompt_cache_write_tokens"],
    ["cacheWrite"], ["cacheWriteTokens"],
  ], context.source);
  const explicitTotal = readToken(input, [
    ["total_tokens"], ["totalTokens"], ["total"],
  ], context.source);
  const derivedTotal = explicitTotal ??
    (inputTokens !== undefined && outputTokens !== undefined
      ? checkedTokenSum(inputTokens, outputTokens)
      : undefined);
  if (
    inputTokens === undefined && outputTokens === undefined && cacheReadTokens === undefined &&
    cacheWriteTokens === undefined && derivedTotal === undefined
  ) {
    return undefined;
  }
  return Object.freeze({
    sampleVersion: 1 as const,
    kind: "provider_usage" as const,
    providerId: requireIdentity(context.providerId, "providerId"),
    modelId: requireIdentity(context.modelId, "modelId"),
    observedAt: requireTimestamp(context.observedAt),
    source: context.source,
    ...(context.requestId === undefined
      ? {}
      : { requestId: requireIdentity(context.requestId, "requestId") }),
    ...(inputTokens === undefined ? {} : { inputTokens }),
    ...(outputTokens === undefined ? {} : { outputTokens }),
    ...(cacheReadTokens === undefined ? {} : { cacheReadTokens }),
    ...(cacheWriteTokens === undefined ? {} : { cacheWriteTokens }),
    ...(derivedTotal === undefined ? {} : { totalTokens: derivedTotal }),
    partial: context.partial ?? false,
  });
}

export function mergeProviderUsageSamples(
  previous: ProviderUsageSampleV1 | undefined,
  next: ProviderUsageSampleV1,
): ProviderUsageSampleV1 {
  if (previous === undefined) {
    return next;
  }
  if (previous.providerId !== next.providerId || previous.modelId !== next.modelId) {
    throw new TypeError("Provider usage samples must describe the same provider and model");
  }
  const requestId = next.requestId ?? previous.requestId;
  return Object.freeze({
    sampleVersion: 1 as const,
    kind: "provider_usage" as const,
    providerId: next.providerId,
    modelId: next.modelId,
    observedAt: next.observedAt,
    source: next.source,
    ...(requestId === undefined ? {} : { requestId }),
    ...known("inputTokens", next.inputTokens ?? previous.inputTokens),
    ...known("outputTokens", next.outputTokens ?? previous.outputTokens),
    ...known("cacheReadTokens", next.cacheReadTokens ?? previous.cacheReadTokens),
    ...known("cacheWriteTokens", next.cacheWriteTokens ?? previous.cacheWriteTokens),
    ...known("totalTokens", next.totalTokens ?? previous.totalTokens),
    partial: next.partial,
  });
}

function readToken(
  input: Record<string, unknown>,
  paths: readonly (readonly string[])[],
  source: ProviderUsageSource,
): number | undefined {
  for (const path of paths) {
    let value: unknown = input;
    for (const segment of path) {
      value = isRecord(value) ? value[segment] : undefined;
    }
    if (value === undefined || value === null) {
      continue;
    }
    if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0 || value > MAX_TOKEN_COUNT) {
      continue;
    }
    // Pi's normalized SDK usage shape fills absent fields with zero. Preserve
    // unknown instead of turning that synthetic zero into provider evidence.
    if (source === "sdk_normalized" && value === 0) {
      continue;
    }
    return value;
  }
  return undefined;
}

function checkedTokenSum(first: number, second: number): number | undefined {
  const total = first + second;
  return Number.isSafeInteger(total) && total <= MAX_TOKEN_COUNT ? total : undefined;
}

function known<K extends keyof ProviderUsageSampleV1>(key: K, value: ProviderUsageSampleV1[K] | undefined):
  Partial<Pick<ProviderUsageSampleV1, K>> {
  return value === undefined ? {} : { [key]: value } as Partial<Pick<ProviderUsageSampleV1, K>>;
}

function requireTimestamp(value: string): string {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) {
    throw new TypeError("observedAt must be an ISO timestamp");
  }
  return new Date(parsed).toISOString();
}

function requireIdentity(value: string, name: string): string {
  const normalized = value.trim();
  if (normalized.length === 0 || normalized.length > 256 || /[\u0000\r\n]/.test(normalized)) {
    throw new TypeError(`${name} must be a bounded non-empty identity`);
  }
  return normalized;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
