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
      "subagent-session.subagent.startup-failure",
    );
    assert.match(observedOptions?.sessionId ?? "", /^[A-Za-z0-9][A-Za-z0-9._-]*[A-Za-z0-9]$/);
  } finally {
    process.off("unhandledRejection", onUnhandled);
  }
});
