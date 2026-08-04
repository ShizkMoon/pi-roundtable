import assert from "node:assert/strict";
import test from "node:test";

import type { AgentSessionEvent, PromptOptions } from "@earendil-works/pi-coding-agent";

import {
  MemoryCredentialStore,
  normalizeProviderEndpoint,
  PiRuntimeAdapter,
  type PiSessionCreateOptions,
  type PiSessionFactory,
  type PiSessionHandle,
} from "../pi-runtime-adapter.js";
import type { ProviderContextDiagnosticV1 } from "../provider-context-diagnostics.js";
import type { RuntimeEvent } from "../runtime-adapter.js";

class FakePiSession implements PiSessionHandle {
  readonly prompts: string[] = [];
  readonly steering: string[] = [];
  readonly followUps: string[] = [];
  abortCount = 0;
  abortCompactionCount = 0;
  disposed = false;
  disposeError: Error | undefined;
  streaming = false;
  compacting = false;
  promptResult: Promise<void> | undefined;
  #listener: ((event: AgentSessionEvent) => void) | undefined;

  constructor(
    readonly sessionId: string,
    readonly activeTools: string[] = [],
  ) {}

  get isStreaming(): boolean {
    return this.streaming;
  }

  get isCompacting(): boolean {
    return this.compacting;
  }

  getActiveToolNames(): string[] {
    return [...this.activeTools];
  }

  subscribe(listener: (event: AgentSessionEvent) => void): () => void {
    this.#listener = listener;
    return () => {
      this.#listener = undefined;
    };
  }

  async prompt(text: string, _options?: PromptOptions): Promise<void> {
    this.prompts.push(text);
    await this.promptResult;
  }

  async steer(text: string): Promise<void> {
    this.steering.push(text);
  }

  async followUp(text: string): Promise<void> {
    this.followUps.push(text);
  }

  async abort(): Promise<void> {
    ++this.abortCount;
  }

  abortCompaction(): void {
    ++this.abortCompactionCount;
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    if (this.disposeError !== undefined) {
      throw this.disposeError;
    }
  }

  emit(event: AgentSessionEvent): void {
    this.#listener?.(event);
  }
}

test("in-memory credential updates preserve the current value when the updater returns undefined", async () => {
  const store = new MemoryCredentialStore("provider.test", "original-key");

  const result = await store.modify("provider.test", async () => undefined);

  assert.deepEqual(result, { type: "api_key", key: "original-key" });
  assert.deepEqual(await store.read("provider.test"), {
    type: "api_key",
    key: "original-key",
  });
  await store.delete("provider.test");
  assert.equal(await store.read("provider.test"), undefined);
});

test("in-memory credential writes are serialized per provider including deletion", async () => {
  const store = new MemoryCredentialStore("provider.test", "original-key");
  const firstEntered = deferred<void>();
  const releaseFirst = deferred<void>();
  let secondObservedKey: string | undefined;
  const first = store.modify("provider.test", async () => {
    firstEntered.resolve();
    await releaseFirst.promise;
    return { type: "api_key", key: "first-key" };
  });
  await firstEntered.promise;
  const second = store.modify("provider.test", async (current) => {
    secondObservedKey = current?.type === "api_key" ? current.key : undefined;
    return { type: "api_key", key: "second-key" };
  });
  const deletion = store.delete("provider.test");

  releaseFirst.resolve();
  await Promise.all([first, second, deletion]);

  assert.equal(secondObservedKey, "first-key");
  assert.equal(await store.read("provider.test"), undefined);
});

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

class FakePiSessionFactory implements PiSessionFactory {
  createCount = 0;
  lastOptions: PiSessionCreateOptions | undefined;
  session: FakePiSession | undefined;

  async create(options: PiSessionCreateOptions): Promise<PiSessionHandle> {
    ++this.createCount;
    this.lastOptions = options;
    this.session = new FakePiSession(options.sessionId, [...options.tools]);
    return this.session;
  }
}

function createAdapter(
  factory: FakePiSessionFactory,
  events: RuntimeEvent[],
  subagentSpawner?: (task: string) => Promise<string>,
  toolApprovalTimeoutMs?: number,
): PiRuntimeAdapter {
  const adapter = new PiRuntimeAdapter({
    runtimeId: "runtime-pi-test",
    sessionId: "session-role-researcher",
    roleId: "role.researcher",
    providerId: "openai",
    modelId: "gpt-test",
    tools: [],
    systemPrompt: "You are the research role.",
    skillPaths: ["skills/research/SKILL.md"],
    credentialProvider: {
      resolveApiKey: async () => "test-key-not-persisted",
    },
    sessionFactory: factory,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    ...(toolApprovalTimeoutMs === undefined ? {} : { toolApprovalTimeoutMs }),
    ...(subagentSpawner === undefined ? {} : { subagentSpawner }),
  });
  adapter.subscribe((event) => events.push(event));
  return adapter;
}

test("starts a direct Pi session and normalizes events without raw Pi records", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);

  const info = await adapter.start();
  assert.equal(info.engine, "pi");
  assert.equal(info.sessionId, "session-role-researcher");
  assert.equal(info.capabilities.tools, false);
  assert.equal(info.capabilities.subagents, false);
  assert.equal(factory.lastOptions?.apiKey.length, "test-key-not-persisted".length);
  assert.deepEqual(factory.lastOptions?.tools, []);
  assert.equal(factory.lastOptions?.systemPrompt, "You are the research role.");
  assert.deepEqual(factory.lastOptions?.skillPaths, ["skills/research/SKILL.md"]);

  const command = {
    kind: "turn.prompt" as const,
    commandId: "command-1",
    roleId: "role.researcher",
    message: "分析这个架构",
    delivery: "immediate" as const,
  };
  assert.equal((await adapter.execute(command)).accepted, true);
  assert.equal((await adapter.execute(command)).accepted, true);
  assert.deepEqual(factory.session?.prompts, ["分析这个架构"]);
  const conflictingCommand = await adapter.execute({
    ...command,
    message: "同一个 ID 不得代表另一条命令",
  });
  assert.equal(conflictingCommand.errorCode, "command_id_conflict");

  const session = factory.session;
  assert.ok(session !== undefined);
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  session.emit({
    type: "message_update",
    message: {} as never,
    assistantMessageEvent: {
      type: "text_delta",
      contentIndex: 0,
      delta: "结论",
      partial: {} as never,
    },
  });
  session.emit({
    type: "tool_execution_start",
    toolCallId: "tool-1",
    toolName: "read",
    args: { path: "hidden" },
  });
  session.emit({
    type: "tool_execution_end",
    toolCallId: "tool-1",
    toolName: "read",
    result: { raw: "hidden" },
    isError: false,
  });
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  assert.equal(events.some((event) => event.kind === "turn.completed"), false);
  session.emit({ type: "agent_settled" });

  assert.deepEqual(
    events.map((event) => event.kind),
    [
      "runtime.ready",
      "turn.started",
      "turn.delta",
      "tool.started",
      "tool.completed",
      "turn.completed",
    ],
  );
  assert.deepEqual(events[2]?.payload, { delta: "结论" });
  assert.deepEqual(events[3]?.payload, { toolCallId: "tool-1", toolName: "read" });
  assert.equal("args" in (events[3]?.payload ?? {}), false);
  assert.equal("result" in (events[4]?.payload ?? {}), false);

  await adapter.stop();
  assert.equal(session.disposed, true);
  assert.equal(events.at(-1)?.kind, "runtime.stopped");
});

test("completes one runtime turn only after a multi-turn Pi tool run settles", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);

  await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-multi-turn-tool",
    roleId: "role.researcher",
    message: "use a tool and then answer",
    delivery: "immediate",
  });
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  session.emit({
    type: "tool_execution_start",
    toolCallId: "tool-multi-turn",
    toolName: "read",
    args: {},
  });
  session.emit({
    type: "tool_execution_end",
    toolCallId: "tool-multi-turn",
    toolName: "read",
    result: {} as never,
    isError: false,
  });
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  assert.equal(events.filter((event) => event.kind === "turn.completed").length, 0);

  session.emit({ type: "turn_start" });
  session.emit({
    type: "message_update",
    message: {} as never,
    assistantMessageEvent: {
      type: "text_delta",
      contentIndex: 0,
      delta: "final answer",
      partial: {} as never,
    },
  });
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  session.emit({ type: "agent_end", messages: [], willRetry: false });
  assert.equal(events.filter((event) => event.kind === "turn.completed").length, 0);
  session.emit({ type: "agent_settled" });

  assert.equal(events.filter((event) => event.kind === "turn.started").length, 1);
  assert.equal(events.filter((event) => event.kind === "turn.completed").length, 1);
  assert.equal(events.find((event) => event.kind === "turn.completed")?.correlationId,
    "prompt-multi-turn-tool");
  assert.equal(events.find((event) => event.kind === "turn.delta")?.correlationId,
    "prompt-multi-turn-tool");
  await adapter.stop();
});

test("propagates the frozen role output-token limit into Pi session creation", async () => {
  const factory = new FakePiSessionFactory();
  const adapter = new PiRuntimeAdapter({
    runtimeId: "runtime-token-limit",
    sessionId: "session-token-limit",
    roleId: "role.bounded",
    providerId: "deepseek",
    providerName: "DeepSeek",
    apiFamily: "openai_chat_completions",
    endpoint: "https://api.deepseek.com",
    modelId: "deepseek-chat",
    modelName: "DeepSeek Chat",
    modelCapabilities: ["text"],
    contextWindow: 65_536,
    maxOutputTokens: 320,
    tools: [],
    credentialProvider: { resolveApiKey: async () => "bounded-test-key" },
    sessionFactory: factory,
  });

  await adapter.start();
  assert.equal(factory.lastOptions?.maxOutputTokens, 320);
  await adapter.stop();
});

test("validates provider endpoints before they reach the Pi model runtime", () => {
  assert.equal(
    normalizeProviderEndpoint("https://api.deepseek.com/v1/"),
    "https://api.deepseek.com/v1",
  );
  assert.equal(
    normalizeProviderEndpoint("http://127.0.0.1:11434/v1"),
    "http://127.0.0.1:11434/v1",
  );
  assert.throws(
    () => normalizeProviderEndpoint("http://api.deepseek.com/v1"),
    /HTTPS or loopback/,
  );
  assert.throws(
    () => normalizeProviderEndpoint("https://user:secret@api.deepseek.com/v1"),
    /credentials/,
  );
  assert.throws(
    () => normalizeProviderEndpoint("https://api.deepseek.com/v1?key=secret"),
    /query/,
  );
});

test("creates an offline Pi session for a discovered DeepSeek-compatible model", async () => {
  const originalFetch = globalThis.fetch;
  let networkCalls = 0;
  globalThis.fetch = async () => {
    ++networkCalls;
    throw new Error("offline test intercepted an unexpected network request");
  };
  const adapter = new PiRuntimeAdapter({
    runtimeId: "runtime-deepseek-offline",
    sessionId: "session-deepseek-offline",
    roleId: "role.deepseek",
    providerId: "deepseek",
    providerName: "DeepSeek",
    apiFamily: "openai_chat_completions",
    endpoint: "https://api.deepseek.com",
    modelId: "deepseek-discovered-test",
    modelName: "DeepSeek discovered test model",
    modelCapabilities: ["text", "reasoning", "tool_calling"],
    contextWindow: 65_536,
    tools: [],
    systemPrompt: "Test only. Do not contact the network during startup.",
    credentialProvider: { resolveApiKey: async () => "offline-test-key" },
  });

  try {
    const info = await adapter.start();
    assert.equal(info.engine, "pi");
    assert.equal(info.sessionId, "session-deepseek-offline");
    await adapter.stop();
    assert.equal(networkCalls, 0);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("returns to stopped state when startup cleanup itself rejects", async () => {
  let createCount = 0;
  const factory: PiSessionFactory = {
    async create(options) {
      ++createCount;
      const session = new FakePiSession("mismatched-session", [...options.tools]);
      session.disposeError = new Error("controlled dispose failure");
      return session;
    },
  };
  const events: RuntimeEvent[] = [];
  const adapter = new PiRuntimeAdapter({
    sessionId: "expected-session",
    roleId: "role.cleanup-failure",
    providerId: "provider.test",
    modelId: "model.test",
    credentialProvider: { resolveApiKey: async () => "cleanup-secret" },
    sessionFactory: factory,
  });
  adapter.subscribe((event) => events.push(event));

  await assert.rejects(adapter.start(), /mismatched-session/);
  await assert.rejects(adapter.start(), /mismatched-session/);
  assert.equal(createCount, 2);
  assert.equal(events.filter((event) => event.kind === "runtime.failed").length, 2);
});

test("exposes one asynchronous SubAgent tool without leaking the delegated task", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const delegatedTasks: string[] = [];
  const adapter = createAdapter(factory, events, async (task) => {
    delegatedTasks.push(task);
    return "subagent.test-1";
  });

  const info = await adapter.start();
  assert.equal(info.capabilities.subagents, true);
  const tool = factory.lastOptions?.customTools?.find((candidate) =>
    candidate.name === "spawn_subagent");
  assert.ok(tool !== undefined);
  const result = await tool.execute(
    "tool-call-1",
    { task: "bounded private research" },
    undefined,
    undefined,
    {} as never,
  );
  assert.deepEqual(delegatedTasks, ["bounded private research"]);
  assert.deepEqual(result.details, { subagentId: "subagent.test-1" });
  assert.equal(JSON.stringify(result).includes("bounded private research"), false);

  await adapter.stop();
});

test("normalizes tool approval without exposing tool arguments", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const requestApproval = factory.lastOptions?.approvalHandler;
  assert.ok(requestApproval !== undefined);

  const decision = requestApproval({
    approvalId: "approval-1",
    toolCallId: "tool-1",
    serverId: "mcp.notes",
    serverDisplayName: "Notes",
    toolName: "write_note",
    toolLabel: "Write note",
  });
  assert.equal(events.at(-1)?.kind, "tool.approval_requested");
  assert.deepEqual(events.at(-1)?.payload, {
    approvalId: "approval-1",
    toolCallId: "tool-1",
    serverId: "mcp.notes",
    serverDisplayName: "Notes",
    toolName: "write_note",
    toolLabel: "Write note",
    expiresAt: "2026-08-01T00:02:00.000Z",
  });
  assert.equal("args" in (events.at(-1)?.payload ?? {}), false);

  const receipt = await adapter.execute({
    kind: "tool.approval.resolve",
    commandId: "approve-1",
    roleId: "role.researcher",
    approvalId: "approval-1",
    approved: true,
  });
  assert.equal(receipt.accepted, true);
  assert.equal(await decision, true);
  assert.deepEqual(events.at(-1)?.payload, {
    approvalId: "approval-1",
    approved: true,
    reason: "user",
    expiresAt: "2026-08-01T00:02:00.000Z",
  });
  await adapter.stop();
});

test("expires tool approval, denies the tool, and rejects a late decision", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events, undefined, 20);
  await adapter.start();
  const requestApproval = factory.lastOptions?.approvalHandler;
  assert.ok(requestApproval !== undefined);

  const decision = requestApproval({
    approvalId: "approval-expiring",
    toolCallId: "tool-expiring",
    serverId: "mcp.notes",
    serverDisplayName: "Notes",
    toolName: "write_note",
    toolLabel: "Write note",
  });

  assert.equal(await decision, false);
  assert.deepEqual(events.at(-1)?.payload, {
    approvalId: "approval-expiring",
    approved: false,
    reason: "expired",
    expiresAt: "2026-08-01T00:00:00.020Z",
  });
  const late = await adapter.execute({
    kind: "tool.approval.resolve",
    commandId: "late-approval",
    roleId: "role.researcher",
    approvalId: "approval-expiring",
    approved: true,
  });
  assert.equal(late.accepted, false);
  assert.equal(late.errorCode, "approval_not_pending");
  await adapter.stop();
});

test("enforces role ownership, busy delivery, cancellation, and unsupported commands", async () => {
  const factory = new FakePiSessionFactory();
  const adapter = createAdapter(factory, []);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);

  const wrongRole = await adapter.execute({
    kind: "turn.prompt",
    commandId: "wrong-role",
    roleId: "role.other",
    message: "hello",
    delivery: "immediate",
  });
  assert.equal(wrongRole.errorCode, "role_mismatch");

  session.streaming = true;
  const busy = await adapter.execute({
    kind: "turn.prompt",
    commandId: "busy",
    roleId: "role.researcher",
    message: "hello",
    delivery: "immediate",
  });
  assert.equal(busy.errorCode, "runtime_busy");

  const steered = await adapter.execute({
    kind: "turn.prompt",
    commandId: "steer",
    roleId: "role.researcher",
    message: "change direction",
    delivery: "steer",
  });
  assert.equal(steered.accepted, true);
  assert.deepEqual(session.steering, ["change direction"]);

  const subscription = await adapter.execute({
    kind: "subagent.subscription",
    commandId: "subagents",
    roleId: "role.researcher",
    level: "events",
  });
  assert.equal(subscription.errorCode, "unsupported_command");

  const cancelled = await adapter.execute({
    kind: "turn.cancel",
    commandId: "cancel",
    roleId: "role.researcher",
  });
  assert.equal(cancelled.accepted, true);
  assert.equal(session.abortCount, 1);
  await adapter.stop();
});

test("fails closed when no provider credential is available", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = new PiRuntimeAdapter({
    runtimeId: "runtime-no-credential",
    sessionId: "session-no-credential",
    roleId: "role.secure",
    providerId: "openai",
    modelId: "gpt-test",
    credentialProvider: { resolveApiKey: async () => undefined },
    sessionFactory: factory,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
  });
  adapter.subscribe((event) => events.push(event));

  await assert.rejects(() => adapter.start(), /No runtime credential is available/);
  assert.equal(factory.createCount, 0);
  assert.equal(events[0]?.kind, "runtime.failed");
  assert.equal(events[0]?.payload.errorCode, "credential_unavailable");
});

test("serializes startup and creates only one Pi session", async () => {
  const credentials = deferred<string | undefined>();
  const factory = new FakePiSessionFactory();
  const adapter = new PiRuntimeAdapter({
    sessionId: "session-serialized-start",
    roleId: "role.serialized",
    providerId: "openai",
    modelId: "gpt-test",
    credentialProvider: { resolveApiKey: () => credentials.promise },
    sessionFactory: factory,
  });

  const firstStart = adapter.start();
  await assert.rejects(() => adapter.start(), /already started/);
  credentials.resolve("test-key");
  await firstStart;
  assert.equal(factory.createCount, 1);
  await adapter.stop();
});

test("disposes a session when stop wins the startup race", async () => {
  const credentials = deferred<string | undefined>();
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = new PiRuntimeAdapter({
    sessionId: "session-stopped-during-start",
    roleId: "role.stopped-during-start",
    providerId: "openai",
    modelId: "gpt-test",
    credentialProvider: { resolveApiKey: () => credentials.promise },
    sessionFactory: factory,
  });
  adapter.subscribe((event) => events.push(event));

  const starting = adapter.start();
  const stopping = adapter.stop();
  credentials.resolve("test-key");
  await assert.rejects(starting, /stopped before startup completed/);
  await stopping;
  assert.equal(factory.session?.disposed, true);
  assert.deepEqual(events.map((event) => event.kind), ["runtime.stopped"]);
});

test("publishes the startup promise before a credential seam can stop reentrantly", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  let stopping: Promise<void> | undefined;
  let adapter!: PiRuntimeAdapter;
  adapter = new PiRuntimeAdapter({
    sessionId: "session-reentrant-credential-stop",
    roleId: "role.reentrant-credential-stop",
    providerId: "openai",
    modelId: "gpt-test",
    credentialProvider: {
      resolveApiKey: async () => {
        stopping = adapter.stop();
        return "test-key";
      },
    },
    sessionFactory: factory,
  });
  adapter.subscribe((event) => events.push(event));

  await assert.rejects(adapter.start(), /stopped before startup completed/);
  await stopping;
  assert.deepEqual(events.map((event) => event.kind), ["runtime.stopped"]);
  assert.equal(factory.session?.disposed, true);
});

test("drops deprecated static MCP credential references when stopped before startup", async () => {
  const factory = new FakePiSessionFactory();
  const adapter = new PiRuntimeAdapter({
    sessionId: "session-legacy-mcp-clear",
    roleId: "role.legacy-mcp-clear",
    providerId: "openai",
    modelId: "gpt-test",
    credentialProvider: { resolveApiKey: async () => "test-key" },
    mcpServers: [{
      serverId: "mcp.legacy",
      displayName: "Legacy MCP",
      transport: "streamable_http",
      endpoint: "https://mcp.example.com/api",
      headers: { Authorization: "Bearer legacy-secret" },
      toolAllowlist: [],
      approvalMode: "never",
      executionMode: "direct",
    }],
    sessionFactory: factory,
  });

  await adapter.stop();
  await adapter.start();
  assert.deepEqual(factory.lastOptions?.mcpServers, []);
  await adapter.stop();
});

test("does not revive deprecated static MCP credentials after a failed startup retry", async () => {
  const factory = new FakePiSessionFactory();
  let credentialAttempts = 0;
  const adapter = new PiRuntimeAdapter({
    sessionId: "session-legacy-mcp-failed-start",
    roleId: "role.legacy-mcp-failed-start",
    providerId: "openai",
    modelId: "gpt-test",
    credentialProvider: {
      resolveApiKey: async () => {
        ++credentialAttempts;
        if (credentialAttempts === 1) {
          throw new Error("controlled credential seam failure");
        }
        return "test-key";
      },
    },
    mcpServers: [{
      serverId: "mcp.legacy",
      displayName: "Legacy MCP",
      transport: "streamable_http",
      endpoint: "https://mcp.example.com/api",
      headers: { Authorization: "Bearer legacy-secret" },
      toolAllowlist: [],
      approvalMode: "never",
      executionMode: "direct",
    }],
    sessionFactory: factory,
  });

  await assert.rejects(adapter.start(), /controlled credential seam failure/);
  await adapter.start();
  assert.deepEqual(factory.lastOptions?.mcpServers, []);
  await adapter.stop();
});

test("a reentrant stop from runtime.ready makes startup fail without double disposal", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  let stopping: Promise<void> | undefined;
  adapter.subscribe((event) => {
    if (event.kind === "runtime.ready") {
      stopping = adapter.stop();
    }
  });

  await assert.rejects(adapter.start(), /stopped while publishing runtime readiness/);
  await stopping;
  assert.equal(factory.session?.disposed, true);
  assert.deepEqual(events.map((event) => event.kind), ["runtime.ready", "runtime.stopped"]);
});

test("rejects concurrent prompts and suppresses late failures after stop", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);
  const prompt = deferred<void>();
  session.promptResult = prompt.promise;

  const first = await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-in-flight",
    roleId: "role.researcher",
    message: "first",
    delivery: "immediate",
  });
  const second = await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-racing",
    roleId: "role.researcher",
    message: "second",
    delivery: "immediate",
  });
  assert.equal(first.accepted, true);
  assert.equal(second.errorCode, "runtime_busy");
  assert.deepEqual(session.prompts, ["first"]);

  await adapter.stop();
  prompt.reject(new Error("late provider failure"));
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(session.abortCount, 1);
  assert.equal(events.at(-1)?.kind, "runtime.stopped");
  assert.equal(events.some((event) => event.kind === "runtime.failed"), false);
});

test("redacts raw provider failure details from runtime events", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);

  await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-provider-error",
    roleId: "role.researcher",
    message: "private prompt",
    delivery: "immediate",
  });
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  session.emit({
    type: "message_update",
    message: {} as never,
    assistantMessageEvent: {
      type: "error",
      reason: "error",
      error: { errorMessage: "Authorization: Bearer secret-value" } as never,
    },
  } as never);

  const failure = events.find((event) => event.kind === "runtime.failed");
  assert.equal(failure?.payload.message, "Pi provider response failed");
  assert.ok(!JSON.stringify(failure).includes("secret-value"));
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  session.emit({ type: "agent_settled" });
  const terminal = events.find((event) => event.kind === "turn.cancelled");
  assert.equal(terminal?.payload.reason, "failed");
  assert.equal(terminal?.payload.errorCode, "pi_response_error");
  await adapter.stop();
});

test("classifies a final retry error without exposing provider details", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);

  await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-incompatible-provider-request",
    roleId: "role.researcher",
    message: "provider compatibility probe",
    delivery: "immediate",
  });
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  session.emit({
    type: "auto_retry_end",
    success: false,
    attempt: 3,
    finalError: "HTTP 400 invalid max_completion_tokens; Authorization: Bearer secret-value",
  });
  session.emit({ type: "agent_settled" });

  const failure = events.find((event) => event.kind === "runtime.failed");
  assert.equal(failure?.payload.errorCode, "pi_provider_request_incompatible");
  assert.equal(failure?.payload.message, "The provider rejected the Pi compatibility request");
  assert.ok(!JSON.stringify(events).includes("secret-value"));
  const terminal = events.find((event) => event.kind === "turn.cancelled");
  assert.equal(terminal?.payload.errorCode, "pi_provider_request_incompatible");
  await adapter.stop();
});

test("treats a final Pi assistant error as failed even without a streamed error update", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);

  await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-final-error",
    roleId: "role.researcher",
    message: "provider returns a terminal HTTP error",
    delivery: "immediate",
  });
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  const finalError = { role: "assistant", stopReason: "error" } as never;
  session.emit({ type: "message_end", message: finalError });
  session.emit({ type: "turn_end", message: finalError, toolResults: [] });
  session.emit({ type: "agent_settled" });

  assert.equal(events.filter((event) => event.kind === "runtime.failed").length, 1);
  assert.equal(events.filter((event) => event.kind === "turn.cancelled").length, 1);
  assert.equal(events.some((event) => event.kind === "turn.completed"), false);
  assert.equal(events.find((event) => event.kind === "turn.cancelled")?.payload.errorCode, "pi_response_error");
  await adapter.stop();
});

test("emits cancellation without a contradictory completion", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);
  session.streaming = true;
  session.compacting = true;

  const cancelled = await adapter.execute({
    kind: "turn.cancel",
    commandId: "cancel-active-turn",
    roleId: "role.researcher",
  });
  assert.equal(cancelled.accepted, true);
  assert.equal(session.abortCompactionCount, 1);
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  session.emit({
    type: "message_update",
    message: {} as never,
    assistantMessageEvent: {
      type: "error",
      reason: "aborted",
      error: { errorMessage: "aborted" } as never,
    },
  });
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  session.emit({ type: "agent_settled" });

  assert.equal(events.filter((event) => event.kind === "turn.cancelled").length, 1);
  assert.equal(
    events.find((event) => event.kind === "turn.cancelled")?.payload.reason,
    "cancelled",
  );
  assert.equal(events.some((event) => event.kind === "turn.completed"), false);
  session.streaming = false;
  session.compacting = false;
  await adapter.stop();
});

test("emits bounded private usage, cache, and compaction diagnostics", async () => {
  const factory = new FakePiSessionFactory();
  const diagnostics: ProviderContextDiagnosticV1[] = [];
  const adapter = new PiRuntimeAdapter({
    runtimeId: "runtime-context-diagnostics",
    sessionId: "session-context-diagnostics",
    roleId: "role.researcher",
    providerId: "openai",
    apiFamily: "openai_responses",
    modelId: "gpt-test",
    contextWindow: 100_000,
    runtimeGeneration: 7,
    systemPrompt: "stable role prefix",
    credentialProvider: { resolveApiKey: async () => "test-key" },
    sessionFactory: factory,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    diagnosticListener: (diagnostic) => diagnostics.push(diagnostic),
  });
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);

  session.emit({ type: "compaction_start", reason: "threshold" });
  session.emit({
    type: "compaction_end",
    reason: "threshold",
    result: {
      summary: "private summary must never be retained in diagnostics",
      firstKeptEntryId: "entry-42",
      tokensBefore: 62_000,
      estimatedTokensAfter: 19_000,
      usage: {
        input: 1_000,
        output: 100,
        cacheRead: 600,
        cacheWrite: 50,
        totalTokens: 1_100,
        cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
      },
    },
    aborted: false,
    willRetry: false,
  });
  const assistantMessage = {
    role: "assistant",
    stopReason: "stop",
    usage: {
      input: 2_000,
      output: 200,
      cacheRead: 1_500,
      cacheWrite: 100,
      totalTokens: 2_200,
    },
  } as never;
  session.emit({ type: "message_end", message: assistantMessage });
  session.emit({ type: "turn_end", message: assistantMessage, toolResults: [] });

  const compaction = diagnostics.find((item) => item.kind === "context_compaction");
  assert.equal(compaction?.status, "completed");
  assert.equal(compaction?.runtimeGeneration, 7);
  assert.equal(compaction?.tokensBefore, 62_000);
  assert.equal(compaction?.triggerRatio, 0.62);
  assert.equal(typeof compaction?.summaryDigest, "string");
  const usage = diagnostics.filter((item) => item.kind === "provider_usage");
  assert.equal(usage.length, 2);
  assert.equal(usage.at(-1)?.cacheReadTokens, 1_500);
  const cache = diagnostics.filter((item) => item.kind === "provider_cache").at(-1);
  assert.equal(cache?.hitRate, 1_500 / 3_600);
  assert.ok(!JSON.stringify(diagnostics).includes("private summary"));
  await adapter.stop();
});

test("does not leak a silent cancellation into the next turn", async () => {
  const factory = new FakePiSessionFactory();
  const events: RuntimeEvent[] = [];
  const adapter = createAdapter(factory, events);
  await adapter.start();
  const session = factory.session;
  assert.ok(session !== undefined);
  const firstPrompt = deferred<void>();
  session.promptResult = firstPrompt.promise;

  await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-before-silent-cancel",
    roleId: "role.researcher",
    message: "first",
    delivery: "immediate",
  });
  await adapter.execute({
    kind: "turn.cancel",
    commandId: "silent-cancel",
    roleId: "role.researcher",
  });
  firstPrompt.resolve();
  await new Promise<void>((resolve) => setImmediate(resolve));

  session.promptResult = undefined;
  const next = await adapter.execute({
    kind: "turn.prompt",
    commandId: "prompt-after-silent-cancel",
    roleId: "role.researcher",
    message: "next",
    delivery: "immediate",
  });
  assert.equal(next.accepted, true);
  session.emit({ type: "agent_start" });
  session.emit({ type: "turn_start" });
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  session.emit({ type: "agent_settled" });
  assert.equal(events.filter((event) => event.kind === "turn.cancelled").length, 0);
  assert.equal(events.filter((event) => event.kind === "turn.completed").length, 1);
  await adapter.stop();
});
