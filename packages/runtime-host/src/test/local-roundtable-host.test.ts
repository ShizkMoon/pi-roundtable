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

function createHost(): {
  host: LocalRoundtableHost;
  adapters: Map<string, FakeRuntimeAdapter>;
} {
  const adapters = new Map<string, FakeRuntimeAdapter>();
  const host = new LocalRoundtableHost({
    meetingId: "meeting-local-test",
    runtimeId: "runtime.windows",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-01T00:00:00.000Z"),
    adapterFactory: (roleId) => {
      const adapter = new FakeRuntimeAdapter(roleId);
      adapters.set(roleId, adapter);
      return adapter;
    },
  });
  return { host, adapters };
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
    assert.equal(resolved?.modelId, "test-model");
    assert.equal(resolved?.apiKey, "runtime-secret");
    assert.equal(resolved?.systemPrompt, "Keep the meeting on track.");
    assert.equal(resolved?.skillPaths.length, 1);
    assert.equal(resolved?.skillPaths[0]?.endsWith("skills\\test\\SKILL.md") || resolved?.skillPaths[0]?.endsWith("skills/test/SKILL.md"), true);
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

test("hands the floor to an interrupting role after cancellation", async () => {
  const { host, adapters } = createHost();
  const events: string[] = [];
  host.subscribe((event) => events.push(event.kind));
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
  assert.deepEqual(events.slice(-2), ["interruption.requested", "speech.cancelled"]);
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
    })}\n{bad json}\n{"type":"shutdown","requestId":"stop-1"}\n`,
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

test("local host parser rejects oversized frames", () => {
  assert.throws(
    () => parseLocalHostInput("x".repeat(MAX_LOCAL_HOST_LINE_BYTES + 1)),
    (error: unknown) =>
      error instanceof LocalHostProtocolError && error.code === "frame_too_large",
  );
});
