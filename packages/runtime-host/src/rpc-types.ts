import type { JsonObject, JsonValue } from "@pi-roundtable/protocol";

export type RpcRecord = Record<string, unknown>;
export type RpcFrameListener = (frame: RpcRecord) => void;

export interface RpcReadyFrame extends RpcRecord {
  type: "ready";
  protocolVersion: number;
  supportedProtocolVersions: number[];
  maxFrameBytes: number;
  maxReassembledFrameBytes: number;
}

export interface RpcResponse<T extends JsonValue | undefined = JsonValue | undefined>
  extends RpcRecord {
  id?: string;
  type: "response";
  command: string;
  success: boolean;
  data?: T;
  error?: string;
}

export type StreamingBehavior = "steer" | "followUp";
export type SubagentSubscription = "off" | "progress" | "events";
export type InterruptMode = "immediate" | "wait";

export interface RpcCommandFields extends JsonObject {
  id?: never;
  type?: never;
}

export function isRpcReadyFrame(frame: RpcRecord): frame is RpcReadyFrame {
  return (
    frame.type === "ready" &&
    Number.isInteger(frame.protocolVersion) &&
    Array.isArray(frame.supportedProtocolVersions) &&
    frame.supportedProtocolVersions.every(Number.isInteger) &&
    Number.isInteger(frame.maxFrameBytes) &&
    Number.isInteger(frame.maxReassembledFrameBytes)
  );
}

export function isRpcResponse(frame: RpcRecord): frame is RpcResponse {
  return (
    frame.type === "response" &&
    typeof frame.command === "string" &&
    typeof frame.success === "boolean"
  );
}
