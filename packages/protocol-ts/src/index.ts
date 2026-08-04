export const PROTOCOL_VERSION = 1 as const;

export * from "./configuration.js";
export * from "./validation.js";
export * from "./session-export.js";

export type ProtocolVersion = typeof PROTOCOL_VERSION;
export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export type MeetingEventKind =
  | "runtime.lease_acquired"
  | "runtime.lease_released"
  | "meeting.opened"
  | "meeting.closed"
  | "message.published"
  | "message.direct_sent"
  | "role.registered"
  | "role.temporary_registered"
  | "role.promoted"
  | "role.archived"
  | "role.left"
  | "speech.started"
  | "speech.delta"
  | "speech.completed"
  | "speech.cancelled"
  | "interruption.requested"
  | "tool.started"
  | "tool.approval_requested"
  | "tool.approval_resolved"
  | "tool.completed"
  | "tool.failed"
  | "subagent.spawned"
  | "subagent.progress"
  | "subagent.completed"
  | "subagent.failed"
  | "discussion.configured"
  | "discussion.mode_changed"
  | "agenda.item_changed"
  | "floor.requested"
  | "floor.granted"
  | "floor.rejected"
  | "discussion.budget_updated"
  | "convergence.recorded";

export const MEETING_EVENT_KINDS = [
  "runtime.lease_acquired",
  "runtime.lease_released",
  "meeting.opened",
  "meeting.closed",
  "message.published",
  "message.direct_sent",
  "role.registered",
  "role.temporary_registered",
  "role.promoted",
  "role.archived",
  "role.left",
  "speech.started",
  "speech.delta",
  "speech.completed",
  "speech.cancelled",
  "interruption.requested",
  "tool.started",
  "tool.approval_requested",
  "tool.approval_resolved",
  "tool.completed",
  "tool.failed",
  "subagent.spawned",
  "subagent.progress",
  "subagent.completed",
  "subagent.failed",
  "discussion.configured",
  "discussion.mode_changed",
  "agenda.item_changed",
  "floor.requested",
  "floor.granted",
  "floor.rejected",
  "discussion.budget_updated",
  "convergence.recorded",
] as const satisfies readonly MeetingEventKind[];

const MEETING_EVENT_KIND_SET: ReadonlySet<string> = new Set(MEETING_EVENT_KINDS);
const MEETING_EVENT_KIND_PATTERN = /^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$/;

export function isMeetingEventKind(value: unknown): value is MeetingEventKind {
  return typeof value === "string" && MEETING_EVENT_KIND_SET.has(value);
}

/** Validates the additive namespaced event-kind syntax used on the v1 wire. */
export function isValidMeetingEventKind(value: unknown): value is string {
  return typeof value === "string" &&
    value.length <= 128 &&
    MEETING_EVENT_KIND_PATTERN.test(value);
}

export interface MeetingEvent {
  protocolVersion: ProtocolVersion;
  meetingId: string;
  eventId: string;
  sequence: number;
  runtimeGeneration: number;
  kind: string;
  occurredAt: string;
  actorId?: string | null;
  targetId?: string | null;
  causationId?: string | null;
  visibility: "public" | "private";
  audience?: string[];
  payload: JsonObject;
}

export function canObserveMeetingEvent(event: MeetingEvent, principalId: string): boolean {
  return event.visibility !== "private" || event.audience?.includes(principalId) === true;
}

export interface MeetingEnvelopeValidationIssue {
  path: string;
  code:
    | "additional_property"
    | "duplicate_item"
    | "invalid_format"
    | "invalid_type"
    | "invalid_value"
    | "missing_property";
  message: string;
}

export type MeetingCommandKind =
  | "meeting.open"
  | "meeting.close"
  | "role.add"
  | "role.create_temporary"
  | "role.promote"
  | "role.archive"
  | "role.remove"
  | "speech.broadcast"
  | "speech.direct"
  | "speech.prompt"
  | "speech.interrupt"
  | "generation.cancel"
  | "subagent.spawn"
  | "tool.approval.resolve"
  | "tool.invoke"
  | "discussion.configure"
  | "discussion.mode.set"
  | "discussion.resume"
  | "agenda.advance"
  | "floor.request"
  | "floor.grant"
  | "floor.reject"
  | "convergence.record";

export const MEETING_COMMAND_KINDS = [
  "meeting.open",
  "meeting.close",
  "role.add",
  "role.create_temporary",
  "role.promote",
  "role.archive",
  "role.remove",
  "speech.broadcast",
  "speech.direct",
  "speech.prompt",
  "speech.interrupt",
  "generation.cancel",
  "subagent.spawn",
  "tool.approval.resolve",
  "tool.invoke",
  "discussion.configure",
  "discussion.mode.set",
  "discussion.resume",
  "agenda.advance",
  "floor.request",
  "floor.grant",
  "floor.reject",
  "convergence.record",
] as const satisfies readonly MeetingCommandKind[];

const MEETING_COMMAND_KIND_SET: ReadonlySet<string> = new Set(MEETING_COMMAND_KINDS);

export function isMeetingCommandKind(value: unknown): value is MeetingCommandKind {
  return typeof value === "string" && MEETING_COMMAND_KIND_SET.has(value);
}

export interface MeetingCommand {
  protocolVersion: ProtocolVersion;
  meetingId: string;
  commandId: string;
  kind: MeetingCommandKind;
  issuedAt: string;
  expectedSequence?: number | null;
  runtimeGeneration?: number | null;
  actorId?: string | null;
  targetId?: string | null;
  payload: JsonObject;
}

const EVENT_PROPERTIES = new Set([
  "protocolVersion",
  "meetingId",
  "eventId",
  "sequence",
  "runtimeGeneration",
  "kind",
  "occurredAt",
  "actorId",
  "targetId",
  "causationId",
  "visibility",
  "audience",
  "payload",
]);
const EVENT_REQUIRED_PROPERTIES = [
  "protocolVersion",
  "meetingId",
  "eventId",
  "sequence",
  "runtimeGeneration",
  "kind",
  "occurredAt",
  "visibility",
  "payload",
] as const;
const COMMAND_PROPERTIES = new Set([
  "protocolVersion",
  "meetingId",
  "commandId",
  "kind",
  "issuedAt",
  "expectedSequence",
  "runtimeGeneration",
  "actorId",
  "targetId",
  "payload",
]);
const COMMAND_REQUIRED_PROPERTIES = [
  "protocolVersion",
  "meetingId",
  "commandId",
  "kind",
  "issuedAt",
  "payload",
] as const;
const ENVELOPE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const RFC3339_DATE_TIME_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})$/;

export function isValidMeetingEventIdentifier(value: unknown): value is string {
  return typeof value === "string" && ENVELOPE_ID_PATTERN.test(value);
}

/** Validates untrusted JSON at the public protocol boundary. */
export function validateMeetingEvent(value: unknown): MeetingEnvelopeValidationIssue[] {
  const issues: MeetingEnvelopeValidationIssue[] = [];
  const event = validateEnvelopeObject(value, EVENT_PROPERTIES, EVENT_REQUIRED_PROPERTIES, issues);
  if (event === undefined) return issues;

  validateProtocolVersion(event.protocolVersion, issues);
  validateIdentifier(event.meetingId, "meetingId", issues);
  validateIdentifier(event.eventId, "eventId", issues);
  validatePositiveInteger(event.sequence, "sequence", 1, issues);
  validatePositiveInteger(event.runtimeGeneration, "runtimeGeneration", 1, issues);
  if (!isValidMeetingEventKind(event.kind)) {
    addIssue(issues, "kind", "invalid_value", "Meeting event kind must be a valid namespaced v1 identifier.");
  }
  validateTimestamp(event.occurredAt, "occurredAt", issues);
  validateOptionalIdentifier(event.actorId, "actorId", issues);
  validateOptionalIdentifier(event.targetId, "targetId", issues);
  validateOptionalIdentifier(event.causationId, "causationId", issues);
  validateJsonObject(event.payload, "payload", issues);

  if (event.visibility !== "public" && event.visibility !== "private") {
    addIssue(issues, "visibility", "invalid_value", "Visibility must be public or private.");
  } else if (event.visibility === "public") {
    if (Object.hasOwn(event, "audience")) {
      addIssue(issues, "audience", "invalid_value", "Public events cannot carry a private audience.");
    }
  } else {
    validatePrivateAudience(event.audience, issues);
  }
  return issues;
}

/** Validates untrusted command JSON before it reaches a runtime owner. */
export function validateMeetingCommand(value: unknown): MeetingEnvelopeValidationIssue[] {
  const issues: MeetingEnvelopeValidationIssue[] = [];
  const command = validateEnvelopeObject(value, COMMAND_PROPERTIES, COMMAND_REQUIRED_PROPERTIES, issues);
  if (command === undefined) return issues;

  validateProtocolVersion(command.protocolVersion, issues);
  validateBoundedString(command.meetingId, "meetingId", issues);
  validateBoundedString(command.commandId, "commandId", issues);
  if (!isMeetingCommandKind(command.kind)) {
    addIssue(issues, "kind", "invalid_value", "Meeting command kind is not part of protocol v1.");
  }
  validateTimestamp(command.issuedAt, "issuedAt", issues);
  validateOptionalInteger(command.expectedSequence, "expectedSequence", 0, issues);
  validateOptionalInteger(command.runtimeGeneration, "runtimeGeneration", 1, issues);
  validateOptionalBoundedString(command.actorId, "actorId", issues);
  validateOptionalBoundedString(command.targetId, "targetId", issues);
  validateJsonObject(command.payload, "payload", issues);
  return issues;
}

function validateEnvelopeObject(
  value: unknown,
  allowedProperties: ReadonlySet<string>,
  requiredProperties: readonly string[],
  issues: MeetingEnvelopeValidationIssue[],
): Record<string, unknown> | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    addIssue(issues, "", "invalid_type", "Protocol envelope must be a JSON object.");
    return undefined;
  }
  const envelope = value as Record<string, unknown>;
  for (const property of Object.keys(envelope)) {
    if (!allowedProperties.has(property)) {
      addIssue(issues, property, "additional_property", `Unknown protocol property '${property}'.`);
    }
  }
  for (const property of requiredProperties) {
    if (!Object.hasOwn(envelope, property)) {
      addIssue(issues, property, "missing_property", `Required protocol property '${property}' is missing.`);
    }
  }
  return envelope;
}

function validateProtocolVersion(value: unknown, issues: MeetingEnvelopeValidationIssue[]): void {
  if (value !== PROTOCOL_VERSION) {
    addIssue(issues, "protocolVersion", "invalid_value", "Protocol version must be 1.");
  }
}

function validateIdentifier(
  value: unknown,
  path: string,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (!isValidMeetingEventIdentifier(value)) {
    addIssue(issues, path, "invalid_format", `${path} must be a protocol identifier.`);
  }
}

function validateBoundedString(
  value: unknown,
  path: string,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (typeof value !== "string" || value.length < 1 || value.length > 128) {
    addIssue(issues, path, "invalid_format", `${path} must contain 1 to 128 characters.`);
  }
}

function validateOptionalIdentifier(
  value: unknown,
  path: string,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (value !== undefined && value !== null) validateIdentifier(value, path, issues);
}

function validateOptionalBoundedString(
  value: unknown,
  path: string,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (value !== undefined && value !== null) validateBoundedString(value, path, issues);
}

function validatePositiveInteger(
  value: unknown,
  path: string,
  minimum: number,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (!Number.isSafeInteger(value) || (value as number) < minimum) {
    addIssue(issues, path, "invalid_value", `${path} must be an integer greater than or equal to ${minimum}.`);
  }
}

function validateOptionalInteger(
  value: unknown,
  path: string,
  minimum: number,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (value !== undefined && value !== null) validatePositiveInteger(value, path, minimum, issues);
}

function validateTimestamp(
  value: unknown,
  path: string,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (typeof value !== "string" || !RFC3339_DATE_TIME_PATTERN.test(value) || !Number.isFinite(Date.parse(value))) {
    addIssue(issues, path, "invalid_format", `${path} must be an ISO date-time string.`);
  }
}

function validateJsonObject(
  value: unknown,
  path: string,
  issues: MeetingEnvelopeValidationIssue[],
): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    addIssue(issues, path, "invalid_type", `${path} must be a JSON object.`);
  }
}

function validatePrivateAudience(value: unknown, issues: MeetingEnvelopeValidationIssue[]): void {
  if (!Array.isArray(value) || value.length === 0) {
    addIssue(issues, "audience", "invalid_value", "Private events require a non-empty audience.");
    return;
  }
  const unique = new Set<string>();
  for (const [index, principalId] of value.entries()) {
    validateIdentifier(principalId, `audience[${index}]`, issues);
    if (typeof principalId === "string" && unique.has(principalId)) {
      addIssue(issues, `audience[${index}]`, "duplicate_item", "Private audiences cannot contain duplicates.");
    }
    if (typeof principalId === "string") unique.add(principalId);
  }
}

function addIssue(
  issues: MeetingEnvelopeValidationIssue[],
  path: string,
  code: MeetingEnvelopeValidationIssue["code"],
  message: string,
): void {
  issues.push({ path, code, message });
}

export interface CommandReceipt {
  protocolVersion: ProtocolVersion;
  meetingId: string;
  commandId: string;
  status: "accepted" | "rejected" | "duplicate";
  acknowledgedAt: string;
  sequence?: number | null;
  errorCode?: string | null;
  message?: string | null;
}

export type RoleScope = "long_term" | "temporary";
export type RoleLifecycle = "active" | "archived";
export type DiscussionMode =
  | "agenda"
  | "free_discussion"
  | "convergence"
  | "paused"
  | "completed";

export type FloorRequestKind =
  | "host"
  | "critical"
  | "facilitator"
  | "reply"
  | "normal";

export type DiscussionProgressKind =
  | "decision"
  | "objection"
  | "evidence_request"
  | "action";

export const DISCUSSION_MODES = [
  "agenda",
  "free_discussion",
  "convergence",
  "paused",
  "completed",
] as const satisfies readonly DiscussionMode[];

export const FLOOR_REQUEST_KINDS = [
  "host",
  "critical",
  "facilitator",
  "reply",
  "normal",
] as const satisfies readonly FloorRequestKind[];

export const DISCUSSION_PROGRESS_KINDS = [
  "decision",
  "objection",
  "evidence_request",
  "action",
] as const satisfies readonly DiscussionProgressKind[];

export const ROLE_SCOPES = ["long_term", "temporary"] as const satisfies readonly RoleScope[];

const ROLE_SCOPE_SET: ReadonlySet<string> = new Set(ROLE_SCOPES);

export function isRoleScope(value: unknown): value is RoleScope {
  return typeof value === "string" && ROLE_SCOPE_SET.has(value);
}

export interface RoleSnapshot {
  roleId: string;
  displayName: string;
  status: "idle" | "thinking" | "speaking" | "tool" | "offline";
}

export interface MeetingSnapshot {
  protocolVersion: ProtocolVersion;
  meetingId: string;
  sequence: number;
  runtimeGeneration: number;
  phase: "created" | "live" | "closed";
  activeSpeakerId?: string | null;
  pendingInterruptorId?: string | null;
  roles: RoleSnapshot[];
}
