import type { JsonObject } from "@pi-roundtable/protocol";

export type RuntimeEngine = "pi" | "test";
export type RuntimeDelivery = "immediate" | "steer" | "follow_up";

export interface RuntimeCapabilities {
  steering: boolean;
  followUp: boolean;
  cancellation: boolean;
  tools: boolean;
  subagents: boolean;
}

export interface RuntimeSessionInfo {
  runtimeId: string;
  sessionId: string;
  engine: RuntimeEngine;
  capabilities: RuntimeCapabilities;
}

export type RuntimeCommand =
  | {
      kind: "turn.prompt";
      commandId: string;
      roleId: string;
      message: string;
      delivery: RuntimeDelivery;
    }
  | {
      kind: "turn.cancel";
      commandId: string;
      roleId: string;
    }
  | {
      kind: "subagent.subscription";
      commandId: string;
      roleId: string;
      level: "off" | "progress" | "events";
    }
  | {
      kind: "tool.approval.resolve";
      commandId: string;
      roleId: string;
      approvalId: string;
      approved: boolean;
    };

export type RuntimeEventKind =
  | "runtime.ready"
  | "runtime.stopped"
  | "runtime.failed"
  | "turn.started"
  | "turn.delta"
  | "turn.completed"
  | "turn.cancelled"
  | "tool.started"
  | "tool.progress"
  | "tool.approval_requested"
  | "tool.approval_resolved"
  | "tool.completed"
  | "tool.failed"
  | "subagent.started"
  | "subagent.progress"
  | "subagent.completed"
  | "subagent.failed";

export interface RuntimeEvent {
  kind: RuntimeEventKind;
  runtimeId: string;
  sessionId: string;
  occurredAt: string;
  roleId?: string | null;
  correlationId?: string | null;
  payload: JsonObject;
}

export interface RuntimeCommandResult {
  commandId: string;
  accepted: boolean;
  errorCode?: string | null;
  message?: string | null;
}

export type RuntimeEventListener = (event: RuntimeEvent) => void;

export interface RuntimeAdapter {
  start(): Promise<RuntimeSessionInfo>;
  stop(): Promise<void>;
  subscribe(listener: RuntimeEventListener): () => void;
  execute(command: RuntimeCommand): Promise<RuntimeCommandResult>;
}
