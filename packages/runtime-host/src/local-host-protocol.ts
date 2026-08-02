import type {
  CommandReceipt,
  MeetingCommand,
  MeetingEvent,
  RoundtableSession,
  WorkspaceProfile,
} from "@pi-roundtable/protocol";
import type { RuntimeCapabilities } from "./runtime-adapter.js";

export const LOCAL_HOST_PROTOCOL_VERSION = 3 as const;
export const MAX_LOCAL_HOST_LINE_BYTES = 1_048_576;

export interface LocalHostInitializeFrame {
  type: "initialize";
  requestId: string;
  workspace: WorkspaceProfile;
  session: RoundtableSession;
  credentials: Record<string, string>;
  initialSequence: number;
}

export interface LocalHostCommandFrame {
  type: "command";
  command: MeetingCommand;
}

export interface LocalHostShutdownFrame {
  type: "shutdown";
  requestId: string;
  mode: "suspend" | "close";
}

export type LocalHostInputFrame =
  | LocalHostInitializeFrame
  | LocalHostCommandFrame
  | LocalHostShutdownFrame;

export interface LocalHostReadyFrame {
  type: "ready";
  protocolVersion: typeof LOCAL_HOST_PROTOCOL_VERSION;
  meetingId: string;
  runtimeId: string;
  runtimeGeneration: number;
  sequence: number;
  capabilities: RuntimeCapabilities;
}

export interface LocalHostReceiptFrame {
  type: "receipt";
  receipt: CommandReceipt;
}

export interface LocalHostEventFrame {
  type: "event";
  event: MeetingEvent;
}

export interface LocalHostErrorFrame {
  type: "error";
  requestId: string | null;
  errorCode: string;
  message: string;
}

export interface LocalHostStoppedFrame {
  type: "stopped";
  requestId: string | null;
}

export type LocalHostOutputFrame =
  | LocalHostReadyFrame
  | LocalHostReceiptFrame
  | LocalHostEventFrame
  | LocalHostErrorFrame
  | LocalHostStoppedFrame;

export class LocalHostProtocolError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly requestId: string | null = null,
  ) {
    super(message);
    this.name = "LocalHostProtocolError";
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readRequestId(value: Record<string, unknown>): string | null {
  return typeof value.requestId === "string" && value.requestId.length > 0
    ? value.requestId
    : null;
}

export function parseLocalHostInput(line: string): LocalHostInputFrame {
  if (Buffer.byteLength(line, "utf8") > MAX_LOCAL_HOST_LINE_BYTES) {
    throw new LocalHostProtocolError(
      "frame_too_large",
      `Local Runtime Host frames cannot exceed ${MAX_LOCAL_HOST_LINE_BYTES} bytes`,
    );
  }

  let value: unknown;
  try {
    value = JSON.parse(line) as unknown;
  } catch {
    throw new LocalHostProtocolError("invalid_json", "Input is not valid JSON");
  }
  if (!isRecord(value)) {
    throw new LocalHostProtocolError("invalid_frame", "Input frame must be an object");
  }

  if (value.type === "shutdown") {
    const requestId = readRequestId(value);
    if (requestId === null) {
      throw new LocalHostProtocolError(
        "invalid_frame",
        "Shutdown frames require a non-empty requestId",
      );
    }
    if (value.mode !== "suspend" && value.mode !== "close") {
      throw new LocalHostProtocolError(
        "invalid_frame",
        "Shutdown frames require mode suspend or close",
        requestId,
      );
    }
    return { type: "shutdown", requestId, mode: value.mode };
  }

  if (value.type === "initialize") {
    const requestId = readRequestId(value);
    if (
      requestId === null ||
      !isRecord(value.workspace) ||
      !isRecord(value.session) ||
      !isRecord(value.credentials) ||
      !Number.isSafeInteger(value.initialSequence) ||
      (value.initialSequence as number) < 0
    ) {
      throw new LocalHostProtocolError(
        "invalid_frame",
        "Initialize frames require requestId, workspace, session, credentials, and a non-negative initialSequence",
        requestId,
      );
    }
    const credentials: Record<string, string> = {};
    for (const [reference, secret] of Object.entries(value.credentials)) {
      if (typeof secret !== "string" || secret.length === 0) {
        throw new LocalHostProtocolError(
          "invalid_frame",
          "Initialize credential values must be non-empty strings",
          requestId,
        );
      }
      credentials[reference] = secret;
    }
    return {
      type: "initialize",
      requestId,
      workspace: value.workspace as unknown as WorkspaceProfile,
      session: value.session as unknown as RoundtableSession,
      credentials,
      initialSequence: value.initialSequence as number,
    };
  }

  if (value.type !== "command" || !isRecord(value.command)) {
    throw new LocalHostProtocolError(
      "invalid_frame",
      "Expected an initialize, command, or shutdown frame",
      readRequestId(value),
    );
  }

  return { type: "command", command: value.command as unknown as MeetingCommand };
}
