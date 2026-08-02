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

test("broadcasts public host messages sequentially with a role-exclusive prompt and isolates direct replies", async () => {
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

  const broadcast = await host.execute(command("speech.broadcast", "broadcast", {
    actorId: "user.direct_host",
    payload: { message: "Review the proposal", mentions: ["role.a", "role.b", "role.c"] },
  }));
  assert.equal(broadcast.status, "accepted");
  assert.equal(events.at(-1)?.kind, "message.published");
  assert.equal(events.at(-1)?.visibility, "public");
  const firstPrompt = adapters.get("role.a")?.commands.at(-1);
  assert.equal(firstPrompt?.kind, "turn.prompt");
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /Review the proposal/);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /Architect \(role\.a\)/);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /only role answering this turn/i);
  assert.match(firstPrompt?.kind === "turn.prompt" ? firstPrompt.message : "", /Do not draft, simulate, summarize as/);

  adapters.get("role.a")?.emit("turn.started", {}, "broadcast:1");
  adapters.get("role.a")?.emit("turn.completed", {}, "broadcast:1");
  await new Promise<void>((resolve) => setImmediate(resolve));
  const secondPrompt = adapters.get("role.b")?.commands.at(-1);
  assert.equal(secondPrompt?.kind, "turn.prompt");
  assert.match(secondPrompt?.kind === "turn.prompt" ? secondPrompt.message : "", /Review the proposal/);
  assert.match(secondPrompt?.kind === "turn.prompt" ? secondPrompt.message : "", /Experience reviewer \(role\.b\)/);
  adapters.get("role.b")?.emit("turn.started", {}, "broadcast:2");
  adapters.get("role.b")?.emit("turn.completed", {}, "broadcast:2");
  await new Promise<void>((resolve) => setImmediate(resolve));
  const thirdPrompt = adapters.get("role.c")?.commands.at(-1);
  assert.equal(thirdPrompt?.kind, "turn.prompt");
  assert.match(thirdPrompt?.kind === "turn.prompt" ? thirdPrompt.message : "", /Review the proposal/);
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

test("local host parser rejects oversized frames", () => {
  assert.throws(
    () => parseLocalHostInput("x".repeat(MAX_LOCAL_HOST_LINE_BYTES + 1)),
    (error: unknown) =>
      error instanceof LocalHostProtocolError && error.code === "frame_too_large",
  );
});
