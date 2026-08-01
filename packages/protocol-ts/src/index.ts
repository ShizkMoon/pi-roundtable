export const PROTOCOL_VERSION = 1 as const;

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
  | "role.remove"
  | "speech.prompt"
  | "speech.interrupt"
  | "generation.cancel"
  | "subagent.spawn"
  | "tool.invoke";

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
