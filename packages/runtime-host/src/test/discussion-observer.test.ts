import assert from "node:assert/strict";
import test from "node:test";

import type { JsonObject } from "@pi-roundtable/protocol";

import {
  PiDiscussionObserver,
  validateDiscussionObservation,
  type DiscussionObservationRequest,
} from "../discussion-observer.js";
import type { PiRuntimeAdapterOptions } from "../pi-runtime-adapter.js";
import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "../runtime-adapter.js";

class ObserverRuntimeAdapter implements RuntimeAdapter {
  readonly #listeners = new Set<RuntimeEventListener>();
  stopCount = 0;
  onStart?: () => Promise<RuntimeSessionInfo>;
  onStop?: () => Promise<void>;

  constructor(
    readonly options: PiRuntimeAdapterOptions,
    readonly output: string,
    readonly complete = true,
  ) {}

  async start(): Promise<RuntimeSessionInfo> {
    if (this.onStart !== undefined) {
      return this.onStart();
    }
    return {
      runtimeId: this.options.runtimeId ?? "observer-runtime",
      sessionId: this.options.sessionId ?? "observer-session",
      engine: "test",
      capabilities: {
        steering: false,
        followUp: false,
        cancellation: true,
        tools: false,
        subagents: false,
      },
    };
  }

  async stop(): Promise<void> {
    ++this.stopCount;
    await this.onStop?.();
  }

  subscribe(listener: RuntimeEventListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  async execute(command: RuntimeCommand): Promise<RuntimeCommandResult> {
    if (this.complete) {
      queueMicrotask(() => {
        this.#emit("turn.delta", { delta: this.output }, command.commandId);
        this.#emit("turn.completed", {}, command.commandId);
      });
    }
    return { commandId: command.commandId, accepted: true };
  }

  #emit(kind: RuntimeEvent["kind"], payload: JsonObject, correlationId: string): void {
    for (const listener of this.#listeners) {
      listener({
        kind,
        runtimeId: this.options.runtimeId ?? "observer-runtime",
        sessionId: this.options.sessionId ?? "observer-session",
        occurredAt: "2026-08-03T00:00:00.000Z",
        roleId: this.options.roleId,
        correlationId,
        payload,
      });
    }
  }
}

const REQUEST: DiscussionObservationRequest = {
  observationId: "observe-1",
  candidateRoleId: "role.risk",
  candidateDisplayName: "风险审查员",
  candidateInstructions: "发现事实、要求或安全问题时明确指出。",
  speakerRoleId: "role.architect",
  speakerDisplayName: "架构师",
  observedText: "我们可以让同步服务器直接执行所有模型调用。",
  meetingContext: "会议要求同步服务器只转发并持久化规范化事件。",
  speechComplete: false,
  model: {
    providerId: "deepseek",
    providerName: "DeepSeek",
    apiFamily: "openai_chat_completions",
    endpoint: "https://api.deepseek.com",
    modelId: "deepseek-chat",
    modelName: "DeepSeek Chat",
    modelCapabilities: ["text"],
    contextWindow: 64_000,
    maxOutputTokens: 8_192,
    apiKey: "test-secret",
  },
  cwd: process.cwd(),
};

test("runs a bounded isolated observer and accepts an exact-evidence interruption", async () => {
  let adapter: ObserverRuntimeAdapter | undefined;
  const observer = new PiDiscussionObserver({
    adapterFactory: (options) => {
      adapter = new ObserverRuntimeAdapter(options, JSON.stringify({
        action: "interrupt",
        kind: "critical",
        reason: "同步服务器直接执行所有模型调用",
        prompt: "指出执行边界并给出本地 Runtime 方案。",
      }));
      return adapter;
    },
  });

  const decision = await observer.observe(REQUEST);

  assert.equal(decision.action, "interrupt");
  assert.equal(decision.kind, "critical");
  assert.deepEqual(adapter?.options.tools, []);
  assert.equal(adapter?.options.customTools?.length, 1);
  assert.equal(adapter?.options.customTools?.[0]?.name, "report_floor_decision");
  assert.deepEqual(adapter?.options.skillPaths, []);
  assert.deepEqual(adapter?.options.mcpServers, []);
  assert.equal(adapter?.options.subagentSpawner, undefined);
  assert.equal(adapter?.options.maxOutputTokens, 384);
  assert.match(adapter?.options.systemPrompt ?? "", /hidden, bounded floor-request observer/);
  assert.equal(adapter?.stopCount, 1);
});

test("downgrades a late interruption to a reply request", () => {
  assert.deepEqual(validateDiscussionObservation({
    action: "interrupt",
    kind: "critical",
    reason: "同步服务器直接执行所有模型调用",
    prompt: "纠正执行边界。",
  }, REQUEST.observedText, true), {
    action: "request",
    kind: "reply",
    reason: "同步服务器直接执行所有模型调用",
    prompt: "纠正执行边界。",
  });
});

test("aligns a tool decision wrapper back to an exact transcript excerpt", () => {
  assert.deepEqual(validateDiscussionObservation({
    action: "request",
    kind: "reply",
    reason: "针对“同步服务器直接执行所有模型调用”这一结论必须纠正",
    prompt: "纠正执行边界。",
  }, REQUEST.observedText, true), {
    action: "request",
    kind: "reply",
    reason: "同步服务器直接执行所有模型调用",
    prompt: "纠正执行边界。",
  });
});

test("repairs an unaligned tool reason with a bounded authoritative excerpt", () => {
  const decision = validateDiscussionObservation({
    action: "request",
    kind: "reply",
    reason: "这段理由只存在于模型参数中",
    prompt: "纠正执行边界。",
  }, REQUEST.observedText, true, true);

  assert.equal(decision.reason, REQUEST.observedText);
});

test("rejects invented reasons and invalid interruption kinds", () => {
  assert.throws(() => validateDiscussionObservation({
    action: "request",
    kind: "reply",
    reason: "原文不存在",
    prompt: "补充说明。",
  }, REQUEST.observedText, false), /exact excerpt/);
  assert.throws(() => validateDiscussionObservation({
    action: "interrupt",
    kind: "normal",
    reason: "同步服务器直接执行所有模型调用",
    prompt: "补充说明。",
  }, REQUEST.observedText, false), /kind is invalid/);
});

test("times out a stalled observer and stops it exactly once", async () => {
  let adapter: ObserverRuntimeAdapter | undefined;
  const observer = new PiDiscussionObserver({
    timeoutMs: 10,
    adapterFactory: (options) => {
      adapter = new ObserverRuntimeAdapter(options, "", false);
      return adapter;
    },
  });

  await assert.rejects(observer.observe(REQUEST), /cancelled/);
  assert.equal(adapter?.stopCount, 1);
});

test("cancels an observer whose adapter startup never settles", async () => {
  const controller = new AbortController();
  let adapter: ObserverRuntimeAdapter | undefined;
  let startResolve: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    startResolve = resolve;
  });
  const observer = new PiDiscussionObserver({
    adapterFactory: (options) => {
      adapter = new ObserverRuntimeAdapter(options, "", false);
      adapter.onStart = () => {
        startResolve?.();
        return new Promise<RuntimeSessionInfo>(() => undefined);
      };
      adapter.onStop = () => Promise.reject(new Error("test stop failure"));
      return adapter;
    },
  });

  const observing = observer.observe(REQUEST, controller.signal);
  await started;
  controller.abort();

  await assert.rejects(observing, /cancelled/);
  assert.equal(adapter?.stopCount, 1);
});
