import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  PROTOCOL_VERSION,
  type MeetingCommand,
  type MeetingEvent,
  type RoundtableSession,
  type WorkspaceProfile,
} from "@pi-roundtable/protocol";

import { LocalRoundtableHost } from "../local-roundtable-host.js";

const TEST_CREDENTIAL_REF = "memory://provider.controlled";
const TEST_MODEL_PROFILE_ID = "model.controlled";
const TEST_PROVIDER_PROFILE_ID = "provider.controlled";
const TEST_MODEL_ID = "controlled-model";

type Scenario =
  | "COMPLETE_A"
  | "FAIL_ONCE_B"
  | "SLOW_CANCEL"
  | "SLOW_INTERRUPT"
  | "HANDOFF_COMPLETE"
  | "SLOW_TIMEOUT";

interface ObservedRequest {
  scenario: Scenario;
  aborted: boolean;
  completed: boolean;
}

class ControlledProvider {
  readonly requests: ObservedRequest[] = [];
  readonly #attempts = new Map<Scenario, number>();
  readonly #server = createServer((request, response) => {
    void this.#handle(request, response);
  });

  async start(): Promise<string> {
    await new Promise<void>((resolve, reject) => {
      this.#server.once("error", reject);
      this.#server.listen(0, "127.0.0.1", () => resolve());
    });
    const address = this.#server.address();
    assert.ok(address !== null && typeof address !== "string");
    return `http://127.0.0.1:${address.port}/v1`;
  }

  async stop(): Promise<void> {
    this.#server.closeAllConnections();
    await new Promise<void>((resolve, reject) => {
      this.#server.close((error) => error === undefined ? resolve() : reject(error));
    });
  }

  count(scenario: Scenario): number {
    return this.requests.filter((request) => request.scenario === scenario).length;
  }

  abortedCount(scenario: Scenario): number {
    return this.requests.filter((request) => request.scenario === scenario && request.aborted).length;
  }

  async #handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
    if (request.method !== "POST" || request.url !== "/v1/chat/completions")
    {
      response.writeHead(404).end();
      return;
    }
    const body = await readBody(request);
    const scenario = readScenario(body);
    const observed: ObservedRequest = { scenario, aborted: false, completed: false };
    this.requests.push(observed);
    const attempt = (this.#attempts.get(scenario) ?? 0) + 1;
    this.#attempts.set(scenario, attempt);
    response.once("close", () => {
      if (!observed.completed)
      {
        observed.aborted = true;
      }
    });

    if (scenario === "FAIL_ONCE_B" && attempt === 1)
    {
      observed.completed = true;
      response.writeHead(400, { "content-type": "application/json" });
      response.end(JSON.stringify({ error: { message: "controlled failure", type: "invalid_request_error" } }));
      return;
    }

    startSse(response);
    writeChatChunk(response, { role: "assistant", content: "" });
    if (scenario.startsWith("SLOW_"))
    {
      writeChatChunk(response, { content: "partial" });
      return;
    }
    writeChatChunk(response, { content: `verified:${scenario}` });
    writeChatChunk(response, {}, "stop");
    response.write("data: [DONE]\n\n");
    observed.completed = true;
    response.end();
  }
}

function readBody(request: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => chunks.push(chunk));
    request.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    request.on("error", reject);
  });
}

function readScenario(body: string): Scenario {
  const match = body.match(/\[SCENARIO:(COMPLETE_A|FAIL_ONCE_B|SLOW_CANCEL|SLOW_INTERRUPT|HANDOFF_COMPLETE|SLOW_TIMEOUT)\]/);
  assert.ok(match !== null, "The controlled provider request must include a known scenario marker");
  return match[1] as Scenario;
}

function startSse(response: ServerResponse): void {
  response.writeHead(200, {
    "cache-control": "no-cache",
    "connection": "keep-alive",
    "content-type": "text/event-stream",
  });
  response.flushHeaders();
}

function writeChatChunk(
  response: ServerResponse,
  delta: { role?: "assistant"; content?: string },
  finishReason: "stop" | null = null,
): void {
  response.write(`data: ${JSON.stringify({
    id: "chatcmpl-controlled",
    object: "chat.completion.chunk",
    created: 1_786_000_000,
    model: TEST_MODEL_ID,
    choices: [{ index: 0, delta, finish_reason: finishReason }],
  })}\n\n`);
}

function command(
  meetingId: string,
  kind: MeetingCommand["kind"],
  commandId: string,
  overrides: Partial<MeetingCommand> = {},
): MeetingCommand {
  return {
    protocolVersion: PROTOCOL_VERSION,
    meetingId,
    commandId,
    kind,
    issuedAt: "2026-08-02T00:00:00.000Z",
    runtimeGeneration: 1,
    payload: {},
    ...overrides,
  };
}

function createConfiguration(meetingId: string, endpoint: string): {
  workspace: WorkspaceProfile;
  session: RoundtableSession;
} {
  const timestamp = "2026-08-02T00:00:00.000Z";
  const modelRoute = {
    primaryModelProfileId: TEST_MODEL_PROFILE_ID,
    fallbackModelProfileIds: [],
    thinkingLevel: "off" as const,
    maxOutputTokens: 128,
  };
  const capabilities = { skillIds: [], mcpGrants: [], toolGrants: [] };
  const delegation = {
    networkAccess: "direct_allowed" as const,
    resultMode: "summary" as const,
    maxConcurrentSubagents: 0,
  };
  const memory = {
    mode: "disabled" as const,
    writeApproval: "always" as const,
    promptEvolution: "disabled" as const,
  };
  const role = (roleProfileId: string, displayName: string) => ({
    roleProfileId,
    displayName,
    description: `${displayName} controlled provider role`,
    systemPrompt: `You are ${displayName}. Echo the controlled provider output without tools.`,
    responsibilities: ["Complete only the addressed controlled scenario"],
    autoJoin: true,
    modelRoute,
    capabilities,
    delegation,
    memory,
  });
  const roles = [role("role.a", "Role A"), role("role.b", "Role B")];
  const workspace: WorkspaceProfile = {
    configurationVersion: 1,
    workspaceId: "workspace.controlled",
    displayName: "Controlled provider workspace",
    updatedAt: timestamp,
    providers: [{
      providerProfileId: TEST_PROVIDER_PROFILE_ID,
      displayName: "Controlled provider",
      apiFamily: "openai_chat_completions",
      runtimeProviderId: "controlled-provider",
      endpoint,
      credentialRef: TEST_CREDENTIAL_REF,
      enabled: true,
    }],
    models: [{
      modelProfileId: TEST_MODEL_PROFILE_ID,
      providerProfileId: TEST_PROVIDER_PROFILE_ID,
      modelId: TEST_MODEL_ID,
      displayName: "Controlled model",
      capabilities: ["text"],
      contextWindow: 8_192,
      enabled: true,
    }],
    skills: [],
    mcpServers: [],
    roles,
  };
  const session: RoundtableSession = {
    sessionVersion: 1,
    sessionId: meetingId,
    workspaceId: workspace.workspaceId,
    title: "Controlled provider E2E",
    phase: "draft",
    createdAt: timestamp,
    updatedAt: timestamp,
    agenda: {
      subject: "Exercise provider failures through the real Pi adapter",
      objectives: ["Verify normalized terminal events"],
      constraints: ["No external network", "No tools"],
    },
    participants: roles.map((entry) => ({
      participantId: entry.roleProfileId,
      scope: "long_term" as const,
      roleProfileId: entry.roleProfileId,
      displayName: entry.displayName,
      systemPromptSnapshot: entry.systemPrompt,
      modelRouteSnapshot: modelRoute,
      capabilitiesSnapshot: capabilities,
      delegationSnapshot: delegation,
      memoryPolicySnapshot: memory,
      retentionPolicy: "retain_profile",
    })),
  };
  return { workspace, session };
}

async function createStartedHost(
  endpoint: string,
  suffix: string,
  turnTimeoutMs = 5_000,
): Promise<{
  host: LocalRoundtableHost;
  events: MeetingEvent[];
  cleanup: () => Promise<void>;
}> {
  const meetingId = `meeting.controlled.${suffix}`;
  const cwd = mkdtempSync(join(tmpdir(), "pi-roundtable-provider-e2e-"));
  const projectConfigurationDirectory = join(cwd, ".pi");
  mkdirSync(projectConfigurationDirectory);
  writeFileSync(
    join(projectConfigurationDirectory, "settings.json"),
    JSON.stringify({ retry: { enabled: false, provider: { maxRetries: 0 } } }),
    "utf8",
  );
  const { workspace, session } = createConfiguration(meetingId, endpoint);
  const host = new LocalRoundtableHost({
    meetingId,
    runtimeGeneration: 1,
    cwd,
    turnTimeoutMs,
  });
  const events: MeetingEvent[] = [];
  host.subscribe((event) => events.push(event));
  host.initializeRuntimeConfiguration(workspace, session, {
    [TEST_CREDENTIAL_REF]: "controlled-test-key",
  });
  host.start();
  for (const roleId of ["role.a", "role.b"])
  {
    const receipt = await host.execute(command(meetingId, "role.add", `add-${roleId}`, {
      actorId: roleId,
    }));
    assert.equal(receipt.status, "accepted");
  }
  assert.equal(
    (await host.execute(command(meetingId, "meeting.open", "open"))).status,
    "accepted",
  );
  return {
    host,
    events,
    cleanup: async () => {
      await host.stop();
      rmSync(cwd, { recursive: true, force: true });
    },
  };
}

async function waitFor(
  predicate: () => boolean,
  description: string,
  timeoutMs = 8_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline)
  {
    if (predicate())
    {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`Timed out waiting for ${description}`);
}

test("real Pi adapter handles controlled provider cancellation, retry, timeout, and handoff", {
  timeout: 60_000,
}, async (context) => {
  const provider = new ControlledProvider();
  const endpoint = await provider.start();
  try
  {
    await context.test("cancels a live provider stream exactly once", async () => {
      const runtime = await createStartedHost(endpoint, "cancel");
      try
      {
        const prompt = await runtime.host.execute(command(runtime.host.meetingId, "speech.prompt", "prompt-cancel", {
          actorId: "role.a",
          payload: { message: "[SCENARIO:SLOW_CANCEL] stream until cancelled" },
        }));
        assert.equal(prompt.status, "accepted");
        await waitFor(() => provider.count("SLOW_CANCEL") === 1, "slow cancellation request");
        const cancellation = await runtime.host.execute(command(
          runtime.host.meetingId,
          "generation.cancel",
          "cancel-a",
          { targetId: "role.a" },
        ));
        assert.equal(cancellation.status, "accepted");
        await waitFor(
          () => runtime.events.some((event) =>
            event.kind === "speech.cancelled" && event.causationId === "prompt-cancel"),
          "normalized cancellation event",
        );
        await waitFor(() => provider.abortedCount("SLOW_CANCEL") === 1, "provider abort");
        assert.equal(provider.count("SLOW_CANCEL"), 1);
        assert.equal(runtime.events.some((event) =>
          event.kind === "speech.completed" && event.causationId === "prompt-cancel"), false);
      }
      finally
      {
        await runtime.cleanup();
      }
    });

    await context.test("retries only the failed role after a provider error", async () => {
      const runtime = await createStartedHost(endpoint, "retry");
      try
      {
        assert.equal((await runtime.host.execute(command(runtime.host.meetingId, "speech.prompt", "prompt-a", {
          actorId: "role.a",
          payload: { message: "[SCENARIO:COMPLETE_A] finish successfully" },
        }))).status, "accepted");
        await waitFor(() => runtime.events.some((event) =>
          event.kind === "speech.completed" && event.causationId === "prompt-a"), "role A completion");

        assert.equal((await runtime.host.execute(command(runtime.host.meetingId, "speech.prompt", "prompt-b-failed", {
          actorId: "role.b",
          payload: { message: "[SCENARIO:FAIL_ONCE_B] fail first" },
        }))).status, "accepted");
        await waitFor(() => runtime.events.some((event) =>
          (event.kind === "speech.cancelled" || event.kind === "speech.completed") &&
          event.causationId === "prompt-b-failed"), "role B terminal");
        const failedTerminal = runtime.events.find((event) =>
          (event.kind === "speech.cancelled" || event.kind === "speech.completed") &&
          event.causationId === "prompt-b-failed");
        assert.equal(
          failedTerminal?.kind,
          "speech.cancelled",
          `Expected a manual-retry failure terminal; requests=${provider.count("FAIL_ONCE_B")}`,
        );

        assert.equal((await runtime.host.execute(command(runtime.host.meetingId, "speech.prompt", "prompt-b-retry", {
          actorId: "role.b",
          payload: { message: "[SCENARIO:FAIL_ONCE_B] retry only B" },
        }))).status, "accepted");
        await waitFor(() => runtime.events.some((event) =>
          event.kind === "speech.completed" && event.causationId === "prompt-b-retry"), "role B retry");

        assert.equal(provider.count("COMPLETE_A"), 1);
        assert.equal(provider.count("FAIL_ONCE_B"), 2);
      }
      finally
      {
        await runtime.cleanup();
      }
    });

    await context.test("times out a stalled provider stream with one retryable terminal", async () => {
      const runtime = await createStartedHost(endpoint, "timeout", 150);
      try
      {
        assert.equal((await runtime.host.execute(command(runtime.host.meetingId, "speech.prompt", "prompt-timeout", {
          actorId: "role.a",
          payload: { message: "[SCENARIO:SLOW_TIMEOUT] wait for host timeout" },
        }))).status, "accepted");
        await waitFor(() => runtime.events.some((event) =>
          event.kind === "speech.cancelled" &&
          event.causationId === "prompt-timeout" &&
          event.payload.errorCode === "turn_timeout"), "timeout terminal");
        await waitFor(() => provider.abortedCount("SLOW_TIMEOUT") === 1, "timed out provider abort");
        assert.equal(runtime.events.filter((event) =>
          event.kind === "speech.cancelled" && event.causationId === "prompt-timeout").length, 1);
        assert.equal(provider.count("SLOW_TIMEOUT"), 1);
      }
      finally
      {
        await runtime.cleanup();
      }
    });

    await context.test("interrupts role A before handing the public floor to role B", async () => {
      const runtime = await createStartedHost(endpoint, "handoff");
      try
      {
        assert.equal((await runtime.host.execute(command(runtime.host.meetingId, "speech.prompt", "prompt-interrupted", {
          actorId: "role.a",
          payload: { message: "[SCENARIO:SLOW_INTERRUPT] keep speaking" },
        }))).status, "accepted");
        await waitFor(() => provider.count("SLOW_INTERRUPT") === 1, "interruptible provider stream");

        assert.equal((await runtime.host.execute(command(runtime.host.meetingId, "speech.interrupt", "interrupt-b", {
          actorId: "role.b",
          targetId: "role.a",
          payload: { message: "[SCENARIO:HANDOFF_COMPLETE] take the floor" },
        }))).status, "accepted");
        await waitFor(() => runtime.events.some((event) =>
          event.kind === "speech.completed" && event.causationId === "interrupt-b"), "handoff completion");
        await waitFor(() => provider.abortedCount("SLOW_INTERRUPT") === 1, "interrupted provider abort");

        const orderedKinds = runtime.events
          .filter((event) =>
            event.causationId === "interrupt-b" || event.causationId === "prompt-interrupted")
          .map((event) => event.kind);
        assert.ok(orderedKinds.indexOf("interruption.requested") < orderedKinds.indexOf("speech.cancelled"));
        assert.ok(orderedKinds.indexOf("speech.cancelled") < orderedKinds.lastIndexOf("speech.started"));
        assert.equal(provider.count("SLOW_INTERRUPT"), 1);
        assert.equal(provider.count("HANDOFF_COMPLETE"), 1);
      }
      finally
      {
        await runtime.cleanup();
      }
    });
  }
  finally
  {
    await provider.stop();
  }
});
