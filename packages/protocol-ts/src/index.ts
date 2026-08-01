export const PROTOCOL_VERSION = 1 as const;

export * from "./configuration.js";
export * from "./validation.js";

export type ProtocolVersion = typeof PROTOCOL_VERSION;
export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export type MeetingEventKind =
  | "runtime.lease_acquired"
  | "runtime.lease_released"
  | "meeting.opened"
  | "meeting.closed"
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
  | "tool.completed"
  | "tool.failed"
  | "subagent.spawned"
  | "subagent.progress"
  | "subagent.completed"
  | "subagent.failed";

export const MEETING_EVENT_KINDS = [
  "runtime.lease_acquired",
  "runtime.lease_released",
  "meeting.opened",
  "meeting.closed",
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
  "tool.completed",
  "tool.failed",
  "subagent.spawned",
  "subagent.progress",
  "subagent.completed",
  "subagent.failed",
] as const satisfies readonly MeetingEventKind[];

const MEETING_EVENT_KIND_SET: ReadonlySet<string> = new Set(MEETING_EVENT_KINDS);

export function isMeetingEventKind(value: unknown): value is MeetingEventKind {
  return typeof value === "string" && MEETING_EVENT_KIND_SET.has(value);
}

export interface MeetingEvent {
  protocolVersion: ProtocolVersion;
  meetingId: string;
  eventId: string;
  sequence: number;
  runtimeGeneration: number;
  kind: MeetingEventKind;
  occurredAt: string;
  actorId?: string | null;
  targetId?: string | null;
  causationId?: string | null;
  payload: JsonObject;
}

export type MeetingCommandKind =
  | "meeting.open"
  | "meeting.close"
  | "role.add"
  | "role.create_temporary"
  | "role.promote"
  | "role.archive"
  | "role.remove"
  | "speech.prompt"
  | "speech.interrupt"
  | "generation.cancel"
  | "subagent.spawn"
  | "tool.invoke";

export const MEETING_COMMAND_KINDS = [
  "meeting.open",
  "meeting.close",
  "role.add",
  "role.create_temporary",
  "role.promote",
  "role.archive",
  "role.remove",
  "speech.prompt",
  "speech.interrupt",
  "generation.cancel",
  "subagent.spawn",
  "tool.invoke",
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
