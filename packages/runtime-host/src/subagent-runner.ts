import type { RuntimeEvent } from "./runtime-adapter.js";
import { PiRuntimeAdapter } from "./pi-runtime-adapter.js";
import type { ApiFamily, ModelCapability } from "@pi-roundtable/protocol";

export interface SubagentRunRequest {
  subagentId: string;
  parentRoleId: string;
  providerId: string;
  providerName: string;
  apiFamily: ApiFamily;
  endpoint?: string;
  modelId: string;
  modelName: string;
  modelCapabilities: ModelCapability[];
  contextWindow?: number;
  maxOutputTokens?: number;
  apiKey: string;
  cwd: string;
  systemPrompt: string;
  skillPaths: string[];
  task: string;
}

export interface SubagentRunProgress {
  updateCount: number;
}

export interface SubagentRunner {
  run(
    request: SubagentRunRequest,
    onProgress: (progress: SubagentRunProgress) => void,
    signal: AbortSignal,
  ): Promise<string>;
}

export class PiSubagentRunner implements SubagentRunner {
  async run(
    request: SubagentRunRequest,
    onProgress: (progress: SubagentRunProgress) => void,
    signal: AbortSignal,
  ): Promise<string> {
    const adapter = new PiRuntimeAdapter({
      roleId: request.parentRoleId,
      runtimeId: `subagent-runtime:${request.subagentId}`,
      sessionId: `subagent-session:${request.subagentId}`,
      providerId: request.providerId,
      providerName: request.providerName,
      apiFamily: request.apiFamily,
      ...(request.endpoint === undefined ? {} : { endpoint: request.endpoint }),
      modelId: request.modelId,
      modelName: request.modelName,
      modelCapabilities: request.modelCapabilities,
      ...(request.contextWindow === undefined ? {} : { contextWindow: request.contextWindow }),
      ...(request.maxOutputTokens === undefined
        ? {}
        : { maxOutputTokens: request.maxOutputTokens }),
      cwd: request.cwd,
      tools: [],
      systemPrompt: [
        request.systemPrompt,
        "You are an isolated Pi Roundtable SubAgent. Complete only the delegated task.",
        "You cannot create nested SubAgents. Return a concise result to the parent role.",
      ].join("\n\n"),
      skillPaths: request.skillPaths,
      mcpServers: [],
      credentialProvider: {
        resolveApiKey: async (providerId) =>
          providerId === request.providerId ? request.apiKey : undefined,
      },
    });
    let output = "";
    let updateCount = 0;
    let terminalResolve!: () => void;
    let terminalReject!: (error: Error) => void;
    const terminal = new Promise<void>((resolve, reject) => {
      terminalResolve = resolve;
      terminalReject = reject;
    });
    const unsubscribe = adapter.subscribe((event: RuntimeEvent) => {
      if (event.kind === "turn.delta" && typeof event.payload.delta === "string") {
        output = (output + event.payload.delta).slice(-32_768);
        updateCount += 1;
        onProgress({ updateCount });
      } else if (event.kind === "turn.completed") {
        terminalResolve();
      } else if (event.kind === "runtime.failed") {
        terminalReject(new Error("The isolated Pi SubAgent failed"));
      }
    });
    const abort = (): void => {
      terminalReject(new Error("The isolated Pi SubAgent was cancelled"));
      void adapter.stop();
    };
    signal.addEventListener("abort", abort, { once: true });
    try {
      await adapter.start();
      const receipt = await adapter.execute({
        kind: "turn.prompt",
        commandId: `subagent-task:${request.subagentId}`,
        roleId: request.parentRoleId,
        message: request.task,
        delivery: "immediate",
      });
      if (!receipt.accepted) {
        throw new Error("The isolated Pi SubAgent rejected its task");
      }
      await terminal;
      return output.trim().length > 0 ? output.trim() : "SubAgent completed without a text result.";
    } finally {
      signal.removeEventListener("abort", abort);
      unsubscribe();
      await adapter.stop();
    }
  }
}
