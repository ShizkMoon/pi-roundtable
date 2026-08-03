import assert from "node:assert/strict";
import test from "node:test";

import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "../runtime-adapter.js";
import {
  PiSubagentRunner,
  type SubagentRunRequest,
} from "../subagent-runner.js";
import type { PiRuntimeAdapterOptions } from "../pi-runtime-adapter.js";

const REQUEST: SubagentRunRequest = {
  subagentId: "subagent.startup-failure",
  parentRoleId: "role.parent",
  runtimeGeneration: 4,
  providerId: "provider.test",
  providerName: "Test provider",
  apiFamily: "openai_chat_completions",
  endpoint: "https://example.invalid",
  modelId: "model.test",
  modelName: "Test model",
  modelCapabilities: ["text"],
  maxOutputTokens: 128,
  apiKey: "runtime-only-test-key",
  cwd: process.cwd(),
  systemPrompt: "Test parent",
  skillPaths: [],
  task: "Return a test result",
};

class StartupFailureAdapter implements RuntimeAdapter {
  readonly #listeners = new Set<RuntimeEventListener>();

  async start(): Promise<RuntimeSessionInfo> {
    for (const listener of this.#listeners) {
      listener({
        kind: "runtime.failed",
        runtimeId: "runtime.subagent-test",
        sessionId: "session.subagent-test",
        roleId: "role.parent",
        occurredAt: "2026-08-02T00:00:00.000Z",
        payload: { errorCode: "session_create_failed" },
      });
    }
    throw new Error("controlled adapter startup failure");
  }

  async stop(): Promise<void> {}

  subscribe(listener: RuntimeEventListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  async execute(command: RuntimeCommand): Promise<RuntimeCommandResult> {
    return { commandId: command.commandId, accepted: true };
  }
}

class AbortStopFailureAdapter implements RuntimeAdapter {
  readonly #listeners = new Set<RuntimeEventListener>();
  executeCount = 0;
  stopCount = 0;

  constructor(readonly hangExecution = false) {}

  async start(): Promise<RuntimeSessionInfo> {
    return {
      runtimeId: "runtime.subagent-abort",
      sessionId: "session.subagent-abort",
      engine: "test",
      capabilities: {
        steering: true,
        followUp: true,
        cancellation: true,
        tools: false,
        subagents: false,
      },
    };
  }

  async stop(): Promise<void> {
    ++this.stopCount;
    throw new Error("controlled stop failure");
  }

  subscribe(listener: RuntimeEventListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  async execute(command: RuntimeCommand): Promise<RuntimeCommandResult> {
    ++this.executeCount;
    if (this.hangExecution) {
      return new Promise<never>(() => undefined);
    }
    return { commandId: command.commandId, accepted: true };
  }
}

test("contains an early runtime.failed rejection when SubAgent startup also rejects", async () => {
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown): void => {
    unhandled.push(reason);
  };
  process.on("unhandledRejection", onUnhandled);
  try {
    let observedOptions: PiRuntimeAdapterOptions | undefined;
    const runner = new PiSubagentRunner((options) => {
      observedOptions = options;
      return new StartupFailureAdapter();
    });
    await assert.rejects(
      runner.run(REQUEST, () => undefined, new AbortController().signal),
      /controlled adapter startup failure/,
    );
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.deepEqual(unhandled, []);
    assert.equal(
      observedOptions?.sessionId,
      "subagent-session.4.subagent.startup-failure",
    );
    assert.match(observedOptions?.sessionId ?? "", /^[A-Za-z0-9][A-Za-z0-9._-]*[A-Za-z0-9]$/);
  } finally {
    process.off("unhandledRejection", onUnhandled);
  }
});

test("rejects a pre-aborted SubAgent before creating its adapter", async () => {
  const controller = new AbortController();
  controller.abort();
  let factoryCalls = 0;
  const runner = new PiSubagentRunner(() => {
    ++factoryCalls;
    return new StartupFailureAdapter();
  });

  await assert.rejects(
    runner.run(REQUEST, () => undefined, controller.signal),
    (error: unknown) => error instanceof DOMException && error.name === "AbortError",
  );
  assert.equal(factoryCalls, 0);
});

test("observes abort-triggered adapter stop rejection", async () => {
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown): void => {
    unhandled.push(reason);
  };
  process.on("unhandledRejection", onUnhandled);
  try {
    const adapter = new AbortStopFailureAdapter(true);
    const runner = new PiSubagentRunner(() => adapter);
    const controller = new AbortController();
    const running = runner.run(REQUEST, () => undefined, controller.signal);
    for (let attempt = 0; attempt < 10 && adapter.executeCount === 0; ++attempt) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    assert.equal(adapter.executeCount, 1);
    controller.abort();

    await assert.rejects(running, /SubAgent was cancelled/);
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.deepEqual(unhandled, []);
    assert.equal(adapter.stopCount, 1);
  } finally {
    process.off("unhandledRejection", onUnhandled);
  }
});
