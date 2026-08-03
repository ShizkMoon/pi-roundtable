import assert from "node:assert/strict";
import test from "node:test";

import type { JsonObject } from "@pi-roundtable/protocol";

import {
  PiPublicMessagePlanner,
  validatePublicMessagePlan,
  type PublicMessagePlanningRequest,
} from "../public-message-planner.js";
import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "../runtime-adapter.js";
import type { PiRuntimeAdapterOptions } from "../pi-runtime-adapter.js";

class PlannerRuntimeAdapter implements RuntimeAdapter {
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
      runtimeId: this.options.runtimeId ?? "planner-runtime",
      sessionId: this.options.sessionId ?? "planner-session",
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
    const event: RuntimeEvent = {
      kind,
      runtimeId: this.options.runtimeId ?? "planner-runtime",
      sessionId: this.options.sessionId ?? "planner-session",
      occurredAt: "2026-08-03T00:00:00.000Z",
      roleId: this.options.roleId,
      correlationId,
      payload,
    };
    for (const listener of this.#listeners) {
      listener(event);
    }
  }
}

const REQUEST: PublicMessagePlanningRequest = {
  commandId: "message-1",
  message: [
    "共同要求：给出验收标准。",
    "@架构师：检查边界。",
    "@体验官：检查流程。",
    "@架构师 与 @体验官：共同检查恢复路径。",
  ].join("\n"),
  roles: [
    { roleId: "role.architect", displayName: "架构师" },
    { roleId: "role.experience", displayName: "体验官" },
  ],
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

test("runs semantic planning in an isolated Pi session and validates exact excerpts", async () => {
  const output = JSON.stringify({
    sharedRequirements: ["共同要求：给出验收标准。"],
    roleTasks: {
      "role.architect": ["@架构师：检查边界。"],
      "role.experience": ["@体验官：检查流程。"],
    },
    groupTasks: [{
      roleIds: ["role.architect", "role.experience"],
      task: "@架构师 与 @体验官：共同检查恢复路径。",
    }],
    speakerOrder: ["role.experience", "role.architect"],
  });
  let adapter: PlannerRuntimeAdapter | undefined;
  const planner = new PiPublicMessagePlanner({
    adapterFactory: (options) => {
      adapter = new PlannerRuntimeAdapter(options, `\`\`\`json\n${output}\n\`\`\``);
      return adapter;
    },
  });

  const plan = await planner.plan(REQUEST);

  assert.deepEqual(plan.speakerOrder, ["role.experience", "role.architect"]);
  assert.deepEqual(plan.sharedRequirements, ["共同要求：给出验收标准。"]);
  assert.deepEqual(adapter?.options.tools, []);
  assert.deepEqual(adapter?.options.skillPaths, []);
  assert.deepEqual(adapter?.options.mcpServers, []);
  assert.equal(adapter?.options.subagentSpawner, undefined);
  assert.equal(adapter?.options.maxOutputTokens, 2_048);
  assert.match(adapter?.options.systemPrompt ?? "", /Return exactly one JSON object/);
  assert.equal(adapter?.stopCount, 1);
});

test("rejects planner paraphrases and roles outside the explicit target set", () => {
  assert.throws(() => validatePublicMessagePlan({
    sharedRequirements: ["请给出一个验收标准。"],
    roleTasks: {
      "role.architect": [],
      "role.experience": [],
    },
    groupTasks: [],
    speakerOrder: ["role.architect", "role.experience"],
  }, REQUEST.message, REQUEST.roles), /exact excerpt/);

  assert.throws(() => validatePublicMessagePlan({
    sharedRequirements: [],
    roleTasks: {
      "role.architect": [],
      "role.experience": [],
      "role.unmentioned": [],
    },
    groupTasks: [],
    speakerOrder: ["role.architect", "role.experience"],
  }, REQUEST.message, REQUEST.roles), /unknown role/);
});

test("times out a stalled planner and stops its isolated adapter exactly once", async () => {
  let adapter: PlannerRuntimeAdapter | undefined;
  const planner = new PiPublicMessagePlanner({
    timeoutMs: 10,
    adapterFactory: (options) => {
      adapter = new PlannerRuntimeAdapter(options, "", false);
      return adapter;
    },
  });

  await assert.rejects(planner.plan(REQUEST), /cancelled/);
  assert.equal(adapter?.stopCount, 1);
});

test("rejects malformed and oversized planner output without exposing partial plans", async () => {
  for (const [output, expected] of [
    ["not-json", /JSON/],
    ["x".repeat(32_769), /exceeded its limit/],
  ] as const) {
    let adapter: PlannerRuntimeAdapter | undefined;
    const planner = new PiPublicMessagePlanner({
      adapterFactory: (options) => {
        adapter = new PlannerRuntimeAdapter(options, output);
        return adapter;
      },
    });

    await assert.rejects(planner.plan(REQUEST), expected);
    assert.equal(adapter?.stopCount, 1);
  }
});

test("rejects an already-cancelled request before creating a Pi session", async () => {
  const controller = new AbortController();
  controller.abort();
  let adapterCreated = false;
  const planner = new PiPublicMessagePlanner({
    adapterFactory: (options) => {
      adapterCreated = true;
      return new PlannerRuntimeAdapter(options, "");
    },
  });

  await assert.rejects(planner.plan(REQUEST, controller.signal), /cancelled/);
  assert.equal(adapterCreated, false);
});

test("cancels a planner whose adapter startup never settles", async () => {
  const controller = new AbortController();
  let adapter: PlannerRuntimeAdapter | undefined;
  let startResolve: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    startResolve = resolve;
  });
  const planner = new PiPublicMessagePlanner({
    adapterFactory: (options) => {
      adapter = new PlannerRuntimeAdapter(options, "", false);
      adapter.onStart = () => {
        startResolve?.();
        return new Promise<RuntimeSessionInfo>(() => undefined);
      };
      adapter.onStop = () => Promise.reject(new Error("test stop failure"));
      return adapter;
    },
  });

  const planning = planner.plan(REQUEST, controller.signal);
  await started;
  controller.abort();

  await assert.rejects(planning, /cancelled/);
  assert.equal(adapter?.stopCount, 1);
});
