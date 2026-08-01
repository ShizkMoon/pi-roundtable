import { randomUUID } from "node:crypto";

import {
  createAgentSession,
  ModelRuntime,
  SessionManager,
  type AgentSessionEvent,
  type CreateAgentSessionOptions,
  type PromptOptions,
} from "@earendil-works/pi-coding-agent";
import type { Credential, CredentialInfo, CredentialStore } from "@earendil-works/pi-ai";

import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "./runtime-adapter.js";

export interface RuntimeCredentialProvider {
  resolveApiKey(providerId: string): Promise<string | undefined>;
}

export interface PiSessionHandle {
  readonly sessionId: string;
  readonly isStreaming: boolean;
  getActiveToolNames(): string[];
  subscribe(listener: (event: AgentSessionEvent) => void): () => void;
  prompt(text: string, options?: PromptOptions): Promise<void>;
  steer(text: string): Promise<void>;
  followUp(text: string): Promise<void>;
  abort(): Promise<void>;
  dispose(): void;
}

export interface PiSessionCreateOptions {
  providerId: string;
  modelId: string;
  cwd: string;
  agentDir?: string;
  sessionId: string;
  tools: readonly string[];
  apiKey: string;
}

export interface PiSessionFactory {
  create(options: PiSessionCreateOptions): Promise<PiSessionHandle>;
}

export interface PiRuntimeAdapterOptions {
  roleId: string;
  providerId: string;
  modelId: string;
  credentialProvider: RuntimeCredentialProvider;
  runtimeId?: string;
  sessionId?: string;
  cwd?: string;
  agentDir?: string;
  tools?: readonly string[];
  sessionFactory?: PiSessionFactory;
  now?: () => Date;
}

export class PiRuntimeError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "PiRuntimeError";
  }
}

type AdapterState = "stopped" | "starting" | "running" | "stopping";

interface RememberedCommand {
  fingerprint: string;
  result: RuntimeCommandResult;
}

class MemoryCredentialStore implements CredentialStore {
  readonly #credentials = new Map<string, Credential>();

  async read(providerId: string): Promise<Credential | undefined> {
    return this.#credentials.get(providerId);
  }

  async list(): Promise<readonly CredentialInfo[]> {
    return [];
  }

  async modify(
    providerId: string,
    update: (current: Credential | undefined) => Promise<Credential | undefined>,
  ): Promise<Credential | undefined> {
    const current = this.#credentials.get(providerId);
    const next = await update(current);
    if (next !== undefined) {
      this.#credentials.set(providerId, next);
      return next;
    }
    return current;
  }

  async delete(providerId: string): Promise<void> {
    this.#credentials.delete(providerId);
  }
}

const DEFAULT_PI_SESSION_FACTORY: PiSessionFactory = {
  async create(options): Promise<PiSessionHandle> {
    const modelRuntime = await ModelRuntime.create({
      credentials: new MemoryCredentialStore(),
      allowModelNetwork: false,
    });
    await modelRuntime.setRuntimeApiKey(options.providerId, options.apiKey, {
      allowNetwork: false,
    });
    const model = modelRuntime.getModel(options.providerId, options.modelId);
    if (model === undefined) {
      throw new PiRuntimeError(
        "model_not_found",
        `Pi model is not available: ${options.providerId}/${options.modelId}`,
      );
    }

    const createOptions: CreateAgentSessionOptions = {
      cwd: options.cwd,
      modelRuntime,
      model,
      sessionManager: SessionManager.inMemory(options.cwd, { id: options.sessionId }),
    };
    if (options.agentDir !== undefined) {
      createOptions.agentDir = options.agentDir;
    }
    if (options.tools.length === 0) {
      createOptions.noTools = "all";
    } else {
      createOptions.tools = [...options.tools];
    }

    const { session } = await createAgentSession(createOptions);
    return session;
  },
};

export class PiRuntimeAdapter implements RuntimeAdapter {
  readonly #options: PiRuntimeAdapterOptions;
  readonly #runtimeId: string;
  readonly #sessionId: string;
  readonly #listeners = new Set<RuntimeEventListener>();
  readonly #commandResults = new Map<string, RememberedCommand>();
  readonly #now: () => Date;
  #state: AdapterState = "stopped";
  #startPromise: Promise<RuntimeSessionInfo> | undefined;
  #stopPromise: Promise<void> | undefined;
  #session: PiSessionHandle | undefined;
  #unsubscribeSession: (() => void) | undefined;
  #promptDispatchPending = false;
  #turnCancellationPending = false;
  #turnCancellationEmitted = false;
  #turnFailed = false;
  #turnFailureTerminalEmitted = false;
  #activeTurnCommandId: string | undefined;

  constructor(options: PiRuntimeAdapterOptions) {
    this.#options = options;
    this.#runtimeId = options.runtimeId ?? randomUUID();
    this.#sessionId = options.sessionId ?? randomUUID();
    this.#now = options.now ?? (() => new Date());
  }

  start(): Promise<RuntimeSessionInfo> {
    if (this.#state !== "stopped") {
      return Promise.reject(
        new PiRuntimeError("already_started", "Pi runtime adapter is already started"),
      );
    }

    this.#state = "starting";
    const operation = this.#startSession();
    this.#startPromise = operation;
    void operation.then(
      () => {
        if (this.#startPromise === operation) {
          this.#startPromise = undefined;
        }
      },
      () => {
        if (this.#startPromise === operation) {
          this.#startPromise = undefined;
        }
      },
    );
    return operation;
  }

  stop(): Promise<void> {
    if (this.#stopPromise !== undefined) {
      return this.#stopPromise;
    }

    const operation = this.#stopSession();
    this.#stopPromise = operation;
    void operation.then(
      () => {
        if (this.#stopPromise === operation) {
          this.#stopPromise = undefined;
        }
      },
      () => {
        if (this.#stopPromise === operation) {
          this.#stopPromise = undefined;
        }
      },
    );
    return operation;
  }

  subscribe(listener: RuntimeEventListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  async execute(command: RuntimeCommand): Promise<RuntimeCommandResult> {
    const fingerprint = this.#commandFingerprint(command);
    const cached = this.#commandResults.get(command.commandId);
    if (cached !== undefined) {
      if (cached.fingerprint !== fingerprint) {
        return {
          commandId: command.commandId,
          accepted: false,
          errorCode: "command_id_conflict",
          message: "The command ID was already used with a different command",
        };
      }
      return cached.result;
    }
    const remember = (result: RuntimeCommandResult): RuntimeCommandResult =>
      this.#remember(fingerprint, result);

    const session = this.#session;
    if (this.#state !== "running" || session === undefined) {
      return remember({
        commandId: command.commandId,
        accepted: false,
        errorCode: "runtime_not_started",
        message: "Pi runtime adapter is not started",
      });
    }
    if (command.roleId !== this.#options.roleId) {
      return remember({
        commandId: command.commandId,
        accepted: false,
        errorCode: "role_mismatch",
        message: `Runtime owns role ${this.#options.roleId}, not ${command.roleId}`,
      });
    }

    if (command.kind === "subagent.subscription") {
      return remember({
        commandId: command.commandId,
        accepted: false,
        errorCode: "unsupported_command",
        message: "The direct Pi adapter has no built-in subagent subscription",
      });
    }

    if (command.kind === "turn.cancel") {
      const previousCancellationPending = this.#turnCancellationPending;
      const previousCancellationEmitted = this.#turnCancellationEmitted;
      if (session.isStreaming || this.#promptDispatchPending) {
        this.#turnCancellationPending = true;
        this.#turnCancellationEmitted = false;
      }
      try {
        await session.abort();
        return remember({ commandId: command.commandId, accepted: true });
      } catch (error) {
        this.#turnCancellationPending = previousCancellationPending;
        this.#turnCancellationEmitted = previousCancellationEmitted;
        return this.#rememberFailure(command.commandId, fingerprint, error);
      }
    }

    if (
      (command.delivery === "immediate" && session.isStreaming) ||
      (!session.isStreaming && this.#promptDispatchPending)
    ) {
      return remember({
        commandId: command.commandId,
        accepted: false,
        errorCode: "runtime_busy",
        message: "Use steer or follow_up while the role is already running",
      });
    }

    let startsTurn = false;
    try {
      let invocation: Promise<void>;
      if (!session.isStreaming || command.delivery === "immediate") {
        startsTurn = true;
        this.#clearCancellationOutcome();
        this.#turnFailed = false;
        this.#turnFailureTerminalEmitted = false;
        this.#activeTurnCommandId = command.commandId;
        this.#promptDispatchPending = true;
        invocation = session.prompt(command.message);
      } else if (command.delivery === "steer") {
        invocation = session.steer(command.message);
      } else {
        invocation = session.followUp(command.message);
      }
      void invocation.then(
        () => {
          if (startsTurn && this.#session === session) {
            this.#promptDispatchPending = false;
            this.#clearCancellationOutcome();
          }
        },
        (error: unknown) => {
          if (this.#session === session && this.#state === "running") {
            if (startsTurn) {
              this.#promptDispatchPending = false;
              this.#clearCancellationOutcome();
              if (!this.#turnFailureTerminalEmitted) {
                this.#turnFailureTerminalEmitted = true;
                this.#emit("turn.cancelled", {}, command.commandId);
              }
              this.#activeTurnCommandId = undefined;
            }
            this.#emitFailure(error, command.commandId);
          }
        },
      );
      return remember({ commandId: command.commandId, accepted: true });
    } catch (error) {
      if (startsTurn) {
        this.#promptDispatchPending = false;
        this.#activeTurnCommandId = undefined;
      }
      return this.#rememberFailure(command.commandId, fingerprint, error);
    }
  }

  async #startSession(): Promise<RuntimeSessionInfo> {
    let session: PiSessionHandle | undefined;
    let unsubscribe: (() => void) | undefined;

    try {
      const apiKey = await this.#options.credentialProvider.resolveApiKey(
        this.#options.providerId,
      );
      if (apiKey === undefined || apiKey.length === 0) {
        throw new PiRuntimeError(
          "credential_unavailable",
          `No runtime credential is available for provider ${this.#options.providerId}`,
        );
      }

      const factory = this.#options.sessionFactory ?? DEFAULT_PI_SESSION_FACTORY;
      const createOptions: PiSessionCreateOptions = {
        providerId: this.#options.providerId,
        modelId: this.#options.modelId,
        cwd: this.#options.cwd ?? process.cwd(),
        sessionId: this.#sessionId,
        tools: [...(this.#options.tools ?? [])],
        apiKey,
      };
      if (this.#options.agentDir !== undefined) {
        createOptions.agentDir = this.#options.agentDir;
      }

      session = await factory.create(createOptions);
      if (session.sessionId !== this.#sessionId) {
        throw new PiRuntimeError(
          "session_mismatch",
          `Pi returned session ${session.sessionId}; expected ${this.#sessionId}`,
        );
      }
      const createdSession = session;
      unsubscribe = createdSession.subscribe((event) =>
        this.#onPiEvent(event, createdSession),
      );
      if (this.#state !== "starting") {
        throw new PiRuntimeError(
          "start_cancelled",
          "Pi runtime adapter was stopped before startup completed",
        );
      }
      this.#session = session;
      this.#unsubscribeSession = unsubscribe;
      this.#state = "running";

      const info = this.#sessionInfo(session);
      this.#emit("runtime.ready", { engine: "pi" });
      return info;
    } catch (error) {
      unsubscribe?.();
      session?.dispose();
      const cancelled = this.#state === "stopping";
      if (this.#state === "starting") {
        this.#state = "stopped";
      }
      if (!cancelled) {
        this.#emitFailure(error);
      }
      throw error;
    }
  }

  async #stopSession(): Promise<void> {
    if (this.#state === "stopped") {
      return;
    }
    if (this.#state === "starting") {
      this.#state = "stopping";
      try {
        await this.#startPromise;
      } catch {
        // Startup observes the stopping state and disposes any session it created.
      }
      this.#state = "stopped";
      this.#emit("runtime.stopped", {});
      return;
    }

    this.#state = "stopping";
    const session = this.#session;
    if (session === undefined) {
      this.#state = "stopped";
      return;
    }

    this.#session = undefined;
    this.#unsubscribeSession?.();
    this.#unsubscribeSession = undefined;
    const shouldAbort = session.isStreaming || this.#promptDispatchPending;
    this.#promptDispatchPending = false;
    this.#clearCancellationOutcome();
    this.#turnFailed = false;
    this.#turnFailureTerminalEmitted = false;
    this.#activeTurnCommandId = undefined;
    try {
      if (shouldAbort) {
        await session.abort();
      }
    } finally {
      try {
        session.dispose();
      } finally {
        this.#state = "stopped";
        this.#emit("runtime.stopped", {});
      }
    }
  }

  #sessionInfo(session: PiSessionHandle): RuntimeSessionInfo {
    return {
      runtimeId: this.#runtimeId,
      sessionId: this.#sessionId,
      engine: "pi",
      capabilities: {
        steering: true,
        followUp: true,
        cancellation: true,
        tools: session.getActiveToolNames().length > 0,
        subagents: false,
      },
    };
  }

  #onPiEvent(event: AgentSessionEvent, sourceSession: PiSessionHandle): void {
    if (this.#state !== "running" || this.#session !== sourceSession) {
      return;
    }

    switch (event.type) {
      case "turn_start":
        this.#turnFailed = false;
        this.#turnFailureTerminalEmitted = false;
        this.#emit("turn.started", {}, this.#activeTurnCommandId);
        break;
      case "message_update": {
        const update = event.assistantMessageEvent;
        if (update.type === "text_delta") {
          this.#emit("turn.delta", { delta: update.delta }, this.#activeTurnCommandId);
        } else if (update.type === "error") {
          if (update.reason === "aborted") {
            this.#turnCancellationPending = true;
            if (!this.#turnCancellationEmitted) {
              this.#turnCancellationEmitted = true;
              this.#emit("turn.cancelled", {}, this.#activeTurnCommandId);
            }
          } else {
            this.#turnFailed = true;
            this.#emit("runtime.failed", {
              errorCode: "pi_response_error",
              message: "Pi provider response failed",
            }, this.#activeTurnCommandId);
          }
        }
        break;
      }
      case "turn_end":
        if (this.#turnCancellationPending) {
          if (!this.#turnCancellationEmitted) {
            this.#emit("turn.cancelled", {}, this.#activeTurnCommandId);
          }
        } else if (this.#turnFailed) {
          if (!this.#turnFailureTerminalEmitted) {
            this.#turnFailureTerminalEmitted = true;
            this.#emit("turn.cancelled", {}, this.#activeTurnCommandId);
          }
        } else if (!this.#turnFailed) {
          this.#emit("turn.completed", {}, this.#activeTurnCommandId);
        }
        this.#clearCancellationOutcome();
        this.#turnFailed = false;
        this.#turnFailureTerminalEmitted = false;
        this.#activeTurnCommandId = undefined;
        break;
      case "tool_execution_start":
        this.#emit("tool.started", {
          toolCallId: event.toolCallId,
          toolName: event.toolName,
        }, this.#activeTurnCommandId);
        break;
      case "tool_execution_update":
        this.#emit("tool.progress", {
          toolCallId: event.toolCallId,
          toolName: event.toolName,
        }, this.#activeTurnCommandId);
        break;
      case "tool_execution_end":
        this.#emit(event.isError ? "tool.failed" : "tool.completed", {
          toolCallId: event.toolCallId,
          toolName: event.toolName,
          isError: event.isError,
        }, this.#activeTurnCommandId);
        break;
      case "auto_retry_end":
        if (!event.success) {
          this.#turnFailed = true;
          this.#emit("runtime.failed", {
            errorCode: "pi_retry_exhausted",
            message: "Pi automatic retry was exhausted",
          }, this.#activeTurnCommandId);
        }
        break;
      default:
        break;
    }
  }

  #emit(
    kind: RuntimeEvent["kind"],
    payload: RuntimeEvent["payload"],
    correlationId?: string,
  ): void {
    const event: RuntimeEvent = {
      kind,
      runtimeId: this.#runtimeId,
      sessionId: this.#sessionId,
      occurredAt: this.#now().toISOString(),
      roleId: this.#options.roleId,
      payload,
    };
    if (correlationId !== undefined) {
      event.correlationId = correlationId;
    }
    for (const listener of this.#listeners) {
      try {
        listener(event);
      } catch {
        // A presentation subscriber cannot corrupt the authoritative runtime lifecycle.
      }
    }
  }

  #emitFailure(error: unknown, correlationId?: string): void {
    const normalized = this.#normalizeError(error);
    this.#emit(
      "runtime.failed",
      { errorCode: normalized.code, message: normalized.message },
      correlationId,
    );
  }

  #clearCancellationOutcome(): void {
    this.#turnCancellationPending = false;
    this.#turnCancellationEmitted = false;
  }

  #rememberFailure(
    commandId: string,
    fingerprint: string,
    error: unknown,
  ): RuntimeCommandResult {
    const normalized = this.#normalizeError(error);
    return this.#remember(fingerprint, {
      commandId,
      accepted: false,
      errorCode: normalized.code,
      message: normalized.message,
    });
  }

  #normalizeError(error: unknown): { code: string; message: string } {
    if (error instanceof PiRuntimeError) {
      return { code: error.code, message: this.#safeErrorMessage(error.code) };
    }
    return { code: "pi_runtime_error", message: "Pi runtime operation failed" };
  }

  #safeErrorMessage(code: string): string {
    switch (code) {
      case "model_not_found":
        return "The requested Pi model is unavailable";
      case "credential_unavailable":
        return "No runtime credential is available";
      case "session_mismatch":
        return "Pi returned an unexpected session";
      case "already_started":
        return "Pi runtime adapter is already started";
      case "start_cancelled":
        return "Pi runtime adapter startup was cancelled";
      default:
        return "Pi runtime operation failed";
    }
  }

  #commandFingerprint(command: RuntimeCommand): string {
    switch (command.kind) {
      case "turn.prompt":
        return [
          command.kind,
          command.roleId,
          command.delivery,
          command.message,
        ].join("\u0000");
      case "turn.cancel":
        return [command.kind, command.roleId].join("\u0000");
      case "subagent.subscription":
        return [command.kind, command.roleId, command.level].join("\u0000");
    }
  }

  #remember(fingerprint: string, result: RuntimeCommandResult): RuntimeCommandResult {
    if (this.#commandResults.size >= 1_024) {
      const oldest = this.#commandResults.keys().next().value as string | undefined;
      if (oldest !== undefined) {
        this.#commandResults.delete(oldest);
      }
    }
    this.#commandResults.set(result.commandId, { fingerprint, result });
    return result;
  }
}
