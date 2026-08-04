import { randomUUID } from "node:crypto";

import {
  createAgentSession,
  DefaultResourceLoader,
  getAgentDir,
  ModelRuntime,
  SessionManager,
  SettingsManager,
  type AgentSessionEvent,
  type CreateAgentSessionOptions,
  type PromptOptions,
  type ToolDefinition,
} from "@earendil-works/pi-coding-agent";
import {
  Type,
  type Credential,
  type CredentialInfo,
  type CredentialStore,
} from "@earendil-works/pi-ai";

import type { ApiFamily, ModelCapability, ThinkingLevel } from "@pi-roundtable/protocol";

import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "./runtime-adapter.js";
import {
  McpClientManager,
  type McpToolApprovalRequest,
  type ResolvedMcpServerRuntimeConfiguration,
} from "./mcp-client-manager.js";
import {
  createProviderTransport,
  type ProviderTransport,
} from "./provider-transport.js";
import {
  resolveRuntimeContextPolicy,
  type RuntimeContextPolicyOptions,
} from "./runtime-context-policy.js";
import {
  finishContextCompaction,
  startContextCompaction,
  type ContextCompactionTrackerV1,
} from "./context-compaction.js";
import {
  createProviderCacheDiagnostic,
  resolveProviderCacheRequestPolicy,
} from "./provider-cache-adapter.js";
import { resolveProviderCapabilityProfile } from "./provider-capability-profile.js";
import type { ProviderContextDiagnosticListener } from "./provider-context-diagnostics.js";
import { parseProviderUsageSample } from "./provider-usage.js";

export interface RuntimeCredentialProvider {
  resolveApiKey(providerId: string): Promise<string | undefined>;
  /** Materializes transient MCP boundary strings without retaining them on the adapter. */
  materializeMcpServers?(): readonly ResolvedMcpServerRuntimeConfiguration[];
}

export interface PiSessionHandle {
  readonly sessionId: string;
  readonly isStreaming: boolean;
  readonly isCompacting?: boolean;
  getActiveToolNames(): string[];
  subscribe(listener: (event: AgentSessionEvent) => void): () => void;
  prompt(text: string, options?: PromptOptions): Promise<void>;
  steer(text: string): Promise<void>;
  followUp(text: string): Promise<void>;
  abort(): Promise<void>;
  abortCompaction?(): void;
  dispose(): void | Promise<void>;
}

export interface PiSessionCreateOptions {
  providerId: string;
  providerName: string;
  apiFamily: ApiFamily;
  endpoint?: string;
  modelId: string;
  modelName: string;
  modelCapabilities: readonly ModelCapability[];
  contextWindow?: number;
  maxOutputTokens?: number;
  thinkingLevel?: ThinkingLevel;
  /**
   * Provider transport owned by the local host. Supplying it explicitly keeps
   * Pi/OpenAI-compatible SDKs on Node's proxy-aware global fetch path.
   */
  providerFetch?: typeof globalThis.fetch;
  cwd: string;
  agentDir?: string;
  sessionId: string;
  tools: readonly string[];
  apiKey: string;
  systemPrompt: string;
  contextPolicy: RuntimeContextPolicyOptions;
  skillPaths: readonly string[];
  mcpServers: readonly ResolvedMcpServerRuntimeConfiguration[];
  approvalHandler?: (request: McpToolApprovalRequest) => Promise<boolean>;
  customTools?: readonly ToolDefinition[];
  diagnosticListener?: ProviderContextDiagnosticListener;
}

export interface PiSessionFactory {
  create(options: PiSessionCreateOptions): Promise<PiSessionHandle>;
}

export interface PiRuntimeAdapterOptions {
  roleId: string;
  providerId: string;
  providerName?: string;
  apiFamily?: ApiFamily;
  endpoint?: string;
  modelId: string;
  modelName?: string;
  modelCapabilities?: readonly ModelCapability[];
  contextWindow?: number;
  maxOutputTokens?: number;
  thinkingLevel?: ThinkingLevel;
  credentialProvider: RuntimeCredentialProvider;
  runtimeId?: string;
  sessionId?: string;
  cwd?: string;
  agentDir?: string;
  tools?: readonly string[];
  systemPrompt?: string;
  contextPolicy?: RuntimeContextPolicyOptions;
  skillPaths?: readonly string[];
  /** @deprecated Prefer credentialProvider.materializeMcpServers for secret-bearing configurations. */
  mcpServers?: readonly ResolvedMcpServerRuntimeConfiguration[];
  /** Internal, host-defined tools; never populated from meeting message content. */
  customTools?: readonly ToolDefinition[];
  subagentSpawner?: (task: string) => Promise<string>;
  sessionFactory?: PiSessionFactory;
  now?: () => Date;
  toolApprovalTimeoutMs?: number;
  runtimeGeneration?: number;
  diagnosticListener?: ProviderContextDiagnosticListener;
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

interface PendingToolApproval {
  resolve: (approved: boolean) => void;
  timeout: ReturnType<typeof setTimeout>;
  expiresAt: string;
  causationId?: string;
}

/** @internal Exported only for contract-level verification. */
export class MemoryCredentialStore implements CredentialStore {
  readonly #credentials = new Map<string, Credential>();
  readonly #writeChains = new Map<string, Promise<unknown>>();

  constructor(providerId?: string, apiKey?: string) {
    if (providerId !== undefined && apiKey !== undefined) {
      this.#credentials.set(providerId, { type: "api_key", key: apiKey });
    }
  }

  async read(providerId: string): Promise<Credential | undefined> {
    return this.#credentials.get(providerId);
  }

  async list(): Promise<readonly CredentialInfo[]> {
    return [...this.#credentials].map(([providerId, credential]) => ({
      providerId,
      type: credential.type,
    }));
  }

  modify(
    providerId: string,
    update: (current: Credential | undefined) => Promise<Credential | undefined>,
  ): Promise<Credential | undefined> {
    return this.#enqueueWrite(providerId, async () => {
      const current = this.#credentials.get(providerId);
      const next = await update(current);
      if (next !== undefined) {
        this.#credentials.set(providerId, next);
        return next;
      }
      // CredentialStore treats undefined as "leave unchanged". Explicit
      // deletion belongs to delete(); this matters for OAuth refresh callbacks.
      return current;
    });
  }

  delete(providerId: string): Promise<void> {
    return this.#enqueueWrite(providerId, async () => {
      this.#credentials.delete(providerId);
    });
  }

  #enqueueWrite<T>(providerId: string, operation: () => Promise<T>): Promise<T> {
    const previous = this.#writeChains.get(providerId) ?? Promise.resolve();
    const next = (async () => {
      await previous.catch(() => undefined);
      return operation();
    })();
    this.#writeChains.set(providerId, next.catch(() => undefined));
    return next;
  }
}

const DEFAULT_PI_SESSION_FACTORY: PiSessionFactory = {
  async create(options): Promise<PiSessionHandle> {
    if (
      options.maxOutputTokens !== undefined &&
      (!Number.isSafeInteger(options.maxOutputTokens) || options.maxOutputTokens < 1)
    ) {
      throw new PiRuntimeError(
        "invalid_max_output_tokens",
        "Pi max output tokens must be a positive safe integer",
      );
    }
    let modelRuntime: ModelRuntime;
    let providerTransport: ProviderTransport | undefined;
    const providerId = options.providerId;
    const credentialStore = new MemoryCredentialStore(providerId, options.apiKey);
    // This factory owns the create-options object. Drop its API-key reference
    // once the isolated in-memory store has accepted the SDK boundary copy.
    options.apiKey = "";
    try {
      modelRuntime = await ModelRuntime.create({
        credentials: credentialStore,
        modelsPath: null,
        allowModelNetwork: false,
      });
    } catch (error) {
      await credentialStore.delete(providerId);
      throw toStageError("model_runtime_init_failed", error);
    }
    const clearRuntimeCredential = async (): Promise<void> => {
      // The SDK boundary stores an immutable string in our isolated in-memory
      // store. Deletion drops that reference; unlike owned Buffers, it cannot
      // physically wipe strings already materialized by the SDK.
      await credentialStore.delete(providerId);
    };
    let endpoint: string | undefined;
    try {
      endpoint = options.endpoint === undefined
        ? undefined
        : normalizeProviderEndpoint(options.endpoint);
    } catch (error) {
      await clearRuntimeCredential();
      throw error;
    }
    let model;
    try {
      model = modelRuntime.getModel(options.providerId, options.modelId);
      if (model === undefined) {
        const api = mapApiFamily(options.apiFamily);
        const existingProvider = modelRuntime.getProvider(options.providerId);
        const baseUrl = endpoint ?? existingProvider?.baseUrl;
        if (api === undefined || baseUrl === undefined) {
          throw new PiRuntimeError(
            "model_not_found",
            `Pi model is not available: ${options.providerId}/${options.modelId}`,
          );
        }
        const contextWindow = options.contextWindow ?? 128_000;
        modelRuntime.registerProvider(options.providerId, {
          name: options.providerName,
          baseUrl,
          api,
          models: [{
            id: options.modelId,
            name: options.modelName,
            reasoning: options.modelCapabilities.includes("reasoning"),
            input: options.modelCapabilities.includes("vision") ? ["text", "image"] : ["text"],
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
            contextWindow,
            maxTokens: Math.min(
              contextWindow,
              65_536,
              options.maxOutputTokens ?? Math.max(4_096, Math.floor(contextWindow / 8)),
            ),
            ...(options.providerId === "deepseek"
              ? {
                  compat: {
                    supportsStore: false,
                    supportsDeveloperRole: false,
                    requiresReasoningContentOnAssistantMessages: true,
                    thinkingFormat: "deepseek" as const,
                  },
                }
              : {}),
          }],
        });
        model = modelRuntime.getModel(options.providerId, options.modelId);
      } else if (endpoint !== undefined) {
        modelRuntime.registerProvider(options.providerId, { baseUrl: endpoint });
        model = modelRuntime.getModel(options.providerId, options.modelId);
      }
    } catch (error) {
      await clearRuntimeCredential();
      throw toStageError("model_registration_failed", error);
    }
    if (model === undefined) {
      await clearRuntimeCredential();
      throw new PiRuntimeError(
        "model_not_found",
        `Pi model is not available: ${options.providerId}/${options.modelId}`,
      );
    }
    if (options.maxOutputTokens !== undefined) {
      model = {
        ...model,
        maxTokens: Math.min(model.maxTokens, options.maxOutputTokens),
      };
    }

    let mcpManager: McpClientManager;
    try {
      mcpManager = new McpClientManager(
        options.mcpServers,
        undefined,
        options.approvalHandler,
      );
    } catch (error) {
      options.mcpServers = [];
      await clearRuntimeCredential();
      throw toStageError("mcp_configuration_failed", error);
    }
    options.mcpServers = [];
    let createdSession: Awaited<ReturnType<typeof createAgentSession>>["session"] | undefined;
    try {
      let mcpTools;
      try {
        mcpTools = await mcpManager.connect();
      } catch (error) {
        throw toStageError("mcp_connect_failed", error);
      }
      const customTools = [...mcpTools, ...(options.customTools ?? [])];
      const capabilityProfile = resolveProviderCapabilityProfile({
        providerId: options.providerId,
        modelId: options.modelId,
        apiFamily: options.apiFamily,
        ...(endpoint === undefined ? {} : { endpoint }),
        ...(options.contextWindow === undefined
          ? {}
          : { declaredContextWindow: options.contextWindow }),
        runtimeContextWindow: model.contextWindow,
      });
      // Resolve disagreement conservatively. A declared window or stale SDK
      // registry entry must never make compaction later than the smaller bound.
      const contextPolicy = resolveRuntimeContextPolicy(
        capabilityProfile.resolvedContextWindow,
        options.contextPolicy,
      );
      const cacheRequestPolicy = resolveProviderCacheRequestPolicy(
        capabilityProfile,
        contextPolicy.cacheRetention,
        options.sessionId,
      );
      emitContextDiagnostic(options.diagnosticListener, createProviderCacheDiagnostic(
        capabilityProfile,
        Buffer.byteLength(options.systemPrompt, "utf8"),
        "initial_session",
      ));
      const settingsManager = createIsolatedSettingsManager(
        options.thinkingLevel ?? "off",
        contextPolicy,
      );
      const createOptions: CreateAgentSessionOptions = {
        cwd: options.cwd,
        modelRuntime,
        model,
        thinkingLevel: options.thinkingLevel ?? "off",
        customTools,
        settingsManager,
        sessionManager: SessionManager.inMemory(options.cwd, { id: options.sessionId }),
        resourceLoader: new DefaultResourceLoader({
          cwd: options.cwd,
          agentDir: options.agentDir ?? getAgentDir(),
          settingsManager,
          noExtensions: true,
          noSkills: true,
          noPromptTemplates: true,
          noThemes: true,
          noContextFiles: true,
          additionalSkillPaths: [...options.skillPaths],
          systemPromptOverride: () => options.systemPrompt,
        }),
      };
      if (options.agentDir !== undefined) {
        createOptions.agentDir = options.agentDir;
      }
      const toolNames = [...options.tools, ...customTools.map((tool) => tool.name)];
      if (toolNames.length === 0) {
        createOptions.noTools = "all";
      } else {
        createOptions.tools = toolNames;
      }

      const activeProviderTransport = createProviderTransport({
        ...(options.providerFetch === undefined
          ? {}
          : { providerFetch: options.providerFetch }),
      });
      providerTransport = activeProviderTransport;
      try {
        ({ session: createdSession } = await createAgentSession(createOptions));
      } catch (error) {
        throw toStageError("session_create_failed", error);
      }
      const session = createdSession;
      const piStreamFunction = session.agent.streamFunction;
      session.agent.streamFunction = (streamModel, context, streamOptions) => {
        const cacheOptions = cacheRequestPolicy.cacheRetention === "none"
          ? {}
          : {
              cacheRetention: cacheRequestPolicy.cacheRetention,
              ...(cacheRequestPolicy.sessionId === undefined
                ? {}
                : { sessionId: cacheRequestPolicy.sessionId }),
            };
        return piStreamFunction(streamModel, context, {
          ...streamOptions,
          fetch: activeProviderTransport.fetch,
          ...cacheOptions,
        });
      };
      let disposePromise: Promise<void> | undefined;
      return {
        sessionId: session.sessionId,
        get isStreaming() {
          return session.isStreaming;
        },
        get isCompacting() {
          return session.isCompacting;
        },
        getActiveToolNames: () => session.getActiveToolNames(),
        subscribe: (listener) => session.subscribe(listener),
        prompt: (text, promptOptions) => session.prompt(text, promptOptions),
        steer: (text) => session.steer(text),
        followUp: (text) => session.followUp(text),
        abort: () => session.abort(),
        abortCompaction: () => session.abortCompaction(),
        dispose: () => {
          disposePromise ??= (async () => {
            try {
              await session.dispose();
            } finally {
              try {
                await Promise.all([mcpManager.close(), activeProviderTransport.close()]);
              } finally {
                await clearRuntimeCredential();
              }
            }
          })();
          return disposePromise;
        },
      };
    } catch (error) {
      await Promise.resolve(createdSession?.dispose()).catch(() => undefined);
      await Promise.allSettled([mcpManager.close(), providerTransport?.close()]);
      await clearRuntimeCredential();
      throw error;
    }
  },
};

function toStageError(code: string, error: unknown): PiRuntimeError {
  return error instanceof PiRuntimeError
    ? error
    : new PiRuntimeError(code, `Pi runtime stage failed: ${code}`);
}

function emitContextDiagnostic(
  listener: ProviderContextDiagnosticListener | undefined,
  diagnostic: Parameters<ProviderContextDiagnosticListener>[0],
): void {
  try {
    listener?.(diagnostic);
  } catch {
    // Diagnostics are private observations and cannot affect the authoritative
    // role session or normalized meeting event stream.
  }
}

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
  #turnFailureErrorCode: string | undefined;
  #turnFailureTerminalEmitted = false;
  #turnStartedEmitted = false;
  #activeTurnCommandId: string | undefined;
  #compactionTracker: ContextCompactionTrackerV1 | undefined;
  #lastUsageSignature: string | undefined;
  readonly #pendingToolApprovals = new Map<string, PendingToolApproval>();

  constructor(options: PiRuntimeAdapterOptions) {
    this.#options = {
      ...options,
      ...(options.mcpServers === undefined
        ? {}
        : { mcpServers: [...options.mcpServers] }),
    };
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
    // Defer user/provider seams until #startPromise is published. A synchronous
    // reentrant stop can then await and cancel the same startup operation.
    const operation = Promise.resolve().then(() => this.#startSession());
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

    if (command.kind === "tool.approval.resolve") {
      const pending = this.#pendingToolApprovals.get(command.approvalId);
      if (pending === undefined) {
        return remember({
          commandId: command.commandId,
          accepted: false,
          errorCode: "approval_not_pending",
          message: "The tool approval request is not pending",
        });
      }
      this.#pendingToolApprovals.delete(command.approvalId);
      clearTimeout(pending.timeout);
      pending.resolve(command.approved);
      this.#emit("tool.approval_resolved", {
        approvalId: command.approvalId,
        approved: command.approved,
        reason: "user",
        expiresAt: pending.expiresAt,
      }, pending.causationId);
      return remember({ commandId: command.commandId, accepted: true });
    }

    if (command.kind === "turn.cancel") {
      const previousCancellationPending = this.#turnCancellationPending;
      const previousCancellationEmitted = this.#turnCancellationEmitted;
      if (session.isStreaming || this.#promptDispatchPending) {
        this.#turnCancellationPending = true;
        this.#turnCancellationEmitted = false;
      }
      try {
        if (session.isCompacting && session.abortCompaction !== undefined) {
          try {
            session.abortCompaction();
          } catch {
            // The turn abort below remains authoritative even if Pi's optional
            // compaction-specific hook races with natural completion.
          }
        }
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
        this.#turnStartedEmitted = false;
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
              this.#turnStartedEmitted = false;
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
        this.#turnStartedEmitted = false;
        this.#activeTurnCommandId = undefined;
      }
      return this.#rememberFailure(command.commandId, fingerprint, error);
    }
  }

  async #startSession(): Promise<RuntimeSessionInfo> {
    let session: PiSessionHandle | undefined;
    let unsubscribe: (() => void) | undefined;
    let sessionPublished = false;
    // Move deprecated secret-bearing configuration out of adapter-owned state
    // before invoking any fallible or reentrant credential seam. A failed
    // startup may be retried without stop(), so retaining this reference on the
    // adapter would otherwise revive stale headers or environment values.
    const legacyMcpServers = this.#options.mcpServers ?? [];
    this.#options.mcpServers = [];

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
      const mcpServers = this.#options.credentialProvider.materializeMcpServers?.() ??
        legacyMcpServers;

      const factory = this.#options.sessionFactory ?? DEFAULT_PI_SESSION_FACTORY;
      const createOptions: PiSessionCreateOptions = {
        providerId: this.#options.providerId,
        providerName: this.#options.providerName ?? this.#options.providerId,
        apiFamily: this.#options.apiFamily ?? "custom",
        ...(this.#options.endpoint === undefined ? {} : { endpoint: this.#options.endpoint }),
        modelId: this.#options.modelId,
        modelName: this.#options.modelName ?? this.#options.modelId,
        modelCapabilities: [...(this.#options.modelCapabilities ?? ["text"])],
        ...(this.#options.contextWindow === undefined
          ? {}
          : { contextWindow: this.#options.contextWindow }),
        ...(this.#options.maxOutputTokens === undefined
          ? {}
          : { maxOutputTokens: this.#options.maxOutputTokens }),
        ...(this.#options.thinkingLevel === undefined
          ? {}
          : { thinkingLevel: this.#options.thinkingLevel }),
        cwd: this.#options.cwd ?? process.cwd(),
        sessionId: this.#sessionId,
        tools: [...(this.#options.tools ?? [])],
        apiKey,
        systemPrompt: this.#options.systemPrompt ?? "",
        contextPolicy: { ...this.#options.contextPolicy },
        skillPaths: [...(this.#options.skillPaths ?? [])],
        mcpServers: [...mcpServers],
        approvalHandler: (request) => this.#requestToolApproval(request),
        customTools: [
          ...(this.#options.customTools ?? []),
          ...(this.#options.subagentSpawner === undefined
            ? []
            : [this.#createSubagentTool(this.#options.subagentSpawner)]),
        ],
        ...(this.#options.diagnosticListener === undefined
          ? {}
          : { diagnosticListener: this.#options.diagnosticListener }),
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
      sessionPublished = true;

      const info = this.#sessionInfo(session);
      this.#emit("runtime.ready", { engine: "pi" });
      if (this.#state !== "running" || this.#session !== session) {
        throw new PiRuntimeError(
          "start_cancelled",
          "Pi runtime adapter was stopped while publishing runtime readiness",
        );
      }
      return info;
    } catch (error) {
      const cancelled = this.#state === "stopping" || this.#state === "stopped";
      try {
        unsubscribe?.();
      } catch {
        // Preserve the startup error while continuing credential/resource cleanup.
      }
      // Once a published session has been removed from #session, stop() owns
      // its disposal. Other startup failures still clean up their local handle.
      if (!sessionPublished || this.#session === session) {
        await Promise.resolve(session?.dispose()).catch(() => undefined);
      }
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
    this.#options.mcpServers = [];
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
    const shouldAbort = session.isStreaming || session.isCompacting || this.#promptDispatchPending;
    this.#promptDispatchPending = false;
    this.#clearCancellationOutcome();
    this.#turnFailed = false;
    this.#turnFailureTerminalEmitted = false;
    this.#turnStartedEmitted = false;
    this.#activeTurnCommandId = undefined;
    this.#compactionTracker = undefined;
    this.#lastUsageSignature = undefined;
    for (const pending of this.#pendingToolApprovals.values()) {
      clearTimeout(pending.timeout);
      pending.resolve(false);
    }
    this.#pendingToolApprovals.clear();
    try {
      if (shouldAbort) {
        if (session.isCompacting && session.abortCompaction !== undefined) {
          try {
            session.abortCompaction();
          } catch {
            // dispose() is still required and is the final cleanup boundary.
          }
        }
        await session.abort();
      }
    } finally {
      try {
        await session.dispose();
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
        subagents: this.#options.subagentSpawner !== undefined,
      },
    };
  }

  #requestToolApproval(request: McpToolApprovalRequest): Promise<boolean> {
    if (this.#state !== "running" || this.#pendingToolApprovals.has(request.approvalId)) {
      return Promise.resolve(false);
    }
    return new Promise<boolean>((resolve) => {
      const timeoutMs = this.#options.toolApprovalTimeoutMs ?? 120_000;
      if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0 || timeoutMs > 30 * 60_000) {
        resolve(false);
        return;
      }
      const expiresAt = new Date(this.#now().getTime() + timeoutMs).toISOString();
      const causationId = this.#activeTurnCommandId;
      const timeout = setTimeout(() => {
        const pending = this.#pendingToolApprovals.get(request.approvalId);
        if (pending === undefined) {
          return;
        }
        this.#pendingToolApprovals.delete(request.approvalId);
        pending.resolve(false);
        this.#emit("tool.approval_resolved", {
          approvalId: request.approvalId,
          approved: false,
          reason: "expired",
          expiresAt: pending.expiresAt,
        }, pending.causationId);
      }, timeoutMs);
      timeout.unref?.();
      this.#pendingToolApprovals.set(request.approvalId, {
        resolve,
        timeout,
        expiresAt,
        ...(causationId === undefined ? {} : { causationId }),
      });
      this.#emit("tool.approval_requested", {
        approvalId: request.approvalId,
        toolCallId: request.toolCallId,
        serverId: request.serverId,
        serverDisplayName: request.serverDisplayName,
        toolName: request.toolName,
        toolLabel: request.toolLabel,
        expiresAt,
      }, causationId);
    });
  }

  #createSubagentTool(spawnSubagent: (task: string) => Promise<string>): ToolDefinition {
    return {
      name: "spawn_subagent",
      label: "Spawn SubAgent",
      description: [
        "Delegate one bounded task to an isolated Pi SubAgent.",
        "The call returns immediately with an ID; the result is delivered privately back to this parent role.",
        "At most two SubAgents may run concurrently for this role, and SubAgents cannot recurse.",
      ].join(" "),
      parameters: Type.Object({
        task: Type.String({ minLength: 1, maxLength: 16_384 }),
      }),
      executionMode: "sequential",
      execute: async (_toolCallId, parameters) => {
        const task = (parameters as { task: string }).task;
        const subagentId = await spawnSubagent(task);
        return {
          content: [{
            type: "text",
            text: `SubAgent ${subagentId} is running asynchronously. Continue without waiting for its result.`,
          }],
          details: { subagentId },
        };
      },
    };
  }

  #onPiEvent(event: AgentSessionEvent, sourceSession: PiSessionHandle): void {
    if (this.#state !== "running" || this.#session !== sourceSession) {
      return;
    }

    switch (event.type) {
      case "agent_start":
        this.#turnFailed = false;
        this.#turnFailureErrorCode = undefined;
        this.#turnFailureTerminalEmitted = false;
        this.#lastUsageSignature = undefined;
        if (!this.#turnStartedEmitted) {
          this.#turnStartedEmitted = true;
          this.#emit("turn.started", {}, this.#activeTurnCommandId);
        }
        break;
      case "message_update": {
        const update = event.assistantMessageEvent;
        if (update.type === "text_delta") {
          this.#emit("turn.delta", { delta: update.delta }, this.#activeTurnCommandId);
        } else if (update.type === "error") {
          if (update.reason === "aborted") {
            this.#markTurnCancelled();
          } else {
            const failure = classifyPiRetryFailure(update.error.errorMessage);
            this.#markTurnFailed(failure.code, failure.message);
          }
        }
        break;
      }
      case "message_end":
        this.#observeFinalMessage(event.message);
        break;
      case "turn_end":
        this.#observeFinalMessage(event.message);
        break;
      case "compaction_start": {
        const policy = this.#resolveDiagnosticContextPolicy();
        this.#compactionTracker = startContextCompaction({
          startedAtMs: this.#now().getTime(),
          trigger: event.reason,
          triggerRatio: policy.compactAtTokens / policy.contextWindow,
          providerId: this.#options.providerId,
          modelId: this.#options.modelId,
          roleId: this.#options.roleId,
          sessionId: this.#sessionId,
          ...(this.#options.runtimeGeneration === undefined
            ? {}
            : { runtimeGeneration: this.#options.runtimeGeneration }),
        });
        break;
      }
      case "compaction_end": {
        const policy = this.#resolveDiagnosticContextPolicy();
        const tracker = this.#compactionTracker ?? startContextCompaction({
          startedAtMs: this.#now().getTime(),
          trigger: event.reason,
          triggerRatio: policy.compactAtTokens / policy.contextWindow,
          providerId: this.#options.providerId,
          modelId: this.#options.modelId,
          roleId: this.#options.roleId,
          sessionId: this.#sessionId,
          ...(this.#options.runtimeGeneration === undefined
            ? {}
            : { runtimeGeneration: this.#options.runtimeGeneration }),
        });
        const status = event.aborted
          ? "aborted" as const
          : event.errorMessage !== undefined
            ? "failed" as const
            : event.result === undefined
              ? "fallback" as const
              : "completed" as const;
        emitContextDiagnostic(this.#options.diagnosticListener, finishContextCompaction(tracker, {
          finishedAtMs: this.#now().getTime(),
          status,
          ...(event.result?.tokensBefore === undefined
            ? {}
            : { tokensBefore: event.result.tokensBefore }),
          ...(event.result?.estimatedTokensAfter === undefined
            ? {}
            : { estimatedTokensAfter: event.result.estimatedTokensAfter }),
          ...(event.result?.firstKeptEntryId === undefined
            ? {}
            : { firstKeptEntryId: event.result.firstKeptEntryId }),
          ...(event.result?.summary === undefined ? {} : { summary: event.result.summary }),
          ...(status === "failed" ? { failureCode: "pi_compaction_failed" } : {}),
          ...(status === "fallback" ? { fallbackReason: "missing_compaction_result" } : {}),
          willRetry: event.willRetry,
        }));
        if (event.result?.usage !== undefined) {
          this.#observeProviderUsage(event.result.usage, true);
        }
        this.#compactionTracker = undefined;
        break;
      }
      case "agent_settled":
        if (this.#turnCancellationPending) {
          if (!this.#turnCancellationEmitted) {
            this.#emit("turn.cancelled", { reason: "cancelled" }, this.#activeTurnCommandId);
          }
        } else if (this.#turnFailed) {
          if (!this.#turnFailureTerminalEmitted) {
            this.#turnFailureTerminalEmitted = true;
            this.#emit("turn.cancelled", {
              reason: "failed",
              errorCode: this.#turnFailureErrorCode ?? "pi_runtime_error",
            }, this.#activeTurnCommandId);
          }
        } else if (!this.#turnFailed) {
          this.#emit("turn.completed", {}, this.#activeTurnCommandId);
        }
        this.#clearCancellationOutcome();
        this.#turnFailed = false;
        this.#turnFailureErrorCode = undefined;
        this.#turnFailureTerminalEmitted = false;
        this.#turnStartedEmitted = false;
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
          const classified = classifyPiRetryFailure(event.finalError);
          const failure = classified.code === "pi_response_error" &&
              this.#turnFailureErrorCode !== undefined &&
              this.#turnFailureErrorCode !== "pi_response_error"
            ? providerFailureForCode(this.#turnFailureErrorCode)
            : classified.code === "pi_response_error"
              ? {
                  code: "pi_retry_exhausted",
                  message: "Pi automatic retry was exhausted",
                }
              : classified;
          this.#turnFailed = true;
          this.#turnFailureErrorCode = failure.code;
          this.#emit("runtime.failed", {
            errorCode: failure.code,
            message: failure.message,
          }, this.#activeTurnCommandId);
        }
        break;
      default:
        break;
    }
  }

  #observeFinalMessage(message: unknown): void {
    if (typeof message !== "object" || message === null) {
      return;
    }
    const finalMessage = message as {
      role?: unknown;
      stopReason?: unknown;
      errorMessage?: unknown;
      usage?: unknown;
    };
    if (finalMessage.role !== "assistant") {
      return;
    }
    this.#observeProviderUsage(finalMessage.usage, false);
    if (finalMessage.stopReason === "aborted") {
      this.#markTurnCancelled();
    } else if (finalMessage.stopReason === "error") {
      const failure = classifyPiRetryFailure(
        typeof finalMessage.errorMessage === "string" ? finalMessage.errorMessage : undefined,
      );
      this.#markTurnFailed(failure.code, failure.message);
    }
  }

  #observeProviderUsage(usage: unknown, partial: boolean): void {
    const sample = parseProviderUsageSample(usage, {
      providerId: this.#options.providerId,
      modelId: this.#options.modelId,
      observedAt: this.#now().toISOString(),
      source: "sdk_normalized",
      partial,
    });
    if (sample === undefined) {
      return;
    }
    const signature = JSON.stringify([
      sample.inputTokens,
      sample.outputTokens,
      sample.cacheReadTokens,
      sample.cacheWriteTokens,
      sample.totalTokens,
      sample.partial,
    ]);
    if (signature === this.#lastUsageSignature) {
      return;
    }
    this.#lastUsageSignature = signature;
    emitContextDiagnostic(this.#options.diagnosticListener, sample);
    const profile = resolveProviderCapabilityProfile({
      providerId: this.#options.providerId,
      modelId: this.#options.modelId,
      apiFamily: this.#options.apiFamily ?? "custom",
      ...(this.#options.endpoint === undefined ? {} : { endpoint: this.#options.endpoint }),
      ...(this.#options.contextWindow === undefined
        ? {}
        : { declaredContextWindow: this.#options.contextWindow }),
    });
    emitContextDiagnostic(this.#options.diagnosticListener, createProviderCacheDiagnostic(
      profile,
      Buffer.byteLength(this.#options.systemPrompt ?? "", "utf8"),
      "initial_session",
      sample,
    ));
  }

  #resolveDiagnosticContextPolicy(): ReturnType<typeof resolveRuntimeContextPolicy> {
    const profile = resolveProviderCapabilityProfile({
      providerId: this.#options.providerId,
      modelId: this.#options.modelId,
      apiFamily: this.#options.apiFamily ?? "custom",
      ...(this.#options.endpoint === undefined ? {} : { endpoint: this.#options.endpoint }),
      ...(this.#options.contextWindow === undefined
        ? {}
        : { declaredContextWindow: this.#options.contextWindow }),
    });
    return resolveRuntimeContextPolicy(
      profile.resolvedContextWindow,
      this.#options.contextPolicy,
    );
  }

  #markTurnCancelled(): void {
    this.#turnCancellationPending = true;
    if (!this.#turnCancellationEmitted) {
      this.#turnCancellationEmitted = true;
      this.#emit("turn.cancelled", { reason: "cancelled" }, this.#activeTurnCommandId);
    }
  }

  #markTurnFailed(errorCode: string, message: string): void {
    if (this.#turnFailed && this.#turnFailureErrorCode === errorCode) {
      return;
    }
    this.#turnFailed = true;
    this.#turnFailureErrorCode = errorCode;
    this.#emit("runtime.failed", { errorCode, message }, this.#activeTurnCommandId);
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
      case "tool.approval.resolve":
        return [
          command.kind,
          command.roleId,
          command.approvalId,
          command.approved ? "approved" : "denied",
        ].join("\u0000");
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

function createIsolatedSettingsManager(
  thinkingLevel: ThinkingLevel,
  contextPolicy: ReturnType<typeof resolveRuntimeContextPolicy>,
): SettingsManager {
  // The frozen participant manifest is authoritative. Loading ~/.pi settings
  // would let unrelated CLI defaults, extensions, and timeouts alter a live
  // Windows-owned meeting after its configuration has been accepted.
  return SettingsManager.inMemory({
    defaultThinkingLevel: thinkingLevel,
    httpIdleTimeoutMs: 90_000,
    websocketConnectTimeoutMs: 15_000,
    retry: {
      enabled: true,
      maxRetries: 1,
      baseDelayMs: 500,
      provider: {
        timeoutMs: 90_000,
        maxRetries: 1,
        maxRetryDelayMs: 2_000,
      },
    },
    compaction: {
      enabled: contextPolicy.autoCompaction,
      reserveTokens: contextPolicy.reserveTokens,
      keepRecentTokens: contextPolicy.keepRecentTokens,
    },
    packages: [],
    extensions: [],
    skills: [],
    prompts: [],
    themes: [],
  }, { projectTrusted: false });
}

function classifyPiRetryFailure(finalError: string | undefined): {
  code: string;
  message: string;
} {
  const normalized = (finalError ?? "").toLowerCase();
  if (/\b(401|403)\b|unauthori[sz]ed|authentication|invalid api[-_ ]?key/.test(normalized)) {
    return {
      code: "pi_provider_auth_failed",
      message: "The provider rejected the runtime credential",
    };
  }
  if (/\b(402|429)\b|rate.?limit|quota|insufficient.?balance|billing/.test(normalized)) {
    return {
      code: "pi_provider_capacity_limited",
      message: "The provider rejected the request because of quota, balance, or rate limits",
    };
  }
  if (/\b404\b|model[^\r\n]*(not.?found|does.?not.?exist|unavailable)/.test(normalized)) {
    return {
      code: "pi_provider_model_unavailable",
      message: "The provider does not expose the configured model",
    };
  }
  if (/\b400\b|invalid.?request|unsupported|max[_ -]?(completion[_ -]?)?tokens/.test(normalized)) {
    return {
      code: "pi_provider_request_incompatible",
      message: "The provider rejected the Pi compatibility request",
    };
  }
  if (/connection|fetch failed|econn|enotfound|etimedout|network|socket|tls|certificate/.test(normalized)) {
    return {
      code: "pi_provider_network_failed",
      message: "The provider request failed at the network layer",
    };
  }
  return {
    code: "pi_response_error",
    message: "Pi provider response failed",
  };
}

function providerFailureForCode(code: string): { code: string; message: string } {
  switch (code) {
    case "pi_provider_auth_failed":
      return { code, message: "The provider rejected the runtime credential" };
    case "pi_provider_capacity_limited":
      return {
        code,
        message: "The provider rejected the request because of quota, balance, or rate limits",
      };
    case "pi_provider_model_unavailable":
      return { code, message: "The provider does not expose the configured model" };
    case "pi_provider_request_incompatible":
      return { code, message: "The provider rejected the Pi compatibility request" };
    case "pi_provider_network_failed":
      return { code, message: "The provider request failed at the network layer" };
    default:
      return { code: "pi_response_error", message: "Pi provider response failed" };
  }
}

function mapApiFamily(apiFamily: ApiFamily): string | undefined {
  switch (apiFamily) {
    case "openai_responses":
      return "openai-responses";
    case "openai_chat_completions":
      return "openai-completions";
    case "anthropic_messages":
      return "anthropic-messages";
    case "google_generate_content":
      return "google-generative-ai";
    case "custom":
      return undefined;
  }
}

export function normalizeProviderEndpoint(value: string): string {
  const endpoint = new URL(value);
  const hostname = endpoint.hostname.replace(/^\[|\]$/g, "").toLowerCase();
  const loopback = hostname === "localhost" || hostname === "::1" ||
    /^127(?:\.\d{1,3}){3}$/.test(hostname);
  if (
    endpoint.username.length > 0 ||
    endpoint.password.length > 0 ||
    endpoint.search.length > 0 ||
    endpoint.hash.length > 0 ||
    (endpoint.protocol !== "https:" && !(endpoint.protocol === "http:" && loopback))
  ) {
    throw new PiRuntimeError(
      "invalid_provider_endpoint",
      "Provider endpoint must use HTTPS or loopback HTTP without credentials, query, or fragment",
    );
  }
  return endpoint.toString().replace(/\/$/u, "");
}
