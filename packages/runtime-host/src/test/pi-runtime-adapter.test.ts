import assert from "node:assert/strict";
import test from "node:test";

import type { AgentSessionEvent, PromptOptions } from "@earendil-works/pi-coding-agent";

import {
  PiRuntimeAdapter,
  type PiSessionCreateOptions,
  type PiSessionFactory,
  type PiSessionHandle,
} from "../pi-runtime-adapter.js";
import type { RuntimeEvent } from "../runtime-adapter.js";

class FakePiSession implements PiSessionHandle {
  readonly prompts: string[] = [];
  readonly steering: string[] = [];
  readonly followUps: string[] = [];
  abortCount = 0;
  disposed = false;
  streaming = false;
  promptResult: Promise<void> | undefined;
  #listener: ((event: AgentSessionEvent) => void) | undefined;

  constructor(
    readonly sessionId: string,
    readonly activeTools: string[] = [],
  ) {}

  get isStreaming(): boolean {
    return this.streaming;
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

  dispose(): void {
    this.disposed = true;
  }

  emit(event: AgentSessionEvent): void {
    this.#listener?.(event);
  }
}

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

function createAdapter(factory: FakePiSessionFactory, events: RuntimeEvent[]): PiRuntimeAdapter {
  const adapter = new PiRuntimeAdapter({
    runtimeId: "runtime-pi-test",
    sessionId: "session-role-researcher",
    roleId: "role.researcher",
    providerId: "openai",
    modelId: "gpt-test",
    tools: [],
    credentialProvider: {
      resolveApiKey: async () => "test-key-not-persisted",
    },
    sessionFactory: factory,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
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

  const cancelled = await adapter.execute({
    kind: "turn.cancel",
    commandId: "cancel-active-turn",
    roleId: "role.researcher",
  });
  assert.equal(cancelled.accepted, true);
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

  assert.equal(events.filter((event) => event.kind === "turn.cancelled").length, 1);
  assert.equal(events.some((event) => event.kind === "turn.completed"), false);
  session.streaming = false;
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
  session.emit({ type: "turn_start" });
  session.emit({ type: "turn_end", message: {} as never, toolResults: [] });
  assert.equal(events.filter((event) => event.kind === "turn.cancelled").length, 0);
  assert.equal(events.filter((event) => event.kind === "turn.completed").length, 1);
  await adapter.stop();
});
