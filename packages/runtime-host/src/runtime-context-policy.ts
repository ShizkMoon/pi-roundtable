import type { CacheRetention } from "@earendil-works/pi-ai";

/**
 * Runtime-only controls for keeping a role session within its model window.
 *
 * The protocol deliberately does not expose these knobs: they describe how the
 * Windows runtime executes a frozen participant manifest, not meeting state.
 */
export interface RuntimeContextPolicyOptions {
  autoCompaction?: boolean;
  /** Fraction of the model window consumed before Pi starts compaction. */
  compactAtRatio?: number;
  /** Fraction of the model window retained verbatim after compaction. */
  keepRecentRatio?: number;
  /** Provider hint. Providers without prompt caching safely ignore it. */
  cacheRetention?: CacheRetention;
}

/** Concrete values passed to a Pi session after policy validation. */
export interface ResolvedRuntimeContextPolicy {
  autoCompaction: boolean;
  contextWindow: number;
  compactAtTokens: number;
  reserveTokens: number;
  keepRecentTokens: number;
  cacheRetention: CacheRetention;
}

const DEFAULT_CONTEXT_WINDOW = 128_000;
const DEFAULT_COMPACT_AT_RATIO = 0.62;
const DEFAULT_KEEP_RECENT_RATIO = 0.20;

/**
 * Resolve ratios once when a role starts so all later turns share the same
 * stable cache prefix and deterministic compaction thresholds.
 */
export function resolveRuntimeContextPolicy(
  contextWindow = DEFAULT_CONTEXT_WINDOW,
  options: RuntimeContextPolicyOptions = {},
): ResolvedRuntimeContextPolicy {
  assertIntegerInRange("contextWindow", contextWindow, 4_096, 10_000_000);
  const compactAtRatio = options.compactAtRatio ?? DEFAULT_COMPACT_AT_RATIO;
  const keepRecentRatio = options.keepRecentRatio ?? DEFAULT_KEEP_RECENT_RATIO;
  assertRatio("compactAtRatio", compactAtRatio, 0.40, 0.90);
  assertRatio("keepRecentRatio", keepRecentRatio, 0.05, 0.40);
  if (keepRecentRatio >= compactAtRatio) {
    throw new Error("keepRecentRatio must be lower than compactAtRatio");
  }

  const compactAtTokens = Math.floor(contextWindow * compactAtRatio);
  return {
    autoCompaction: options.autoCompaction ?? true,
    contextWindow,
    compactAtTokens,
    reserveTokens: contextWindow - compactAtTokens,
    keepRecentTokens: Math.floor(contextWindow * keepRecentRatio),
    cacheRetention: options.cacheRetention ?? "short",
  };
}

/**
 * Build the immutable role prefix shared by every request in one Pi session.
 * Dynamic meeting state belongs in user turns; keeping it out of this prefix
 * lets providers reuse the longest possible prefix without stale instructions.
 */
export function buildStableRoleSystemPrompt(
  basePrompt: string,
  roleId: string,
  displayName: string,
): string {
  const boundary = [
    "[Pi Roundtable stable role boundary]",
    `Your immutable meeting identity is ${displayName} (${roleId}).`,
    "Answer only from this role's own perspective and responsibilities.",
    "Never draft, simulate, summarize as, or create answer sections on behalf of another role.",
    "A host message may contain shared requirements plus separate @role assignments. Apply shared requirements, then perform only assignments addressed to your display name or role id.",
    "Do not perform assignments addressed to other roles. When no assignment addresses you, answer only the shared request.",
    "You may reference or disagree with public statements, but label only your own position.",
    "Keep private conversations private.",
  ].join("\n");
  const normalizedBase = basePrompt.trim();
  return normalizedBase.length === 0 ? boundary : `${normalizedBase}\n\n${boundary}`;
}

function assertRatio(name: string, value: number, minimum: number, maximum: number): void {
  if (!Number.isFinite(value) || value < minimum || value > maximum) {
    throw new Error(`${name} must be between ${minimum} and ${maximum}`);
  }
}

function assertIntegerInRange(
  name: string,
  value: number,
  minimum: number,
  maximum: number,
): void {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`${name} must be an integer between ${minimum} and ${maximum}`);
  }
}
