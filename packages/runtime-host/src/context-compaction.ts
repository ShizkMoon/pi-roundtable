import { createHash } from "node:crypto";

export type ContextCompactionTrigger = "manual" | "threshold" | "overflow";
export type ContextCompactionStatus = "completed" | "failed" | "aborted" | "fallback";

export interface ContextCompactionTrackerV1 {
  readonly trackerVersion: 1;
  readonly startedAtMs: number;
  readonly trigger: ContextCompactionTrigger;
  readonly triggerRatio: number;
  readonly providerId: string;
  readonly modelId: string;
  readonly roleId: string;
  readonly sessionId: string;
  readonly runtimeGeneration?: number;
}

export interface ContextCompactionResultInput {
  readonly finishedAtMs: number;
  readonly status: ContextCompactionStatus;
  readonly tokensBefore?: number;
  readonly estimatedTokensAfter?: number;
  readonly firstKeptEntryId?: string;
  readonly lastKeptEntryId?: string;
  readonly summary?: string;
  readonly failureCode?: string;
  readonly fallbackReason?: string;
  readonly willRetry?: boolean;
}

export interface ContextCompactionRecordV1 {
  readonly recordVersion: 1;
  readonly kind: "context_compaction";
  readonly trigger: ContextCompactionTrigger;
  readonly triggerRatio: number;
  readonly status: ContextCompactionStatus;
  readonly providerId: string;
  readonly modelId: string;
  readonly roleId: string;
  readonly sessionId: string;
  readonly runtimeGeneration?: number;
  readonly startedAt: string;
  readonly durationMs: number;
  readonly tokensBefore?: number;
  readonly estimatedTokensAfter?: number;
  readonly firstKeptEntryId?: string;
  readonly lastKeptEntryId?: string;
  readonly summaryDigest?: string;
  readonly failureCode?: string;
  readonly fallbackReason?: string;
  readonly willRetry: boolean;
}

export function startContextCompaction(
  input: Omit<ContextCompactionTrackerV1, "trackerVersion">,
): ContextCompactionTrackerV1 {
  if (!Number.isFinite(input.startedAtMs) || input.startedAtMs < 0) {
    throw new RangeError("startedAtMs must be a non-negative finite timestamp");
  }
  if (!Number.isFinite(input.triggerRatio) || input.triggerRatio <= 0 || input.triggerRatio > 1) {
    throw new RangeError("triggerRatio must be greater than zero and no greater than one");
  }
  return Object.freeze({
    trackerVersion: 1 as const,
    ...input,
    providerId: requireIdentity(input.providerId, "providerId"),
    modelId: requireIdentity(input.modelId, "modelId"),
    roleId: requireIdentity(input.roleId, "roleId"),
    sessionId: requireIdentity(input.sessionId, "sessionId"),
    ...(input.runtimeGeneration === undefined
      ? {}
      : { runtimeGeneration: requirePositiveSafeInteger(input.runtimeGeneration, "runtimeGeneration") }),
  });
}

export function finishContextCompaction(
  tracker: ContextCompactionTrackerV1,
  result: ContextCompactionResultInput,
): ContextCompactionRecordV1 {
  if (!Number.isFinite(result.finishedAtMs) || result.finishedAtMs < tracker.startedAtMs) {
    throw new RangeError("finishedAtMs must not precede compaction start");
  }
  const tokensBefore = optionalTokenCount(result.tokensBefore, "tokensBefore");
  const estimatedTokensAfter = optionalTokenCount(
    result.estimatedTokensAfter,
    "estimatedTokensAfter",
  );
  const summaryDigest = result.summary === undefined
    ? undefined
    : createHash("sha256").update(result.summary, "utf8").digest("hex");
  return Object.freeze({
    recordVersion: 1 as const,
    kind: "context_compaction" as const,
    trigger: tracker.trigger,
    triggerRatio: tracker.triggerRatio,
    status: result.status,
    providerId: tracker.providerId,
    modelId: tracker.modelId,
    roleId: tracker.roleId,
    sessionId: tracker.sessionId,
    ...(tracker.runtimeGeneration === undefined ? {} : { runtimeGeneration: tracker.runtimeGeneration }),
    startedAt: new Date(tracker.startedAtMs).toISOString(),
    durationMs: Math.ceil(result.finishedAtMs - tracker.startedAtMs),
    ...(tokensBefore === undefined ? {} : { tokensBefore }),
    ...(estimatedTokensAfter === undefined ? {} : { estimatedTokensAfter }),
    ...(result.firstKeptEntryId === undefined
      ? {}
      : { firstKeptEntryId: requireIdentity(result.firstKeptEntryId, "firstKeptEntryId") }),
    ...(result.lastKeptEntryId === undefined
      ? {}
      : { lastKeptEntryId: requireIdentity(result.lastKeptEntryId, "lastKeptEntryId") }),
    ...(summaryDigest === undefined ? {} : { summaryDigest }),
    ...(result.failureCode === undefined
      ? {}
      : { failureCode: requireClosedCode(result.failureCode, "failureCode") }),
    ...(result.fallbackReason === undefined
      ? {}
      : { fallbackReason: requireClosedCode(result.fallbackReason, "fallbackReason") }),
    willRetry: result.willRetry ?? false,
  });
}

function optionalTokenCount(value: number | undefined, name: string): number | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (!Number.isSafeInteger(value) || value < 0 || value > 1_000_000_000_000) {
    throw new RangeError(`${name} must be a bounded non-negative safe integer`);
  }
  return value;
}

function requirePositiveSafeInteger(value: number, name: string): number {
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

function requireClosedCode(value: string, name: string): string {
  if (!/^[a-z0-9][a-z0-9_.-]{0,63}$/.test(value)) {
    throw new TypeError(`${name} must be a closed diagnostic code`);
  }
  return value;
}
