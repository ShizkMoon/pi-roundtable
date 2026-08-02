import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import test from "node:test";

import {
  PROTOCOL_VERSION,
  type JsonObject,
  type MeetingCommand,
  type MeetingEvent,
  type RoundtableSession,
  type WorkspaceProfile,
} from "@pi-roundtable/protocol";

import {
  LocalHostProtocolError,
  MAX_LOCAL_HOST_LINE_BYTES,
  parseLocalHostInput,
} from "../local-host-protocol.js";
import { LocalRoundtableHost } from "../local-roundtable-host.js";
import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "../runtime-adapter.js";
import type {
  SubagentRunRequest,
  SubagentRunner,
} from "../subagent-runner.js";
import type {
  PublicMessagePlan,
  PublicMessagePlanner,
  PublicMessagePlanningRequest,
} from "../public-message-planner.js";
import type {
  DiscussionObservationDecision,
  DiscussionObservationRequest,
  DiscussionObserver,
} from "../discussion-observer.js";
import { StdioRuntimeHost } from "../stdio-runtime-host.js";

class FakeRuntimeAdapter implements RuntimeAdapter {
  readonly commands: RuntimeCommand[] = [];
  readonly #listeners = new Set<RuntimeEventListener>();
  startCount = 0;
  stopCount = 0;
  onExecute: ((command: RuntimeCommand) => RuntimeCommandResult | undefined) | undefined;

  constructor(readonly roleId: string) {}

  async start(): Promise<RuntimeSessionInfo> {
    ++this.startCount;
    return {
      runtimeId: `runtime:${this.roleId}`,
      sessionId: `session:${this.roleId}`,
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
  }

  subscribe(listener: RuntimeEventListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  async execute(command: RuntimeCommand): Promise<RuntimeCommandResult> {
    this.commands.push(command);
    return this.onExecute?.(command) ?? { commandId: command.commandId, accepted: true };
  }

  emit(
    kind: RuntimeEvent["kind"],
    payload: JsonObject = {},
    correlationId?: string,
  ): void {
    const event: RuntimeEvent = {
      kind,
      runtimeId: `runtime:${this.roleId}`,
      sessionId: `session:${this.roleId}`,
      occurredAt: "2026-08-01T00:00:00.000Z",
      roleId: this.roleId,
      payload,
    };
    if (correlationId !== undefined) {
      event.correlationId = correlationId;
    }
    for (const listener of this.#listeners) {
      listener(event);
    }
  }
}

class ControlledSubagentRunner implements SubagentRunner {
  readonly requests: SubagentRunRequest[] = [];
  readonly pending: Array<{
    resolve: (result: string) => void;
    reject: (error: Error) => void;
  }> = [];

  run(request: SubagentRunRequest): Promise<string> {
    this.requests.push(request);
    return new Promise<string>((resolve, reject) => {
      this.pending.push({ resolve, reject });
    });
  }
}

class ControlledPublicMessagePlanner implements PublicMessagePlanner {
  readonly requests: PublicMessagePlanningRequest[] = [];

  constructor(readonly outcome: PublicMessagePlan | Error) {}

  async plan(request: PublicMessagePlanningRequest): Promise<PublicMessagePlan> {
    this.requests.push(request);
    if (this.outcome instanceof Error) {
      throw this.outcome;
    }
    return structuredClone(this.outcome);
  }
}

class ControlledDiscussionObserver implements DiscussionObserver {
  readonly requests: DiscussionObservationRequest[] = [];

  constructor(readonly decision: DiscussionObservationDecision) {}

  async observe(request: DiscussionObservationRequest): Promise<DiscussionObservationDecision> {
    this.requests.push(structuredClone(request));
    return structuredClone(this.decision);
  }
}

function createObservedWorkspaceAndSession(): {
  workspace: WorkspaceProfile;
  session: RoundtableSession;
} {
  const workspace = structuredClone(RESUME_WORKSPACE);
  const riskProfile = structuredClone(workspace.roles[0]!);
  riskProfile.roleProfileId = "role.risk";
  riskProfile.displayName = "Risk reviewer";
  riskProfile.description = "Find factual and process errors";
  riskProfile.systemPrompt = "Correct factual, requirement, safety, and meeting-process errors.";
  workspace.roles.push(riskProfile);
  const session = structuredClone(RESUME_SESSION);
  const riskParticipant = structuredClone(session.participants[0]!);
  riskParticipant.participantId = "participant.risk";
  riskParticipant.roleProfileId = "role.risk";
  riskParticipant.displayName = "Risk reviewer";
  riskParticipant.systemPromptSnapshot = riskProfile.systemPrompt;
  session.participants.push(riskParticipant);
  return { workspace, session };
}

function command(
  kind: MeetingCommand["kind"],
  commandId: string,
  overrides: Partial<MeetingCommand> = {},
): MeetingCommand {
  return {
    protocolVersion: PROTOCOL_VERSION,
    meetingId: "meeting-local-test",
    commandId,
    kind,
    issuedAt: "2026-08-01T00:00:00.000Z",
    runtimeGeneration: 1,
    payload: {},
    ...overrides,
  };
}

const TEST_WORKSPACE: WorkspaceProfile = {
  configurationVersion: 1,
  workspaceId: "workspace.test",
  displayName: "Test workspace",
  updatedAt: "2026-08-01T00:00:00.000Z",
  providers: [{
    providerProfileId: "provider.test",
    displayName: "Test provider",
    apiFamily: "custom",
    runtimeProviderId: "test",
    credentialRef: "memory://provider.test",
    enabled: true,
  }],
  models: [{
    modelProfileId: "model.test",
    providerProfileId: "provider.test",
    modelId: "test-model",
    displayName: "Test model",
    capabilities: ["text"],
    enabled: true,
  }],
  skills: [{
    skillId: "skill.test",
    displayName: "Test skill",
    description: "A test-only skill",
    source: { kind: "local", locator: "skills/test/SKILL.md" },
    enabled: true,
  }],
  mcpServers: [],
  roles: [{
    roleProfileId: "role.secretary",
    displayName: "Secretary",
    description: "Test secretary",
    systemPrompt: "Keep the meeting on track.",
    responsibilities: ["Maintain agenda"],
    autoJoin: true,
    modelRoute: {
      primaryModelProfileId: "model.test",
      fallbackModelProfileIds: [],
      thinkingLevel: "medium",
    },
    capabilities: { skillIds: ["skill.test"], mcpGrants: [], toolGrants: [] },
    delegation: {
      networkAccess: "subagent_required",
      resultMode: "summary_with_citations",
      maxConcurrentSubagents: 2,
    },
    memory: {
      mode: "selective",
      writeApproval: "meeting_close",
      promptEvolution: "review_required",
    },
  }],
};

const TEST_SESSION: RoundtableSession = {
  sessionVersion: 1,
  sessionId: "meeting-local-test",
  workspaceId: TEST_WORKSPACE.workspaceId,
  title: "Test meeting",
  phase: "draft",
  createdAt: "2026-08-01T00:00:00.000Z",
  updatedAt: "2026-08-01T00:00:00.000Z",
  agenda: { subject: "Test", objectives: [], constraints: [] },
  participants: [{
    participantId: "participant.secretary",
    scope: "long_term",
    roleProfileId: "role.secretary",
    displayName: "Secretary",
    systemPromptSnapshot: "Keep the meeting on track.",
    modelRouteSnapshot: TEST_WORKSPACE.roles[0]!.modelRoute,
    capabilitiesSnapshot: TEST_WORKSPACE.roles[0]!.capabilities,
    delegationSnapshot: TEST_WORKSPACE.roles[0]!.delegation,
    memoryPolicySnapshot: TEST_WORKSPACE.roles[0]!.memory,
    retentionPolicy: "retain_profile",
  }],
};

const RESUME_WORKSPACE = structuredClone(TEST_WORKSPACE);
RESUME_WORKSPACE.skills = [];
RESUME_WORKSPACE.roles[0]!.capabilities.skillIds = [];
const RESUME_SESSION = structuredClone(TEST_SESSION);
RESUME_SESSION.phase = "live";
RESUME_SESSION.participants[0]!.capabilitiesSnapshot.skillIds = [];

function createHost(
  turnTimeoutMs?: number,
  publicMessagePlanner?: PublicMessagePlanner,
): {
  host: LocalRoundtableHost;
  adapters: Map<string, FakeRuntimeAdapter>;
} {
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    ...(turnTimeoutMs === undefined ? {} : { turnTimeoutMs }),
    ...(publicMessagePlanner === undefined ? {} : { publicMessagePlanner }),
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  return { host, adapters };
}

async function waitFor(
  predicate: () => boolean,
  message: string,
  attempts = 100,
): Promise<void> {
  for (let attempt = 0; attempt < attempts; ++attempt) {
    if (predicate()) {
      return;
    }
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  assert.fail(message);
}

async function waitForTimed(
  predicate: () => boolean,
  message: string,
  timeoutMs = 2_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) {
      return;
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 10));
  }
  assert.fail(message);
}

test("runs a local meeting with long-term and temporary roles", async () => {
  const { host, adapters } = createHost();
  const events: string[] = [];
  host.subscribe((event) => events.push(event.kind));
  host.start();

  assert.equal(
    (
      await host.execute(
        command("role.add", "add-host", {
          actorId: "role.host",
          payload: { displayName: "主持人" },
        }),
      )
    ).status,
    "accepted",
  );
  assert.equal(
    (
      await host.execute(
        command("role.create_temporary", "add-critic", {
          actorId: "role.critic",
          payload: { displayName: "临时质疑者" },
        }),
      )
    ).status,
    "accepted",
  );
  await host.execute(command("meeting.open", "open"));
  const prompt = command("speech.prompt", "prompt-host", {
    actorId: "role.host",
    payload: { message: "总结议题" },
  });
  const receipt = await host.execute(prompt);
  assert.equal(receipt.status, "accepted");
  assert.equal((await host.execute(prompt)).status, "duplicate");

  const hostAdapter = adapters.get("role.host");
  assert.ok(hostAdapter !== undefined);
  hostAdapter.emit("turn.started");
  hostAdapter.emit("turn.delta", { delta: "结论" });
  hostAdapter.emit("turn.completed");

  assert.deepEqual(events, [
    "runtime.lease_acquired",
    "role.registered",
    "role.temporary_registered",
    "meeting.opened",
    "speech.started",
    "speech.delta",
    "speech.completed",
  ]);
  assert.equal(host.sequence, 7);
  await host.stop();
  assert.equal(adapters.get("role.host")?.stopCount, 1);
  assert.equal(adapters.get("role.critic")?.stopCount, 1);
});

test("broadcasts shared and per-role assignments sequentially with a role-exclusive prompt", async () => {
  const { host, adapters } = createHost();
  const events: import("@pi-roundtable/protocol").MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.start();
  await host.execute(command("role.add", "add-a", {
    actorId: "role.a",
    payload: { displayName: "Architect" },
  }));
  await host.execute(command("role.add", "add-b", {
    actorId: "role.b",
    payload: { displayName: "Experience reviewer" },
  }));
  await host.execute(command("role.add", "add-c", {
    actorId: "role.c",
    payload: { displayName: "Risk reviewer" },
  }));
  await host.execute(command("meeting.open", "open"));

  const publicMessage = [
    "Shared requirement: give one acceptance criterion.",
    "@Architect: identify the architecture boundary.",
    "@Experience reviewer: inspect the interaction.",
    "@Risk reviewer: identify the failure mode.",
  ].join("\n");
  const broadcast = await host.execute(command("speech.broadcast", "broadcast", {
    actorId: "user.direct_host",
    payload: { message: publicMessage, mentions: ["role.a", "role.b", "role.c"] },
  }));
  assert.equal(broadcast.status, "accepted");
  assert.equal(events.at(-1)?.kind, "message.published");
  assert.equal(events.at(-1)?.visibility, "public");
  const firstPrompt = adapters.get("role.a")?.commands.at(-1);
  assert.equal(firstPrompt?.kind, "turn.prompt");
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /Shared requirement: give one acceptance criterion/);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /@Experience reviewer: inspect the interaction/);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /Architect \(role\.a\)/);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /only role answering this turn/i);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /Do not draft, simulate, summarize as/);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /shared requirements plus separate @role assignments/i);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /only the assignment addressed to your display name/i);

  adapters.get("role.a")?.emit("turn.started", {}, "broadcast:1");
  adapters.get("role.a")?.emit("turn.completed", {}, "broadcast:1");
  await new Promise<void>((resolve) => setImmediate(resolve));
  const secondPrompt = adapters.get("role.b")?.commands.at(-1);
  assert.equal(secondPrompt?.kind, "turn.prompt");
  assert.match(secondPrompt?.kind === "turn.prompt" ? secondPrompt.message : "", /Shared requirement: give one acceptance criterion/);
  assert.match(secondPrompt?.kind === "turn.prompt" ? secondPrompt.message : "", /@Experience reviewer: inspect the interaction/);
  assert.match(secondPrompt?.kind === "turn.prompt" ? secondPrompt.message : "", /Experience reviewer \(role\.b\)/);
  adapters.get("role.b")?.emit("turn.started", {}, "broadcast:2");
  adapters.get("role.b")?.emit("turn.completed", {}, "broadcast:2");
  await new Promise<void>((resolve) => setImmediate(resolve));
  const thirdPrompt = adapters.get("role.c")?.commands.at(-1);
  assert.equal(thirdPrompt?.kind, "turn.prompt");
  assert.match(thirdPrompt?.kind === "turn.prompt" ? thirdPrompt.message : "", /Shared requirement: give one acceptance criterion/);
  assert.match(thirdPrompt?.kind === "turn.prompt" ? thirdPrompt.message : "", /@Risk reviewer: identify the failure mode/);
  assert.match(thirdPrompt?.kind === "turn.prompt" ? thirdPrompt.message : "", /Risk reviewer \(role\.c\)/);
  adapters.get("role.c")?.emit("turn.started", {}, "broadcast:3");
  adapters.get("role.c")?.emit("turn.completed", {}, "broadcast:3");
  await new Promise<void>((resolve) => setImmediate(resolve));

  const direct = await host.execute(command("speech.direct", "direct-b", {
    actorId: "user.direct_host",
    targetId: "role.b",
    payload: { message: "Keep this risk private" },
  }));
  assert.equal(direct.status, "accepted");
  const directSent = events.find((event) => event.kind === "message.direct_sent");
  assert.equal(directSent?.visibility, "private");
  assert.deepEqual(directSent?.audience, ["user.direct_host", "role.b"]);
  adapters.get("role.b")?.emit("turn.started", {}, "direct-b");
  adapters.get("role.b")?.emit("turn.delta", { delta: "Private answer" }, "direct-b");
  adapters.get("role.b")?.emit("turn.completed", {}, "direct-b");
  const privateSpeech = events.filter((event) =>
    event.causationId === "direct-b" && event.kind.startsWith("speech."));
  assert.equal(privateSpeech.length, 3);
  assert.equal(privateSpeech.every((event) => event.visibility === "private"), true);
  assert.equal(privateSpeech.every((event) => event.audience?.includes("role.a") !== true), true);
  await host.stop();
});

test("uses an invisible semantic plan to route arbitrary-order shared, individual, and group tasks", async () => {
  const publicMessage = [
    "@Risk reviewer：先指出会阻塞上线的风险。",
    "共同要求：每项建议都给出验收标准。",
    "@Architect：在风险之后给出系统边界。",
    "@Experience reviewer：最后检查用户是否能理解流程。",
    "@Architect 和 @Risk reviewer：共同确认恢复路径。",
  ].join("\n");
  const semanticPlan: PublicMessagePlan = {
    sharedRequirements: ["共同要求：每项建议都给出验收标准。"],
    roleTasks: {
      "role.a": ["@Architect：在风险之后给出系统边界。"],
      "role.b": ["@Experience reviewer：最后检查用户是否能理解流程。"],
      "role.c": ["@Risk reviewer：先指出会阻塞上线的风险。"],
    },
    groupTasks: [{
      roleIds: ["role.a", "role.c"],
      task: "@Architect 和 @Risk reviewer：共同确认恢复路径。",
    }],
    speakerOrder: ["role.c", "role.a", "role.b"],
  };
  const planner = new ControlledPublicMessagePlanner(semanticPlan);
  const { host, adapters } = createHost(undefined, planner);
  const events: MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.start();
  await host.execute(command("role.add", "add-a", {
    actorId: "role.a",
    payload: { displayName: "Architect" },
  }));
  await host.execute(command("role.add", "add-b", {
    actorId: "role.b",
    payload: { displayName: "Experience reviewer" },
  }));
  await host.execute(command("role.add", "add-c", {
    actorId: "role.c",
    payload: { displayName: "Risk reviewer" },
  }));
  await host.execute(command("meeting.open", "open"));

  const receipt = await host.execute(command("speech.broadcast", "semantic-broadcast", {
    actorId: "user.direct_host",
    payload: { message: publicMessage, mentions: ["role.a", "role.b", "role.c"] },
  }));

  assert.equal(receipt.status, "accepted");
  assert.equal(planner.requests.length, 1);
  assert.equal(planner.requests[0]?.message, publicMessage);
  assert.deepEqual(
    planner.requests[0]?.roles.map((role) => role.roleId),
    ["role.a", "role.b", "role.c"],
  );
  const published = events.find((event) => event.kind === "message.published");
  assert.deepEqual(published?.payload, {
    message: publicMessage,
    mentions: ["role.a", "role.b", "role.c"],
  });
  const firstPrompt = adapters.get("role.c")?.commands.at(-1);
  assert.equal(firstPrompt?.kind, "turn.prompt");
  const firstMessage = firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "";
  const hiddenRouting = firstMessage.split("[Hidden semantic routing;")[1] ?? "";
  assert.match(hiddenRouting, /共同要求：每项建议都给出验收标准/);
  assert.match(hiddenRouting, /先指出会阻塞上线的风险/);
  assert.match(hiddenRouting, /共同确认恢复路径/);
  assert.doesNotMatch(hiddenRouting, /最后检查用户是否能理解流程/);

  adapters.get("role.c")?.emit("turn.started", {}, "semantic-broadcast:1");
  adapters.get("role.c")?.emit("turn.completed", {}, "semantic-broadcast:1");
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(adapters.get("role.a")?.commands.at(-1)?.kind, "turn.prompt");
  await host.stop();
});

test("falls back to explicit mention order when invisible semantic planning fails", async () => {
  const planner = new ControlledPublicMessagePlanner(new Error("planner unavailable"));
  const { host, adapters } = createHost(undefined, planner);
  host.start();
  await host.execute(command("role.add", "add-a", {
    actorId: "role.a",
    payload: { displayName: "A" },
  }));
  await host.execute(command("role.add", "add-b", {
    actorId: "role.b",
    payload: { displayName: "B" },
  }));
  await host.execute(command("meeting.open", "open"));

  const receipt = await host.execute(command("speech.broadcast", "fallback-broadcast", {
    actorId: "user.direct_host",
    payload: { message: "@A 与 @B 分别检查。", mentions: ["role.b", "role.a"] },
  }));

  assert.equal(receipt.status, "accepted");
  assert.equal(adapters.get("role.b")?.commands.at(-1)?.kind, "turn.prompt");
  assert.equal(adapters.get("role.a")?.commands.length, 0);
  await host.stop();
});

test("dispatches an explicitly mentioned public turn only to that role", async () => {
  const { host, adapters } = createHost();
  host.start();
  await host.execute(command("role.add", "add-a", {
    actorId: "role.a",
    payload: { displayName: "Architect" },
  }));
  await host.execute(command("role.add", "add-b", {
    actorId: "role.b",
    payload: { displayName: "Experience reviewer" },
  }));
  await host.execute(command("role.add", "add-c", {
    actorId: "role.c",
    payload: { displayName: "Risk reviewer" },
  }));
  await host.execute(command("meeting.open", "open"));

  const receipt = await host.execute(command("speech.broadcast", "mention-b", {
    actorId: "user.direct_host",
    payload: { message: "@Experience reviewer focus on usability", mentions: ["role.b"] },
  }));

  assert.equal(receipt.status, "accepted");
  assert.equal(adapters.get("role.a")?.commands.filter((entry) => entry.kind === "turn.prompt").length, 0);
  assert.equal(adapters.get("role.b")?.commands.filter((entry) => entry.kind === "turn.prompt").length, 1);
  assert.equal(adapters.get("role.c")?.commands.filter((entry) => entry.kind === "turn.prompt").length, 0);
  const mentionedPrompt = adapters.get("role.b")?.commands.at(-1);
  assert.match(
    mentionedPrompt?.kind === "turn.prompt" ? mentionedPrompt.message : "",
    /only role answering this turn/i,
  );
  await host.stop();
});

test("lets an isolated role observer request a budgeted critical interruption in free discussion", async () => {
  const { workspace, session } = createObservedWorkspaceAndSession();

  const observer = new ControlledDiscussionObserver({
    action: "interrupt",
    kind: "critical",
    reason: "同步服务器直接执行所有模型调用",
    prompt: "纠正执行边界，并说明为什么模型执行应保留在本地 Runtime。",
  });
  const planner = new ControlledPublicMessagePlanner({
    sharedRequirements: [],
    roleTasks: { "participant.secretary": [] },
    groupTasks: [],
    speakerOrder: ["participant.secretary"],
  });
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    publicMessagePlanner: planner,
    discussionObserver: observer,
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  const events: MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.initializeRuntimeConfiguration(workspace, session, {
    "memory://provider.test": "runtime-secret",
  });
  host.start();
  await host.execute(command("role.add", "add-secretary", {
    actorId: "participant.secretary",
  }));
  await host.execute(command("role.add", "add-risk", {
    actorId: "participant.risk",
  }));
  await host.execute(command("meeting.open", "open-observed"));
  await host.execute(command("discussion.configure", "configure-observed", {
    actorId: "user.direct_host",
    payload: {
      agendaItems: ["Runtime ownership"],
      limits: {
        softTurnLimit: 8,
        hardTurnLimit: 12,
        softRoundLimit: 2,
        hardRoundLimit: 3,
        maxConsecutiveTurnsPerRole: 2,
        maxInterruptionsPerSegment: 2,
        maxInterruptionsPerRole: 1,
        noProgressTurnLimit: 2,
        maxObserverProbesPerSegment: 4,
      },
    },
  }));
  await host.execute(command("discussion.mode.set", "free-observed", {
    actorId: "user.direct_host",
    payload: { mode: "free_discussion", reason: "test" },
  }));
  await host.execute(command("speech.broadcast", "broadcast-observed", {
    actorId: "user.direct_host",
    payload: {
      message: "请讨论模型执行边界。",
      mentions: ["participant.secretary"],
    },
  }));
  const speaker = adapters.get("participant.secretary");
  assert.ok(speaker !== undefined);
  const turn = speaker.commands.at(-1);
  assert.equal(turn?.kind, "turn.prompt");
  const correlationId = turn?.commandId;
  assert.ok(correlationId !== undefined);
  speaker.emit("turn.started", {}, correlationId);
  speaker.emit("turn.delta", {
    delta: `同步服务器直接执行所有模型调用。${"这是一段仍在继续的公开发言。".repeat(20)}`,
  }, correlationId);

  await waitFor(
    () => events.some((event) => event.kind === "interruption.requested"),
    "observer should request a critical interruption before the speech completes",
  );

  assert.equal(observer.requests.length, 1);
  assert.equal(observer.requests[0]?.candidateRoleId, "participant.risk");
  assert.equal(observer.requests[0]?.speechComplete, false);
  const floorIndex = events.findIndex((event) => event.kind === "floor.requested");
  const interruptionIndex = events.findIndex((event) => event.kind === "interruption.requested");
  assert.ok(floorIndex >= 0 && interruptionIndex > floorIndex);
  assert.equal(events[floorIndex]?.actorId, "participant.risk");
  assert.equal(events[floorIndex]?.payload.kind, "critical");
  assert.ok(events.some((event) =>
    event.kind === "discussion.budget_updated" && event.payload.observerProbes === 1));
  assert.equal(speaker.commands.at(-1)?.kind, "turn.cancel");
  await host.stop();
});

test("runs one final observer probe when a completed speech adds no new text", async () => {
  const { workspace, session } = createObservedWorkspaceAndSession();
  const observer = new ControlledDiscussionObserver({ action: "none" });
  const planner = new ControlledPublicMessagePlanner({
    sharedRequirements: [],
    roleTasks: { "participant.secretary": [] },
    groupTasks: [],
    speakerOrder: ["participant.secretary"],
  });
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    publicMessagePlanner: planner,
    discussionObserver: observer,
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  const events: MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.initializeRuntimeConfiguration(workspace, session, {
    "memory://provider.test": "runtime-secret",
  });
  host.start();
  await host.execute(command("role.add", "add-final-secretary", {
    actorId: "participant.secretary",
  }));
  await host.execute(command("role.add", "add-final-risk", {
    actorId: "participant.risk",
  }));
  await host.execute(command("meeting.open", "open-final-observer"));
  await host.execute(command("discussion.configure", "configure-final-observer", {
    actorId: "user.direct_host",
    payload: {
      agendaItems: ["Runtime ownership"],
      limits: { maxObserverProbesPerSegment: 4 },
    },
  }));
  await host.execute(command("discussion.mode.set", "free-final-observer", {
    actorId: "user.direct_host",
    payload: { mode: "free_discussion", reason: "test" },
  }));
  await host.execute(command("speech.broadcast", "broadcast-final-observer", {
    actorId: "user.direct_host",
    payload: {
      message: "请讨论模型执行边界。",
      mentions: ["participant.secretary"],
    },
  }));

  const speaker = adapters.get("participant.secretary");
  const turn = speaker?.commands.at(-1);
  assert.equal(turn?.kind, "turn.prompt");
  const correlationId = turn?.commandId;
  assert.ok(correlationId !== undefined);
  speaker?.emit("turn.started", {}, correlationId);
  speaker?.emit("turn.delta", {
    delta: "这是一段足以触发流式观察、但完成帧不会追加任何字符的公开发言。".repeat(12),
  }, correlationId);
  await waitFor(() => observer.requests.length === 1, "streaming observer probe should run");
  speaker?.emit("turn.completed", {}, correlationId);
  await waitFor(() => observer.requests.length === 2, "final observer probe should run");

  assert.equal(observer.requests[0]?.speechComplete, false);
  assert.equal(observer.requests[1]?.speechComplete, true);
  const probeCounters = events
    .filter((event) => event.kind === "discussion.budget_updated")
    .map((event) => event.payload.observerProbes);
  assert.deepEqual(probeCounters.slice(-2), [1, 2]);
  await host.stop();
});

test("automatically converges a no-progress discussion and completes after one facilitator turn", async () => {
  const { host, adapters } = createHost();
  const events: MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.start();
  await host.execute(command("role.add", "add-facilitator", {
    actorId: "role.host",
    payload: { displayName: "主持人" },
  }));
  await host.execute(command("role.add", "add-expert", {
    actorId: "role.expert",
    payload: { displayName: "专家" },
  }));
  await host.execute(command("meeting.open", "open-convergence"));
  await host.execute(command("discussion.configure", "configure-convergence", {
    actorId: "user.direct_host",
    payload: {
      agendaItems: ["确定边界"],
      limits: {
        softTurnLimit: 3,
        hardTurnLimit: 4,
        softRoundLimit: 2,
        hardRoundLimit: 3,
        maxConsecutiveTurnsPerRole: 2,
        maxInterruptionsPerSegment: 2,
        maxInterruptionsPerRole: 1,
        noProgressTurnLimit: 1,
        maxObserverProbesPerSegment: 0,
      },
    },
  }));

  const completeExpertTurn = async (commandId: string, output: string): Promise<void> => {
    const receipt = await host.execute(command("speech.broadcast", commandId, {
      actorId: "user.direct_host",
      payload: { message: "请继续。", mentions: ["role.expert"] },
    }));
    assert.equal(receipt.status, "accepted");
    const expert = adapters.get("role.expert");
    const turn = expert?.commands.at(-1);
    assert.equal(turn?.kind, "turn.prompt");
    const correlationId = turn?.commandId;
    assert.ok(correlationId !== undefined);
    expert?.emit("turn.started", {}, correlationId);
    expert?.emit("turn.delta", { delta: output }, correlationId);
    expert?.emit("turn.completed", {}, correlationId);
    await waitFor(
      () => events.some((event) =>
        event.kind === "discussion.budget_updated" && event.causationId === correlationId),
      "discussion budget should be updated after a public turn",
    );
  };

  await completeExpertTurn("round-one", "我暂时没有新增结论。");
  assert.equal(events.some((event) =>
    event.kind === "discussion.mode_changed" && event.payload.mode === "free_discussion"), false);
  assert.equal((await host.execute(command("agenda.advance", "advance-agenda", {
    actorId: "user.direct_host",
    payload: { reason: "host_completed_item" },
  }))).status, "accepted");
  await waitFor(
    () => events.some((event) =>
      event.kind === "discussion.mode_changed" && event.payload.mode === "free_discussion"),
    "the host should explicitly advance the single agenda item into free discussion",
  );
  await completeExpertTurn("round-two", "我仍然没有新增结论。");
  await waitFor(
    () => events.some((event) =>
      event.kind === "discussion.mode_changed" && event.payload.mode === "convergence"),
    "the configured free-discussion no-progress limit should trigger convergence",
  );

  const facilitator = adapters.get("role.host");
  await waitFor(
    () => facilitator?.commands.at(-1)?.kind === "turn.prompt",
    "the facilitator should receive one automatic convergence turn",
  );
  const convergenceTurn = facilitator?.commands.at(-1);
  assert.match(
    convergenceTurn?.kind === "turn.prompt" ? convergenceTurn.message : "",
    /已有决策、未解决异议/,
  );
  const convergenceCorrelationId = convergenceTurn?.commandId;
  assert.ok(convergenceCorrelationId !== undefined);
  facilitator?.emit("turn.started", {}, convergenceCorrelationId);
  facilitator?.emit("turn.delta", { delta: "决策：停止重复讨论并记录当前边界。" }, convergenceCorrelationId);
  facilitator?.emit("turn.completed", {}, convergenceCorrelationId);
  await waitFor(
    () => events.some((event) =>
      event.kind === "discussion.mode_changed" && event.payload.mode === "completed"),
    "one convergence turn should complete the bounded discussion",
  );

  assert.ok(events.some((event) =>
    event.kind === "convergence.recorded" && event.payload.complete === true));
  assert.equal(events.filter((event) =>
    event.kind === "floor.requested" && event.payload.automatic === true).length, 1);
  await host.stop();
});

test("resolves a frozen participant manifest into private Pi runtime options", async () => {
  const runtimeDirectory = mkdtempSync(join(tmpdir(), "pi-roundtable-runtime-"));
  mkdirSync(join(runtimeDirectory, "skills", "test"), { recursive: true });
  writeFileSync(join(runtimeDirectory, "skills", "test", "SKILL.md"), "# Test Skill\n");
  let resolved: import("../local-roundtable-host.js").ResolvedRoleRuntimeConfiguration | undefined;
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeGeneration: 1,
    cwd: runtimeDirectory,
    adapterFactory: (roleId, configuration) => {
      resolved = configuration;
      return new FakeRuntimeAdapter(roleId);
    },
  });
  try {
    host.initializeRuntimeConfiguration(TEST_WORKSPACE, TEST_SESSION, {
      "memory://provider.test": "runtime-secret",
    });
    host.start();
    const receipt = await host.execute(command("role.add", "add-secretary", {
      actorId: "participant.secretary",
      payload: { displayName: "Untrusted override" },
    }));

    assert.equal(receipt.status, "accepted");
    assert.equal(resolved?.providerId, "test");
    assert.equal(resolved?.providerName, "Test provider");
    assert.equal(resolved?.apiFamily, "custom");
    assert.equal(resolved?.endpoint, undefined);
    assert.equal(resolved?.modelId, "test-model");
    assert.equal(resolved?.modelName, "Test model");
    assert.deepEqual(resolved?.modelCapabilities, ["text"]);
    assert.equal(resolved?.apiKey, "runtime-secret");
    assert.equal(resolved?.systemPrompt, "Keep the meeting on track.");
    assert.equal(resolved?.skillPaths.length, 1);
    assert.equal(resolved?.skillPaths[0]?.endsWith("skills\\test\\SKILL.md") || resolved?.skillPaths[0]?.endsWith("skills/test/SKILL.md"), true);
  } finally {
    await host.stop();
    rmSync(runtimeDirectory, { recursive: true, force: true });
  }
});

test("resolves only verified Git Skill installations from the catalog root", async () => {
  const runtimeDirectory = mkdtempSync(join(tmpdir(), "pi-roundtable-git-skill-"));
  const catalogRoot = join(runtimeDirectory, "catalog", "skills");
  const installedSkill = join(catalogRoot, "skill.test");
  mkdirSync(installedSkill, { recursive: true });
  writeFileSync(join(installedSkill, "SKILL.md"), "# Installed Skill\n");
  const workspace = structuredClone(TEST_WORKSPACE);
  workspace.skills[0]!.source = {
    kind: "git",
    locator: "https://github.com/example/test-skill",
    contentDigest: "sha256:test",
  };
  workspace.skills[0]!.importStatus = "installed";
  workspace.skills[0]!.installDirectory = installedSkill;
  let resolved: import("../local-roundtable-host.js").ResolvedRoleRuntimeConfiguration | undefined;
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeGeneration: 1,
    cwd: runtimeDirectory,
    catalogSkillRoot: catalogRoot,
    adapterFactory: (roleId, configuration) => {
      resolved = configuration;
      return new FakeRuntimeAdapter(roleId);
    },
  });
  try {
    host.initializeRuntimeConfiguration(workspace, TEST_SESSION, {
      "memory://provider.test": "runtime-secret",
    });
    host.start();
    const receipt = await host.execute(command("role.add", "add-git-skill", {
      actorId: "participant.secretary",
    }));
    assert.equal(receipt.status, "accepted", receipt.message ?? receipt.errorCode ?? undefined);
    assert.equal(resolved?.skillPaths[0], installedSkill);
  } finally {
    await host.stop();
    rmSync(runtimeDirectory, { recursive: true, force: true });
  }
});

test("rejects a registered Git Skill that lacks an installed digest", async () => {
  const workspace = structuredClone(TEST_WORKSPACE);
  workspace.skills[0]!.source = {
    kind: "git",
    locator: "https://github.com/example/test-skill",
  };
  workspace.skills[0]!.importStatus = "registered";
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeGeneration: 1,
    adapterFactory: (roleId) => new FakeRuntimeAdapter(roleId),
  });
  try {
    host.initializeRuntimeConfiguration(workspace, TEST_SESSION, {
      "memory://provider.test": "runtime-secret",
    });
    host.start();
    const receipt = await host.execute(command("role.add", "add-uninstalled-git-skill", {
      actorId: "participant.secretary",
    }));
    assert.equal(receipt.status, "rejected");
    assert.equal(receipt.errorCode, "invalid_role_manifest");
  } finally {
    await host.stop();
  }
});

test("resolves an approved Git MCP grant and its credential references", async () => {
  const runtimeDirectory = mkdtempSync(join(tmpdir(), "pi-roundtable-git-mcp-"));
  const catalogRoot = join(runtimeDirectory, "catalog", "mcp");
  const installation = join(catalogRoot, "mcp.test");
  mkdirSync(installation, { recursive: true });
  writeFileSync(join(installation, "server.js"), "export {};\n");
  const workspace = structuredClone(TEST_WORKSPACE);
  workspace.mcpServers = [{
    mcpServerId: "mcp.test",
    displayName: "Test MCP",
    source: {
      kind: "git",
      locator: "https://github.com/example/test-mcp",
      contentDigest: "sha256:test-mcp",
    },
    risk: "low",
    importStatus: "installed",
    installDirectory: installation,
    contentDigest: "sha256:test-mcp",
    transport: "stdio",
    command: "node",
    arguments: ["server.js"],
    workingDirectory: installation,
    environmentCredentialRefs: { TEST_TOKEN: "memory://mcp.test/token" },
    toolCatalog: [{ name: "echo", displayName: "Echo" }],
    enabled: true,
  }];
  const session = structuredClone(TEST_SESSION);
  session.participants[0]!.capabilitiesSnapshot.skillIds = [];
  session.participants[0]!.capabilitiesSnapshot.mcpGrants = [{
    mcpServerId: "mcp.test",
    toolAllowlist: ["echo"],
    approvalMode: "never",
    executionMode: "subagent_preferred",
  }];
  let resolved: import("../local-roundtable-host.js").ResolvedRoleRuntimeConfiguration | undefined;
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeGeneration: 1,
    cwd: runtimeDirectory,
    catalogMcpRoot: catalogRoot,
    adapterFactory: (roleId, configuration) => {
      resolved = configuration;
      return new FakeRuntimeAdapter(roleId);
    },
  });
  try {
    host.initializeRuntimeConfiguration(workspace, session, {
      "memory://provider.test": "runtime-secret",
      "memory://mcp.test/token": "mcp-secret",
    });
    host.start();
    const receipt = await host.execute(command("role.add", "add-mcp-role", {
      actorId: "participant.secretary",
    }));
    assert.equal(receipt.status, "accepted");
    assert.equal(resolved?.mcpServers[0]?.serverId, "mcp.test");
    assert.deepEqual(resolved?.mcpServers[0]?.toolAllowlist, ["echo"]);
    assert.equal(resolved?.mcpServers[0]?.environment?.TEST_TOKEN, "mcp-secret");
  } finally {
    await host.stop();
    rmSync(runtimeDirectory, { recursive: true, force: true });
  }
});

test("rejects a Skill locator whose symlink escapes approved roots", async () => {
  const runtimeDirectory = mkdtempSync(join(tmpdir(), "pi-roundtable-root-"));
  const outsideDirectory = mkdtempSync(join(tmpdir(), "pi-roundtable-outside-"));
  mkdirSync(join(runtimeDirectory, "skills"), { recursive: true });
  symlinkSync(
    outsideDirectory,
    join(runtimeDirectory, "skills", "escape"),
    process.platform === "win32" ? "junction" : "dir",
  );
  const workspace = structuredClone(TEST_WORKSPACE);
  workspace.skills[0]!.source.locator = "skills/escape";
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeGeneration: 1,
    cwd: runtimeDirectory,
    adapterFactory: (roleId) => new FakeRuntimeAdapter(roleId),
  });
  try {
    host.initializeRuntimeConfiguration(workspace, TEST_SESSION, {
      "memory://provider.test": "runtime-secret",
    });
    host.start();
    const receipt = await host.execute(command("role.add", "add-secretary", {
      actorId: "participant.secretary",
    }));
    assert.equal(receipt.status, "rejected");
    assert.equal(receipt.errorCode, "invalid_role_manifest");
  } finally {
    await host.stop();
    rmSync(runtimeDirectory, { recursive: true, force: true });
    rmSync(outsideDirectory, { recursive: true, force: true });
  }
});

test("rejects runtime generations outside the public contract", () => {
  for (const runtimeGeneration of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
    assert.throws(
      () => new LocalRoundtableHost({ meetingId: "meeting-local-test", runtimeGeneration }),
      /positive safe integer/,
    );
  }
});

test("accepts dynamic temporary invitations only from an active long-term inviter", async () => {
  const workspace = structuredClone(TEST_WORKSPACE);
  workspace.skills = [];
  workspace.roles[0]!.capabilities = { skillIds: [], mcpGrants: [], toolGrants: [] };
  const session = structuredClone(TEST_SESSION);
  session.participants[0]!.capabilitiesSnapshot = { skillIds: [], mcpGrants: [], toolGrants: [] };
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeGeneration: 1,
    adapterFactory: (roleId) => new FakeRuntimeAdapter(roleId),
  });
  const manifest = {
    participantId: "participant.planner",
    scope: "temporary" as const,
    displayName: "Planner",
    systemPromptSnapshot: "Plan only within this meeting.",
    modelRouteSnapshot: workspace.roles[0]!.modelRoute,
    capabilitiesSnapshot: { skillIds: [], mcpGrants: [], toolGrants: [] },
    delegationSnapshot: workspace.roles[0]!.delegation,
    memoryPolicySnapshot: {
      mode: "disabled" as const,
      writeApproval: "always" as const,
      promptEvolution: "disabled" as const,
    },
    invitation: {
      invitationId: "invite.planner",
      inviterType: "role" as const,
      inviterId: "participant.secretary",
      purpose: "Plan the current meeting",
      status: "accepted" as const,
      createdAt: "2026-08-01T00:01:00.000Z",
      acceptedAt: "2026-08-01T00:01:00.000Z",
    },
    retentionPolicy: "review_at_close" as const,
  };
  host.initializeRuntimeConfiguration(workspace, session, {
    "memory://provider.test": "runtime-secret",
  });
  host.start();
  try {
    const beforeLive = await host.execute(command("role.create_temporary", "invite-before-live", {
      actorId: manifest.participantId,
      payload: { participantManifest: manifest as unknown as JsonObject },
    }));
    assert.equal(beforeLive.status, "rejected");
    assert.equal(beforeLive.errorCode, "invalid_role_manifest");

    assert.equal((await host.execute(command("role.add", "add-secretary", {
      actorId: "participant.secretary",
    }))).status, "accepted");
    assert.equal((await host.execute(command("meeting.open", "open"))).status, "accepted");

    const duringLive = await host.execute(command("role.create_temporary", "invite-during-live", {
      actorId: manifest.participantId,
      payload: { participantManifest: manifest as unknown as JsonObject },
    }));
    assert.equal(duringLive.status, "accepted");
  } finally {
    await host.stop();
  }
});

test("publishes the interruption reason and hands the floor to an interrupting role after cancellation", async () => {
  const { host, adapters } = createHost();
  const events: import("@pi-roundtable/protocol").MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.start();
  await host.execute(
    command("role.add", "add-a", { actorId: "role.a", payload: { displayName: "A" } }),
  );
  await host.execute(
    command("role.add", "add-b", { actorId: "role.b", payload: { displayName: "B" } }),
  );
  await host.execute(command("meeting.open", "open"));
  await host.execute(
    command("speech.prompt", "prompt-a", {
      actorId: "role.a",
      payload: { message: "A speaks" },
    }),
  );
  adapters.get("role.a")?.emit("turn.started");

  const receipt = await host.execute(
    command("speech.interrupt", "interrupt-b", {
      actorId: "role.b",
      targetId: "role.a",
      payload: { message: "B takes the floor" },
    }),
  );
  assert.equal(receipt.status, "accepted");
  assert.equal(adapters.get("role.a")?.commands.at(-1)?.kind, "turn.cancel");
  adapters.get("role.a")?.emit("turn.cancelled");
  await new Promise<void>((resolve) => setImmediate(resolve));
  const handoff = adapters.get("role.b")?.commands.at(-1);
  assert.equal(handoff?.kind, "turn.prompt");
  assert.equal(handoff?.commandId, "interrupt-b");
  assert.deepEqual(events.slice(-2).map((event) => event.kind), ["interruption.requested", "speech.cancelled"]);
  const interruption = events.find((event) => event.kind === "interruption.requested");
  assert.equal(interruption?.actorId, "role.b");
  assert.equal(interruption?.targetId, "role.a");
  assert.equal(interruption?.payload.message, "B takes the floor");
  await host.stop();
});

test("preserves a sanitized runtime failure reason on the public terminal event", async () => {
  const { host, adapters } = createHost();
  const terminalEvents: MeetingEvent[] = [];
  host.subscribe((event) => {
    if (event.kind === "speech.cancelled") {
      terminalEvents.push(event);
    }
  });
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("meeting.open", "open"));
  await host.execute(command("speech.prompt", "prompt-a", {
    actorId: "role.a",
    payload: { message: "A speaks" },
  }));
  const roleA = adapters.get("role.a");
  assert.ok(roleA !== undefined);
  roleA.emit("turn.started", {}, "prompt-a");
  roleA.emit("turn.cancelled", {
    reason: "failed",
    errorCode: "pi_retry_exhausted",
  }, "prompt-a");

  assert.equal(terminalEvents.length, 1);
  assert.equal(terminalEvents[0]?.payload.reason, "failed");
  assert.equal(terminalEvents[0]?.payload.errorCode, "pi_retry_exhausted");
  await host.stop();
});

test("cancels a timed-out turn and emits one retryable timeout terminal event", async () => {
  const { host, adapters } = createHost(20);
  const terminalEvents: MeetingEvent[] = [];
  host.subscribe((event) => {
    if (event.kind === "speech.cancelled") {
      terminalEvents.push(event);
    }
  });
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("meeting.open", "open"));
  await host.execute(command("speech.prompt", "prompt-timeout", {
    actorId: "role.a",
    payload: { message: "hang" },
  }));
  const roleA = adapters.get("role.a");
  assert.ok(roleA !== undefined);
  roleA.onExecute = (runtimeCommand) => {
    if (runtimeCommand.kind === "turn.cancel") {
      roleA.emit("turn.cancelled", { reason: "cancelled" }, "prompt-timeout");
    }
    return undefined;
  };
  roleA.emit("turn.started", {}, "prompt-timeout");

  await new Promise((resolve) => setTimeout(resolve, 40));
  assert.equal(terminalEvents.length, 1);

  assert.equal(roleA.commands.at(-1)?.kind, "turn.cancel");
  assert.equal(roleA.commands.at(-1)?.commandId, "prompt-timeout:timeout");
  assert.equal(terminalEvents[0]?.payload.reason, "timeout");
  assert.equal(terminalEvents[0]?.payload.errorCode, "turn_timeout");
  await new Promise((resolve) => setTimeout(resolve, 30));
  assert.equal(terminalEvents.length, 1);
  await host.stop();
});

test("retries only the failed role without repeating completed role output", async () => {
  const { host, adapters } = createHost();
  const terminalEvents: MeetingEvent[] = [];
  host.subscribe((event) => {
    if (event.kind === "speech.completed" || event.kind === "speech.cancelled") {
      terminalEvents.push(event);
    }
  });
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("role.add", "add-b", { actorId: "role.b" }));
  await host.execute(command("meeting.open", "open"));
  await host.execute(command("speech.broadcast", "round-one", {
    actorId: "user.direct_host",
    payload: { message: "Answer once", mentions: ["role.a", "role.b"] },
  }));
  const roleA = adapters.get("role.a");
  const roleB = adapters.get("role.b");
  assert.ok(roleA !== undefined);
  assert.ok(roleB !== undefined);
  const roleAFirst = roleA.commands.find((item) => item.kind === "turn.prompt");
  assert.ok(roleAFirst !== undefined);
  roleA.emit("turn.started", {}, roleAFirst.commandId);
  roleA.emit("turn.completed", {}, roleAFirst.commandId);
  await waitFor(
    () => roleB.commands.some((item) => item.kind === "turn.prompt"),
    "role B did not receive its queued turn",
  );
  const roleBFirst = roleB.commands.find((item) => item.kind === "turn.prompt");
  assert.ok(roleBFirst !== undefined);
  roleB.emit("turn.started", {}, roleBFirst.commandId);
  roleB.emit("turn.cancelled", {
    reason: "failed",
    errorCode: "pi_retry_exhausted",
  }, roleBFirst.commandId);

  await host.execute(command("speech.broadcast", "retry-b", {
    actorId: "user.direct_host",
    payload: { message: "Answer once", mentions: ["role.b"] },
  }));
  const roleBPrompts = roleB.commands.filter((item) => item.kind === "turn.prompt");
  assert.equal(roleBPrompts.length, 2);
  roleB.emit("turn.started", {}, roleBPrompts[1]!.commandId);
  roleB.emit("turn.completed", {}, roleBPrompts[1]!.commandId);

  assert.equal(roleA.commands.filter((item) => item.kind === "turn.prompt").length, 1);
  assert.deepEqual(
    terminalEvents.map((event) => `${event.actorId}:${event.kind}`),
    [
      "role.a:speech.completed",
      "role.b:speech.cancelled",
      "role.b:speech.completed",
    ],
  );
  await host.stop();
});

test("serializes duplicate commands before checking idempotent receipts", async () => {
  const { host, adapters } = createHost();
  host.start();
  const add = command("role.add", "add-once", {
    actorId: "role.once",
    payload: { displayName: "Once" },
  });

  const [first, duplicate] = await Promise.all([host.execute(add), host.execute(add)]);
  assert.equal(first.status, "accepted");
  assert.equal(duplicate.status, "duplicate");
  assert.equal(adapters.get("role.once")?.startCount, 1);
  await host.stop();
});

test("orders a synchronous cancellation after the interruption request", async () => {
  const { host, adapters } = createHost();
  const events: string[] = [];
  host.subscribe((event) => events.push(event.kind));
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("role.add", "add-b", { actorId: "role.b" }));
  await host.execute(command("meeting.open", "open"));
  await host.execute(
    command("speech.prompt", "prompt-a", {
      actorId: "role.a",
      payload: { message: "A speaks" },
    }),
  );
  const roleA = adapters.get("role.a");
  assert.ok(roleA !== undefined);
  roleA.emit("turn.started", {}, "prompt-a");
  roleA.onExecute = (runtimeCommand) => {
    if (runtimeCommand.kind === "turn.cancel") {
      roleA.emit("turn.cancelled", {}, "prompt-a");
    }
    return undefined;
  };

  const receipt = await host.execute(
    command("speech.interrupt", "interrupt-b", {
      actorId: "role.b",
      targetId: "role.a",
      payload: { message: "B takes the floor" },
    }),
  );
  assert.equal(receipt.status, "accepted");
  assert.deepEqual(events.slice(-2), ["interruption.requested", "speech.cancelled"]);
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(adapters.get("role.b")?.commands.at(-1)?.kind, "turn.prompt");
  await host.stop();
});

test("ignores terminal events from an older turn correlation", async () => {
  const { host, adapters } = createHost();
  const events: string[] = [];
  host.subscribe((event) => events.push(event.kind));
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("meeting.open", "open"));
  const roleA = adapters.get("role.a");
  assert.ok(roleA !== undefined);

  await host.execute(
    command("speech.prompt", "prompt-1", {
      actorId: "role.a",
      payload: { message: "first" },
    }),
  );
  roleA.emit("turn.started", {}, "prompt-1");
  roleA.emit("turn.completed", {}, "prompt-1");
  await host.execute(
    command("speech.prompt", "prompt-2", {
      actorId: "role.a",
      payload: { message: "second" },
    }),
  );
  roleA.emit("turn.started", {}, "prompt-2");
  roleA.emit("turn.completed", {}, "prompt-1");
  roleA.emit("turn.delta", { delta: "current" }, "prompt-2");
  roleA.emit("turn.completed", {}, "prompt-2");

  assert.deepEqual(events.slice(-3), ["speech.started", "speech.delta", "speech.completed"]);
  await host.stop();
});

test("ignores a delayed turn start after cancelling pending prompt dispatch", async () => {
  const { host, adapters } = createHost();
  const events: string[] = [];
  host.subscribe((event) => events.push(event.kind));
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("meeting.open", "open"));
  await host.execute(
    command("speech.prompt", "prompt-pending", {
      actorId: "role.a",
      payload: { message: "pending" },
    }),
  );
  const cancelled = await host.execute(
    command("generation.cancel", "cancel-pending", { targetId: "role.a" }),
  );
  assert.equal(cancelled.status, "accepted");
  adapters.get("role.a")?.emit("turn.started", {}, "prompt-pending");
  adapters.get("role.a")?.emit("turn.completed", {}, "prompt-pending");
  assert.equal(events.some((kind) => kind.startsWith("speech.")), false);
  await host.stop();
});

test("isolates listener failures and hides runtime error details", async () => {
  const { host, adapters } = createHost();
  const observed: string[] = [];
  host.subscribe(() => {
    throw new Error("presentation failed");
  });
  host.subscribe((event) => observed.push(event.kind));
  host.start();
  await host.execute(command("role.add", "add-a", { actorId: "role.a" }));
  await host.execute(command("meeting.open", "open"));
  const roleA = adapters.get("role.a");
  assert.ok(roleA !== undefined);
  roleA.onExecute = (runtimeCommand) => ({
    commandId: runtimeCommand.commandId,
    accepted: false,
    errorCode: "provider_rejected",
    message: "Authorization: Bearer secret-value",
  });
  const receipt = await host.execute(
    command("speech.prompt", "prompt-secret", {
      actorId: "role.a",
      payload: { message: "private prompt" },
    }),
  );
  assert.equal(receipt.errorCode, "provider_rejected");
  assert.equal(receipt.message, "The role runtime rejected the command");
  assert.ok(!JSON.stringify(receipt).includes("secret-value"));
  assert.deepEqual(observed.slice(0, 3), [
    "runtime.lease_acquired",
    "role.registered",
    "meeting.opened",
  ]);
  await host.stop();
});

test("rejects stale generation and expected-sequence mismatches", async () => {
  const { host, adapters } = createHost();
  host.start();
  const stale = await host.execute(
    command("role.add", "stale", {
      runtimeGeneration: 2,
      actorId: "role.stale",
    }),
  );
  assert.equal(stale.errorCode, "runtime_generation_mismatch");
  const mismatched = await host.execute(
    command("role.add", "sequence", {
      expectedSequence: 99,
      actorId: "role.sequence",
    }),
  );
  assert.equal(mismatched.errorCode, "sequence_mismatch");
  assert.equal(adapters.size, 0);
  await host.stop();
});

test("stdio host frames ready, errors, events, and shutdown", async () => {
  const { host } = createHost();
  const input = new PassThrough();
  const output = new PassThrough();
  let text = "";
  output.setEncoding("utf8");
  output.on("data", (chunk: string) => {
    text += chunk;
  });
  input.end(
    `${JSON.stringify({
      type: "initialize",
      requestId: "init-1",
      workspace: TEST_WORKSPACE,
      session: TEST_SESSION,
      credentials: { "memory://provider.test": "memory-only" },
      initialSequence: 0,
    })}\n{bad json}\n{"type":"shutdown","requestId":"stop-1","mode":"suspend"}\n`,
  );

  await new StdioRuntimeHost(host).run(input, output);
  const frames = text
    .trim()
    .split("\n")
    .map((line) => JSON.parse(line) as { type: string; errorCode?: string });
  assert.deepEqual(
    frames.map((frame) => frame.type),
    ["ready", "event", "error", "event", "stopped"],
  );
  assert.equal(frames[2]?.errorCode, "invalid_json");
});

test("routes a private tool approval decision to the owning role", async () => {
  const { host, adapters } = createHost();
  const events: Array<{
    kind: string;
    visibility: string;
    audience?: string[];
    targetId?: string | null;
  }> = [];
  host.subscribe((event) => events.push(event));
  host.start();
  assert.equal((await host.execute(command("role.add", "add-approval-role", {
    actorId: "role.approval",
    payload: { displayName: "Approval role" },
  }))).status, "accepted");
  assert.equal((await host.execute(command("meeting.open", "open-approval"))).status, "accepted");
  const adapter = adapters.get("role.approval");
  assert.ok(adapter);
  adapter.emit("tool.approval_requested", {
    approvalId: "approval-1",
    toolName: "write_note",
    toolLabel: "Write note",
  });
  const approvalEvent = events.at(-1);
  assert.equal(approvalEvent?.kind, "tool.approval_requested");
  assert.equal(approvalEvent?.visibility, "private");
  assert.deepEqual(approvalEvent?.audience, ["user.direct_host"]);
  assert.equal(approvalEvent?.targetId, "user.direct_host");

  const receipt = await host.execute(command("tool.approval.resolve", "approve-tool", {
    actorId: "user.direct_host",
    targetId: "role.approval",
    payload: { approvalId: "approval-1", approved: true },
  }));
  assert.equal(receipt.status, "accepted");
  assert.deepEqual(adapter.commands.at(-1), {
    kind: "tool.approval.resolve",
    commandId: "approve-tool",
    roleId: "role.approval",
    approvalId: "approval-1",
    approved: true,
  });
  await host.stop();
});

test("runs at most two isolated SubAgents and returns results only to the parent role", async () => {
  const runner = new ControlledSubagentRunner();
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const events: import("@pi-roundtable/protocol").MeetingEvent[] = [];
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    subagentRunner: runner,
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  const session = structuredClone(RESUME_SESSION);
  session.phase = "draft";
  host.initializeRuntimeConfiguration(RESUME_WORKSPACE, session, {
    "memory://provider.test": "runtime-secret",
  });
  host.subscribe((event) => events.push(event));

  try {
    host.start();
    assert.equal((await host.execute(command("role.add", "add-subagent-parent", {
      actorId: "participant.secretary",
    }))).status, "accepted");
    assert.equal((await host.execute(command("meeting.open", "open-subagents"))).status, "accepted");

    for (const [commandId, task] of [
      ["spawn-1", "private task one"],
      ["spawn-2", "private task two"],
    ] as const) {
      assert.equal((await host.execute(command("subagent.spawn", commandId, {
        actorId: "participant.secretary",
        payload: { task },
      }))).status, "accepted");
    }
    await waitFor(() => runner.requests.length === 2, "both SubAgents should start");
    const limited = await host.execute(command("subagent.spawn", "spawn-3-limited", {
      actorId: "participant.secretary",
      payload: { task: "must wait for a free slot" },
    }));
    assert.equal(limited.status, "rejected");
    assert.equal(limited.errorCode, "subagent_limit");

    const spawned = events.filter((event) => event.kind === "subagent.spawned");
    assert.equal(spawned.length, 2);
    assert.equal(spawned.every((event) => event.visibility === "private"), true);
    assert.equal(spawned.every((event) => event.targetId === "participant.secretary"), true);
    assert.equal(spawned.every((event) =>
      JSON.stringify(event).includes("private task") === false), true);

    runner.pending[0]!.resolve("secret-child-result");
    await waitFor(
      () => events.some((event) => event.kind === "subagent.completed"),
      "a completed SubAgent event should be emitted",
    );
    await waitFor(
      () => adapters.get("participant.secretary")?.commands.some((runtimeCommand) =>
        runtimeCommand.kind === "turn.prompt" && runtimeCommand.message.includes("secret-child-result")) === true,
      "the full result should be delivered privately to its parent adapter",
    );
    const completed = events.find((event) => event.kind === "subagent.completed");
    assert.ok(completed !== undefined);
    assert.equal(JSON.stringify(completed).includes("secret-child-result"), false);
    assert.deepEqual(completed.audience, ["participant.secretary"]);

    const afterCompletion = await host.execute(command("subagent.spawn", "spawn-3", {
      actorId: "participant.secretary",
      payload: { task: "replacement task" },
    }));
    assert.equal(afterCompletion.status, "accepted");
    await waitFor(() => runner.requests.length === 3, "a completed run should release its slot");
  } finally {
    runner.pending.slice(1).forEach((pending) => pending.reject(new Error("test cleanup")));
    await host.stop();
  }
});

test("retries a failed SubAgent continuation after the parent Pi dispatch settles", async () => {
  const runner = new ControlledSubagentRunner();
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const diagnostics: Array<{ errorCode: string; message: string }> = [];
  const events: MeetingEvent[] = [];
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    subagentRunner: runner,
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  const session = structuredClone(RESUME_SESSION);
  session.phase = "draft";
  host.initializeRuntimeConfiguration(RESUME_WORKSPACE, session, {
    "memory://provider.test": "runtime-secret",
  });
  host.subscribe((event) => events.push(event));
  host.subscribeDiagnostics((errorCode, message) => diagnostics.push({ errorCode, message }));

  try {
    host.start();
    assert.equal((await host.execute(command("role.add", "add-retry-parent", {
      actorId: "participant.secretary",
    }))).status, "accepted");
    assert.equal((await host.execute(command("meeting.open", "open-retry"))).status, "accepted");
    const parent = adapters.get("participant.secretary");
    assert.ok(parent !== undefined);
    let continuationAttempts = 0;
    parent.onExecute = (runtimeCommand) => {
      if (
        runtimeCommand.kind === "turn.prompt" &&
        runtimeCommand.commandId.startsWith("subagent-result:")
      ) {
        continuationAttempts += 1;
        if (continuationAttempts === 1) {
          return {
            commandId: runtimeCommand.commandId,
            accepted: false,
            errorCode: "runtime_busy",
            message: "Parent prompt promise is still settling",
          };
        }
      }
      return undefined;
    };

    assert.equal((await host.execute(command("speech.prompt", "parent-active-turn", {
      actorId: "participant.secretary",
      payload: { message: "parent remains active while the SubAgent finishes" },
    }))).status, "accepted");
    parent.emit("turn.started", {}, "parent-active-turn");

    assert.equal((await host.execute(command("subagent.spawn", "spawn-retry", {
      actorId: "participant.secretary",
      payload: { task: "task that fails before the parent dispatch settles" },
    }))).status, "accepted");
    await waitFor(() => runner.pending.length === 1, "the SubAgent should start");
    runner.pending[0]!.reject(new Error("controlled SubAgent failure"));

    await waitFor(
      () => events.some((event) => event.kind === "subagent.failed"),
      "the SubAgent failure should be emitted",
    );
    assert.equal(continuationAttempts, 0);
    parent.emit("turn.completed", {}, "parent-active-turn");
    await waitForTimed(
      () => continuationAttempts === 2,
      "the parent continuation should retry after runtime_busy",
    );

    const continuationCommands = parent.commands.filter((runtimeCommand) =>
      runtimeCommand.kind === "turn.prompt" &&
      runtimeCommand.commandId.startsWith("subagent-result:"));
    assert.equal(continuationCommands.length, 2);
    assert.notEqual(continuationCommands[0]?.commandId, continuationCommands[1]?.commandId);
    assert.match(
      continuationCommands[1]?.kind === "turn.prompt" ? continuationCommands[1].message : "",
      /delegated task failed/i,
    );
    assert.deepEqual(diagnostics, []);

    const acceptedCommandId = continuationCommands[1]!.commandId;
    parent.emit("turn.started", {}, acceptedCommandId);
    parent.emit("turn.completed", {}, acceptedCommandId);
    await new Promise((resolve) => setTimeout(resolve, 75));
    assert.equal(continuationAttempts, 2);
    assert.equal(events.filter((event) => event.kind === "speech.completed").length, 2);
  } finally {
    await host.stop();
  }
});

test("cancels a pending SubAgent continuation retry when the host stops", async () => {
  const runner = new ControlledSubagentRunner();
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    subagentRunner: runner,
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  const session = structuredClone(RESUME_SESSION);
  session.phase = "draft";
  host.initializeRuntimeConfiguration(RESUME_WORKSPACE, session, {
    "memory://provider.test": "runtime-secret",
  });

  try {
    host.start();
    assert.equal((await host.execute(command("role.add", "add-stopping-parent", {
      actorId: "participant.secretary",
    }))).status, "accepted");
    assert.equal((await host.execute(command("meeting.open", "open-stopping-parent"))).status, "accepted");
    const parent = adapters.get("participant.secretary");
    assert.ok(parent !== undefined);
    let continuationAttempts = 0;
    parent.onExecute = (runtimeCommand) => {
      if (
        runtimeCommand.kind === "turn.prompt" &&
        runtimeCommand.commandId.startsWith("subagent-result:")
      ) {
        continuationAttempts += 1;
        return {
          commandId: runtimeCommand.commandId,
          accepted: false,
          errorCode: "runtime_busy",
          message: "Parent prompt promise is still settling",
        };
      }
      return undefined;
    };

    assert.equal((await host.execute(command("speech.prompt", "stopping-parent-turn", {
      actorId: "participant.secretary",
      payload: { message: "parent is active" },
    }))).status, "accepted");
    parent.emit("turn.started", {}, "stopping-parent-turn");
    assert.equal((await host.execute(command("subagent.spawn", "spawn-before-stop", {
      actorId: "participant.secretary",
      payload: { task: "fail before the host stops" },
    }))).status, "accepted");
    await waitFor(() => runner.pending.length === 1, "the SubAgent should start");
    runner.pending[0]!.reject(new Error("controlled SubAgent failure"));
    parent.emit("turn.completed", {}, "stopping-parent-turn");
    await waitForTimed(
      () => continuationAttempts === 1,
      "the first continuation attempt should schedule a retry",
    );

    await host.stop();
    await new Promise((resolve) => setTimeout(resolve, 75));
    assert.equal(continuationAttempts, 1);
  } finally {
    await host.stop();
  }
});

test("bounds repeated runtime_busy responses while resuming a failed SubAgent", async () => {
  const runner = new ControlledSubagentRunner();
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const diagnostics: Array<{ errorCode: string; message: string }> = [];
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    subagentRunner: runner,
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  const session = structuredClone(RESUME_SESSION);
  session.phase = "draft";
  host.initializeRuntimeConfiguration(RESUME_WORKSPACE, session, {
    "memory://provider.test": "runtime-secret",
  });
  host.subscribeDiagnostics((errorCode, message) => diagnostics.push({ errorCode, message }));

  try {
    host.start();
    assert.equal((await host.execute(command("role.add", "add-bounded-parent", {
      actorId: "participant.secretary",
    }))).status, "accepted");
    assert.equal((await host.execute(command("meeting.open", "open-bounded-parent"))).status, "accepted");
    const parent = adapters.get("participant.secretary");
    assert.ok(parent !== undefined);
    let continuationAttempts = 0;
    parent.onExecute = (runtimeCommand) => {
      if (
        runtimeCommand.kind === "turn.prompt" &&
        runtimeCommand.commandId.startsWith("subagent-result:")
      ) {
        continuationAttempts += 1;
        return {
          commandId: runtimeCommand.commandId,
          accepted: false,
          errorCode: "runtime_busy",
          message: "Parent remains busy",
        };
      }
      return undefined;
    };

    assert.equal((await host.execute(command("speech.prompt", "bounded-parent-turn", {
      actorId: "participant.secretary",
      payload: { message: "parent is active" },
    }))).status, "accepted");
    parent.emit("turn.started", {}, "bounded-parent-turn");
    assert.equal((await host.execute(command("subagent.spawn", "spawn-bounded", {
      actorId: "participant.secretary",
      payload: { task: "fail while the parent stays busy" },
    }))).status, "accepted");
    await waitFor(() => runner.pending.length === 1, "the SubAgent should start");
    runner.pending[0]!.reject(new Error("controlled SubAgent failure"));
    parent.emit("turn.completed", {}, "bounded-parent-turn");

    await waitForTimed(
      () => diagnostics.length === 1,
      "runtime_busy retries should eventually produce one diagnostic",
      2_500,
    );
    assert.equal(continuationAttempts, 41);
    assert.deepEqual(diagnostics, [{
      errorCode: "runtime_busy",
      message: "A parent role could not continue after its SubAgent failed",
    }]);
    await new Promise((resolve) => setTimeout(resolve, 75));
    assert.equal(continuationAttempts, 41);
  } finally {
    await host.stop();
  }
});

test("stdio suspend continues sequence without closing a live meeting", async () => {
  const { host } = createHost();
  const input = new PassThrough();
  const output = new PassThrough();
  let text = "";
  output.setEncoding("utf8");
  output.on("data", (chunk: string) => {
    text += chunk;
  });
  input.end(
    `${JSON.stringify({
      type: "initialize",
      requestId: "init-resume",
      workspace: RESUME_WORKSPACE,
      session: RESUME_SESSION,
      credentials: { "memory://provider.test": "memory-only" },
      initialSequence: 8,
    })}\n{"type":"shutdown","requestId":"stop-resume","mode":"suspend"}\n`,
  );

  await new StdioRuntimeHost(host).run(input, output);
  const frames = text.trim().split("\n").map((line) => JSON.parse(line) as {
    type: string;
    sequence?: number;
    event?: { kind: string; sequence: number };
  });
  assert.equal(frames[0]?.type, "ready");
  assert.equal(frames[0]?.sequence, 9);
  assert.deepEqual(
    frames.filter((frame) => frame.type === "event").map((frame) => frame.event?.kind),
    ["runtime.lease_acquired", "runtime.lease_released"],
  );
  assert.deepEqual(
    frames.filter((frame) => frame.type === "event").map((frame) => frame.event?.sequence),
    [9, 10],
  );
});

test("stdio close emits a terminal meeting event before releasing the lease", async () => {
  const { host } = createHost();
  const input = new PassThrough();
  const output = new PassThrough();
  let text = "";
  output.setEncoding("utf8");
  output.on("data", (chunk: string) => {
    text += chunk;
  });
  input.end(
    `${JSON.stringify({
      type: "initialize",
      requestId: "init-close",
      workspace: RESUME_WORKSPACE,
      session: RESUME_SESSION,
      credentials: { "memory://provider.test": "memory-only" },
      initialSequence: 2,
    })}\n{"type":"shutdown","requestId":"stop-close","mode":"close"}\n`,
  );

  await new StdioRuntimeHost(host).run(input, output);
  const eventKinds = text.trim().split("\n")
    .map((line) => JSON.parse(line) as { type: string; event?: { kind: string } })
    .filter((frame) => frame.type === "event")
    .map((frame) => frame.event?.kind);
  assert.deepEqual(eventKinds, [
    "runtime.lease_acquired",
    "meeting.closed",
    "runtime.lease_released",
  ]);
});

test("local host parser requires recovery sequence and explicit shutdown mode", () => {
  assert.throws(
    () => parseLocalHostInput(JSON.stringify({
      type: "initialize",
      requestId: "missing-sequence",
      workspace: TEST_WORKSPACE,
      session: TEST_SESSION,
      credentials: {},
    })),
    (error: unknown) => error instanceof LocalHostProtocolError && error.code === "invalid_frame",
  );
  assert.throws(
    () => parseLocalHostInput('{"type":"shutdown","requestId":"missing-mode"}'),
    (error: unknown) => error instanceof LocalHostProtocolError && error.code === "invalid_frame",
  );
});

test("local host parser carries a complete discussion snapshot across Windows recovery", () => {
  const discussionState = {
    configured: true,
    mode: "paused",
    resumeMode: "free_discussion",
    agendaItems: [{ agendaItemId: "agenda.1", title: "恢复议题", status: "completed" }],
    participantCount: 2,
    limits: {
      softTurnLimit: 8,
      hardTurnLimit: 12,
      softRoundLimit: 2,
      hardRoundLimit: 3,
      maxConsecutiveTurnsPerRole: 2,
      maxInterruptionsPerSegment: 2,
      maxInterruptionsPerRole: 1,
      noProgressTurnLimit: 2,
      maxObserverProbesPerSegment: 12,
    },
    counters: {
      publicTurns: 4,
      rounds: 2,
      noProgressTurns: 2,
      interruptions: 1,
      observerProbes: 3,
      consecutiveRoleId: "participant.risk",
      consecutiveTurns: 1,
      interruptionsByRole: { "participant.risk": 1 },
    },
    pendingRequests: [{
      requestId: "request.restore",
      roleId: "participant.risk",
      kind: "reply",
      reason: "恢复后继续回应",
      prompt: "继续说明未解决风险。",
      requestedAtSequence: 19,
      respondsToRoleId: "participant.secretary",
    }],
    pauseReason: "hard_limit",
  } as const;
  const frame = parseLocalHostInput(JSON.stringify({
    type: "initialize",
    requestId: "restore-discussion",
    workspace: RESUME_WORKSPACE,
    session: RESUME_SESSION,
    credentials: { "memory://provider.test": "memory-only" },
    initialSequence: 20,
    discussionState,
  }));

  assert.equal(frame.type, "initialize");
  assert.deepEqual(frame.type === "initialize" ? frame.discussionState : undefined, discussionState);
  assert.throws(
    () => parseLocalHostInput(JSON.stringify({
      type: "initialize",
      requestId: "invalid-discussion",
      workspace: RESUME_WORKSPACE,
      session: RESUME_SESSION,
      credentials: {},
      initialSequence: 20,
      discussionState: [],
    })),
    (error: unknown) => error instanceof LocalHostProtocolError && error.code === "invalid_frame",
  );
});

test("local host parser rejects oversized frames", () => {
  assert.throws(
    () => parseLocalHostInput("x".repeat(MAX_LOCAL_HOST_LINE_BYTES + 1)),
    (error: unknown) =>
      error instanceof LocalHostProtocolError && error.code === "frame_too_large",
  );
});

test("local host parser validates untrusted commands against protocol v1", () => {
  assert.throws(
    () => parseLocalHostInput(JSON.stringify({
      type: "command",
      command: {
        ...command("meeting.open", "invalid-generation"),
        runtimeGeneration: 0,
        unexpected: true,
      },
    })),
    (error: unknown) =>
      error instanceof LocalHostProtocolError && error.code === "invalid_command",
  );
});
