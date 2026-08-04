import type { RuntimeAdapter, RuntimeEvent } from "./runtime-adapter.js";
import {
  PiRuntimeAdapter,
  type PiRuntimeAdapterOptions,
} from "./pi-runtime-adapter.js";
import type { ApiFamily, ModelCapability, ThinkingLevel } from "@pi-roundtable/protocol";

export interface SubagentRunRequest {
  subagentId: string;
  parentRoleId: string;
  runtimeGeneration: number;
  providerId: string;
  providerName: string;
  apiFamily: ApiFamily;
  endpoint?: string;
  modelId: string;
  modelName: string;
  modelCapabilities: ModelCapability[];
  contextWindow?: number;
  maxOutputTokens?: number;
  thinkingLevel?: ThinkingLevel;
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

export type SubagentRuntimeAdapterFactory = (
  options: PiRuntimeAdapterOptions,
) => RuntimeAdapter;

export class PiSubagentRunner implements SubagentRunner {
  readonly #adapterFactory: SubagentRuntimeAdapterFactory;

  constructor(
    adapterFactory: SubagentRuntimeAdapterFactory = (options) => new PiRuntimeAdapter(options),
  ) {
    this.#adapterFactory = adapterFactory;
  }

  async run(
    request: SubagentRunRequest,
    onProgress: (progress: SubagentRunProgress) => void,
    signal: AbortSignal,
  ): Promise<string> {
    signal.throwIfAborted();
    if (!Number.isSafeInteger(request.runtimeGeneration) || request.runtimeGeneration < 1) {
      throw new RangeError("runtimeGeneration must be a positive safe integer");
    }
    const adapter = this.#adapterFactory({
      roleId: request.parentRoleId,
      runtimeId: `subagent-runtime:${request.runtimeGeneration}:${request.subagentId}`,
      sessionId: `subagent-session.${request.runtimeGeneration}.${request.subagentId}`,
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
      ...(request.thinkingLevel === undefined
        ? {}
        : { thinkingLevel: request.thinkingLevel }),
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
    let abortReject!: (error: Error) => void;
    const aborted = new Promise<never>((_resolve, reject) => {
      abortReject = reject;
    });
    // Startup failures emit runtime.failed before adapter.start() rejects. Attach
    // a handler immediately so that this early terminal rejection cannot become
    // an unhandled rejection while run() is still awaiting adapter.start().
    void terminal.catch(() => undefined);
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
    let abortHandled = false;
    const abort = (): void => {
      if (abortHandled) {
        return;
      }
      abortHandled = true;
      const error = new Error("The isolated Pi SubAgent was cancelled");
      terminalReject(error);
      abortReject(error);
      this.#requestStopWithoutWaiting(adapter);
    };
    signal.addEventListener("abort", abort, { once: true });
    try {
      if (signal.aborted) {
        abort();
        signal.throwIfAborted();
      }
      await Promise.race([adapter.start(), aborted]);
      const receipt = await Promise.race([
        adapter.execute({
          kind: "turn.prompt",
          commandId: `subagent-task:${request.subagentId}`,
          roleId: request.parentRoleId,
          message: request.task,
          delivery: "immediate",
        }),
        aborted,
      ]);
      if (!receipt.accepted) {
        throw new Error("The isolated Pi SubAgent rejected its task");
      }
      await terminal;
      return output.trim().length > 0 ? output.trim() : "SubAgent completed without a text result.";
    } finally {
      signal.removeEventListener("abort", abort);
      unsubscribe();
      if (abortHandled) {
        // abort() already requested cancellation without waiting for a possibly
        // unbounded startup Promise.
      } else {
        await adapter.stop();
      }
    }
  }

  #requestStopWithoutWaiting(adapter: RuntimeAdapter): void {
    try {
      void adapter.stop().catch(() => undefined);
    } catch {
      // The caller observes cancellation, not a secondary cleanup failure.
    }
  }
}
