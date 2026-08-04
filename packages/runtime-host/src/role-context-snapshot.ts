import { createHash } from "node:crypto";

import type { PrefixInvalidationCause } from "./provider-cache-adapter.js";

export interface RoleContextTurnV1 {
  readonly turnId: string;
  readonly visibility: "public" | "private";
  readonly content: string;
}

export interface RoleContextToolResultV1 {
  readonly toolCallId: string;
  readonly toolName: string;
  readonly content: string;
}

export interface RoleContextSnapshotV1 {
  readonly snapshotVersion: 1;
  readonly meetingId: string;
  readonly roleId: string;
  readonly runtimeGeneration: number;
  readonly sourceSequence: number;
  readonly policyVersion: string;
  readonly stableRolePrefix: string;
  readonly sessionFrozenMemoryContext: readonly string[];
  readonly dynamicAgendaRouting: readonly string[];
  readonly recentTurns: readonly RoleContextTurnV1[];
  readonly largeToolResults: readonly RoleContextToolResultV1[];
  readonly providerPrivateState: Readonly<Record<string, string | number | boolean>>;
  readonly prefixFingerprint: string;
  readonly prefixBytes: number;
  readonly estimatedPrefixTokens: number;
}

export interface RoleContextSnapshotInput extends Omit<
  RoleContextSnapshotV1,
  "snapshotVersion" | "prefixFingerprint" | "prefixBytes" | "estimatedPrefixTokens"
> {}

export interface RoleContextSnapshotExpectation {
  readonly meetingId: string;
  readonly roleId: string;
  readonly runtimeGeneration: number;
  readonly sourceSequence: number;
  readonly policyVersion: string;
  readonly prefixFingerprint: string;
}

export type RoleContextSnapshotRejection =
  | "meeting_mismatch"
  | "role_mismatch"
  | "generation_mismatch"
  | "sequence_mismatch"
  | "policy_mismatch"
  | "prefix_mismatch"
  | "snapshot_corrupt";

const MAX_STABLE_PREFIX_BYTES = 256 * 1024;
const MAX_MEMORY_ENTRIES = 64;
const MAX_MEMORY_BYTES = 256 * 1024;
const MAX_DYNAMIC_ITEMS = 128;
const MAX_DYNAMIC_BYTES = 256 * 1024;
const MAX_RECENT_TURNS = 256;
const MAX_RECENT_BYTES = 2 * 1024 * 1024;
const MAX_TOOL_RESULTS = 128;
const MAX_TOOL_BYTES = 1024 * 1024;

export function createRoleContextSnapshot(input: RoleContextSnapshotInput): RoleContextSnapshotV1 {
  const meetingId = requireIdentity(input.meetingId, "meetingId");
  const roleId = requireIdentity(input.roleId, "roleId");
  const runtimeGeneration = requirePositiveSafeInteger(input.runtimeGeneration, "runtimeGeneration");
  const sourceSequence = requireNonNegativeSafeInteger(input.sourceSequence, "sourceSequence");
  const policyVersion = requireIdentity(input.policyVersion, "policyVersion");
  const stableRolePrefix = requireBoundedText(
    input.stableRolePrefix,
    MAX_STABLE_PREFIX_BYTES,
    "stableRolePrefix",
  );
  const sessionFrozenMemoryContext = freezeTextArray(
    input.sessionFrozenMemoryContext,
    MAX_MEMORY_ENTRIES,
    MAX_MEMORY_BYTES,
    "sessionFrozenMemoryContext",
  );
  const dynamicAgendaRouting = freezeTextArray(
    input.dynamicAgendaRouting,
    MAX_DYNAMIC_ITEMS,
    MAX_DYNAMIC_BYTES,
    "dynamicAgendaRouting",
  );
  const recentTurns = freezeTurns(input.recentTurns);
  const largeToolResults = freezeToolResults(input.largeToolResults);
  const providerPrivateState = freezeProviderState(input.providerPrivateState);
  const prefixPayload = encodeLengthPrefixed([stableRolePrefix, ...sessionFrozenMemoryContext]);
  const prefixBytes = prefixPayload.byteLength;
  const prefixFingerprint = createHash("sha256").update(prefixPayload).digest("hex");

  return Object.freeze({
    snapshotVersion: 1 as const,
    meetingId,
    roleId,
    runtimeGeneration,
    sourceSequence,
    policyVersion,
    stableRolePrefix,
    sessionFrozenMemoryContext,
    dynamicAgendaRouting,
    recentTurns,
    largeToolResults,
    providerPrivateState,
    prefixFingerprint,
    prefixBytes,
    estimatedPrefixTokens: Math.ceil(prefixBytes / 4),
  });
}

export function validateRoleContextSnapshot(
  snapshot: RoleContextSnapshotV1,
  expected: RoleContextSnapshotExpectation,
): { readonly accepted: true } | { readonly accepted: false; readonly reason: RoleContextSnapshotRejection } {
  let rebuilt: RoleContextSnapshotV1;
  try {
    rebuilt = createRoleContextSnapshot(snapshot);
  } catch {
    return { accepted: false, reason: "snapshot_corrupt" };
  }
  if (rebuilt.prefixFingerprint !== snapshot.prefixFingerprint || rebuilt.prefixBytes !== snapshot.prefixBytes) {
    return { accepted: false, reason: "snapshot_corrupt" };
  }
  if (snapshot.meetingId !== expected.meetingId) {
    return { accepted: false, reason: "meeting_mismatch" };
  }
  if (snapshot.roleId !== expected.roleId) {
    return { accepted: false, reason: "role_mismatch" };
  }
  if (snapshot.runtimeGeneration !== expected.runtimeGeneration) {
    return { accepted: false, reason: "generation_mismatch" };
  }
  if (snapshot.sourceSequence !== expected.sourceSequence) {
    return { accepted: false, reason: "sequence_mismatch" };
  }
  if (snapshot.policyVersion !== expected.policyVersion) {
    return { accepted: false, reason: "policy_mismatch" };
  }
  if (snapshot.prefixFingerprint !== expected.prefixFingerprint) {
    return { accepted: false, reason: "prefix_mismatch" };
  }
  return { accepted: true };
}

export function classifyPrefixInvalidation(
  previous: RoleContextSnapshotV1 | undefined,
  next: RoleContextSnapshotV1,
): PrefixInvalidationCause {
  if (previous === undefined) {
    return "initial_session";
  }
  if (previous.roleId !== next.roleId || previous.meetingId !== next.meetingId) {
    return "session_reset";
  }
  if (previous.policyVersion !== next.policyVersion) {
    return "policy_changed";
  }
  if (previous.stableRolePrefix !== next.stableRolePrefix) {
    return "role_prompt_changed";
  }
  if (!sameStrings(previous.sessionFrozenMemoryContext, next.sessionFrozenMemoryContext)) {
    return "memory_changed";
  }
  return previous.prefixFingerprint === next.prefixFingerprint ? "manual" : "session_reset";
}

function freezeTurns(turns: readonly RoleContextTurnV1[]): readonly RoleContextTurnV1[] {
  if (turns.length > MAX_RECENT_TURNS) {
    throw new RangeError("recentTurns exceeds the item limit");
  }
  let bytes = 0;
  const result = turns.map((turn) => {
    const content = requireBoundedText(turn.content, MAX_RECENT_BYTES, "recentTurn.content");
    if (turn.visibility !== "public" && turn.visibility !== "private") {
      throw new TypeError("recentTurn.visibility must be public or private");
    }
    bytes += Buffer.byteLength(content, "utf8");
    return Object.freeze({
      turnId: requireIdentity(turn.turnId, "turnId"),
      visibility: turn.visibility,
      content,
    });
  });
  if (bytes > MAX_RECENT_BYTES) {
    throw new RangeError("recentTurns exceeds the byte limit");
  }
  return Object.freeze(result);
}

function freezeToolResults(results: readonly RoleContextToolResultV1[]): readonly RoleContextToolResultV1[] {
  if (results.length > MAX_TOOL_RESULTS) {
    throw new RangeError("largeToolResults exceeds the item limit");
  }
  let bytes = 0;
  const frozen = results.map((result) => {
    const content = requireBoundedText(result.content, MAX_TOOL_BYTES, "toolResult.content");
    bytes += Buffer.byteLength(content, "utf8");
    return Object.freeze({
      toolCallId: requireIdentity(result.toolCallId, "toolCallId"),
      toolName: requireIdentity(result.toolName, "toolName"),
      content,
    });
  });
  if (bytes > MAX_TOOL_BYTES) {
    throw new RangeError("largeToolResults exceeds the byte limit");
  }
  return Object.freeze(frozen);
}

function freezeTextArray(
  values: readonly string[],
  maxItems: number,
  maxBytes: number,
  name: string,
): readonly string[] {
  if (values.length > maxItems) {
    throw new RangeError(`${name} exceeds the item limit`);
  }
  let bytes = 0;
  const result = values.map((value) => {
    const normalized = requireBoundedText(value, maxBytes, name);
    bytes += Buffer.byteLength(normalized, "utf8");
    return normalized;
  });
  if (bytes > maxBytes) {
    throw new RangeError(`${name} exceeds the byte limit`);
  }
  return Object.freeze(result);
}

function freezeProviderState(
  state: Readonly<Record<string, string | number | boolean>>,
): Readonly<Record<string, string | number | boolean>> {
  const entries = Object.entries(state);
  if (entries.length > 64) {
    throw new RangeError("providerPrivateState exceeds the field limit");
  }
  const result: Record<string, string | number | boolean> = Object.create(null);
  for (const [key, value] of entries.sort(([left], [right]) => left.localeCompare(right))) {
    const safeKey = requireIdentity(key, "providerPrivateState key");
    if (typeof value !== "string" && typeof value !== "number" && typeof value !== "boolean") {
      throw new TypeError("providerPrivateState values must be scalar");
    }
    if (typeof value === "number" && !Number.isFinite(value)) {
      throw new TypeError("providerPrivateState numbers must be finite");
    }
    if (typeof value === "string" && Buffer.byteLength(value, "utf8") > 4096) {
      throw new RangeError("providerPrivateState string exceeds the byte limit");
    }
    result[safeKey] = value;
  }
  return Object.freeze(result);
}

function encodeLengthPrefixed(values: readonly string[]): Buffer {
  const buffers: Buffer[] = [];
  for (const value of values) {
    const content = Buffer.from(value, "utf8");
    const length = Buffer.allocUnsafe(4);
    length.writeUInt32BE(content.byteLength);
    buffers.push(length, content);
  }
  return Buffer.concat(buffers);
}

function requireBoundedText(value: string, maxBytes: number, name: string): string {
  if (typeof value !== "string" || value.includes("\u0000")) {
    throw new TypeError(`${name} must be text without NUL characters`);
  }
  if (Buffer.byteLength(value, "utf8") > maxBytes) {
    throw new RangeError(`${name} exceeds the byte limit`);
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

function requirePositiveSafeInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 1) {
    throw new RangeError(`${name} must be a positive safe integer`);
  }
  return value;
}

function requireNonNegativeSafeInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${name} must be a non-negative safe integer`);
  }
  return value;
}

function sameStrings(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}
