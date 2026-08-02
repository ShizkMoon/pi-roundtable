import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { resolve } from "node:path";

const DEFAULT_ENDPOINT = "https://api.deepseek.com";
const EXPECTED_REPLY = "PI_ROUNDTABLE_SMOKE_OK";
const DEFAULT_TIMEOUT_MS = 120_000;

function readArgument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || index + 1 >= process.argv.length) {
    throw new Error(`Missing required argument ${name}`);
  }
  return process.argv[index + 1];
}

function command(kind, commandId, overrides = {}) {
  return {
    protocolVersion: 1,
    meetingId: "meeting.deepseek-smoke",
    commandId,
    kind,
    issuedAt: new Date().toISOString(),
    runtimeGeneration: 1,
    payload: {},
    ...overrides,
  };
}

async function discoverModel(apiKey, endpoint) {
  const response = await fetch(`${endpoint}/models`, {
    headers: { Authorization: `Bearer ${apiKey}` },
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) {
    throw new Error(`DeepSeek model discovery failed with HTTP ${response.status}`);
  }
  const body = await response.json();
  const modelIds = Array.isArray(body?.data)
    ? body.data
      .map((model) => typeof model?.id === "string" ? model.id.trim() : "")
      .filter((modelId) => modelId.length > 0)
    : [];
  if (modelIds.length === 0) {
    throw new Error("DeepSeek model discovery returned no model identifiers");
  }
  return ["deepseek-chat", "deepseek-v4-flash", "deepseek-reasoner"]
    .find((modelId) => modelIds.includes(modelId)) ?? modelIds[0];
}

function createRuntimeFrames(modelId, apiKey, endpoint) {
  const timestamp = new Date().toISOString();
  const credentialRef = "memory://provider.deepseek-smoke";
  const modelProfileId = "model.deepseek-smoke";
  const roleProfileId = "role.deepseek-smoke";
  const participantId = roleProfileId;
  const modelRoute = {
    primaryModelProfileId: modelProfileId,
    fallbackModelProfileIds: [],
    thinkingLevel: "off",
    maxOutputTokens: 128,
  };
  const capabilities = { skillIds: [], mcpGrants: [], toolGrants: [] };
  const delegation = {
    networkAccess: "direct_allowed",
    resultMode: "summary",
    maxConcurrentSubagents: 0,
  };
  const memory = {
    mode: "disabled",
    writeApproval: "always",
    promptEvolution: "disabled",
  };
  const workspace = {
    configurationVersion: 1,
    workspaceId: "workspace.deepseek-smoke",
    displayName: "DeepSeek smoke workspace",
    updatedAt: timestamp,
    providers: [{
      providerProfileId: "provider.deepseek-smoke",
      displayName: "DeepSeek",
      apiFamily: "openai_chat_completions",
      runtimeProviderId: "deepseek",
      endpoint,
      credentialRef,
      enabled: true,
    }],
    models: [{
      modelProfileId,
      providerProfileId: "provider.deepseek-smoke",
      modelId,
      displayName: modelId,
      capabilities: modelId.includes("reasoner") || modelId.includes("v4")
        ? ["text", "reasoning"]
        : ["text"],
      contextWindow: 128_000,
      enabled: true,
    }],
    skills: [],
    mcpServers: [],
    roles: [{
      roleProfileId,
      displayName: "DeepSeek smoke role",
      description: "A credential-safe packaged runtime smoke-test role",
      systemPrompt: `This is a connectivity test. Reply with exactly ${EXPECTED_REPLY} and nothing else.`,
      responsibilities: ["Return the exact smoke-test token"],
      autoJoin: true,
      modelRoute,
      capabilities,
      delegation,
      memory,
    }],
  };
  const session = {
    sessionVersion: 1,
    sessionId: "meeting.deepseek-smoke",
    workspaceId: workspace.workspaceId,
    title: "DeepSeek packaged runtime smoke test",
    phase: "draft",
    createdAt: timestamp,
    updatedAt: timestamp,
    agenda: {
      subject: "Verify the packaged Pi runtime can reach DeepSeek",
      objectives: ["Receive the exact smoke-test token"],
      constraints: ["No tools", "No persistent credentials"],
    },
    participants: [{
      participantId,
      scope: "long_term",
      roleProfileId,
      displayName: "DeepSeek smoke role",
      systemPromptSnapshot: workspace.roles[0].systemPrompt,
      modelRouteSnapshot: modelRoute,
      capabilitiesSnapshot: capabilities,
      delegationSnapshot: delegation,
      memoryPolicySnapshot: memory,
      retentionPolicy: "retain_profile",
    }],
  };
  return {
    initialize: {
      type: "initialize",
      requestId: "initialize.deepseek-smoke",
      workspace,
      session,
      credentials: { [credentialRef]: apiKey },
      initialSequence: 0,
    },
    commands: [
      command("role.add", "command.add-role", { actorId: participantId }),
      command("meeting.open", "command.open-meeting"),
      command("speech.prompt", "command.prompt", {
        actorId: participantId,
        payload: { message: `Reply with exactly ${EXPECTED_REPLY} and nothing else.` },
      }),
    ],
  };
}

async function main() {
  const keyFile = resolve(readArgument("--key-file"));
  const appDirectory = resolve(readArgument("--app-dir"));
  const endpoint = DEFAULT_ENDPOINT;
  let apiKey = (await readFile(keyFile, "utf8")).trim();
  if (apiKey.length < 8 || /\s/.test(apiKey)) {
    throw new Error("The DeepSeek key file does not contain one valid non-empty credential");
  }

  const modelId = await discoverModel(apiKey, endpoint);
  const nodeExecutable = resolve(appDirectory, "runtime", "node.exe");
  const hostEntry = resolve(appDirectory, "runtime-host", "host-main.js");
  const child = spawn(nodeExecutable, [hostEntry], {
    cwd: appDirectory,
    env: {
      ...process.env,
      PI_ROUNDTABLE_MEETING_ID: "meeting.deepseek-smoke",
      PI_ROUNDTABLE_RUNTIME_ID: "runtime.windows.deepseek-smoke",
      PI_ROUNDTABLE_RUNTIME_GENERATION: "1",
      PI_ROUNDTABLE_WORKING_DIRECTORY: appDirectory,
    },
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  let stderrObserved = false;
  child.stderr.on("data", () => { stderrObserved = true; });

  const frames = [];
  const waiters = new Set();
  let parseFailure;
  let childExit;
  const lines = createInterface({ input: child.stdout, crlfDelay: Infinity });
  lines.on("line", (line) => {
    try {
      const frame = JSON.parse(line);
      frames.push(frame);
      for (const wake of waiters) wake();
    } catch {
      parseFailure = new Error("Runtime host emitted a non-JSON output frame");
      for (const wake of waiters) wake();
    }
  });
  child.on("exit", (code, signal) => {
    childExit = { code, signal };
    for (const wake of waiters) wake();
  });

  const writeFrame = (frame) => {
    child.stdin.write(`${JSON.stringify(frame)}\n`);
  };
  const waitForFrame = async (predicate, description, timeoutMs = DEFAULT_TIMEOUT_MS) => {
    const existing = frames.find(predicate);
    if (existing !== undefined) return existing;
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (parseFailure !== undefined) throw parseFailure;
      const errorFrame = frames.find((frame) => frame?.type === "error");
      if (errorFrame !== undefined) {
        throw new Error(`Runtime host error: ${errorFrame.errorCode ?? "unknown"}`);
      }
      const match = frames.find(predicate);
      if (match !== undefined) return match;
      if (childExit !== undefined) {
        throw new Error(`Runtime host exited before ${description}`);
      }
      await new Promise((resolveWait) => {
        const remaining = Math.max(1, Math.min(1_000, deadline - Date.now()));
        const timer = setTimeout(() => {
          waiters.delete(wake);
          resolveWait();
        }, remaining);
        const wake = () => {
          clearTimeout(timer);
          waiters.delete(wake);
          resolveWait();
        };
        waiters.add(wake);
      });
    }
    throw new Error(`Timed out waiting for ${description}`);
  };

  try {
    const runtime = createRuntimeFrames(modelId, apiKey, endpoint);
    writeFrame(runtime.initialize);
    const ready = await waitForFrame(
      (frame) => frame?.type === "ready",
      "runtime readiness",
      30_000,
    );
    if (ready.protocolVersion !== 3 || ready.runtimeGeneration !== 1) {
      throw new Error("Runtime host reported an unexpected protocol version or generation");
    }

    for (const runtimeCommand of runtime.commands) {
      writeFrame({ type: "command", command: runtimeCommand });
      const receipt = await waitForFrame(
        (frame) => frame?.type === "receipt" && frame.receipt?.commandId === runtimeCommand.commandId,
        `receipt for ${runtimeCommand.kind}`,
      );
      if (receipt.receipt?.status !== "accepted") {
        throw new Error(`Runtime command was rejected: ${runtimeCommand.kind}/${receipt.receipt?.errorCode ?? "unknown"}`);
      }
    }

    await waitForFrame(
      (frame) => frame?.type === "event" && frame.event?.kind === "speech.completed",
      "DeepSeek speech completion",
    );
    const reply = frames
      .filter((frame) => frame?.type === "event" && frame.event?.kind === "speech.delta")
      .map((frame) => typeof frame.event?.payload?.delta === "string" ? frame.event.payload.delta : "")
      .join("");
    if (!reply.includes(EXPECTED_REPLY)) {
      throw new Error("DeepSeek completed the turn without the expected smoke-test token");
    }

    writeFrame({ type: "shutdown", requestId: "shutdown.deepseek-smoke", mode: "close" });
    await waitForFrame(
      (frame) => frame?.type === "stopped" && frame.requestId === "shutdown.deepseek-smoke",
      "runtime shutdown",
      30_000,
    );
    child.stdin.end();
    console.log(`DeepSeek model: ${modelId}`);
    console.log("Packaged Pi runtime: ready/role/open/prompt/completed/shutdown verified");
    console.log(`Expected response token observed: ${EXPECTED_REPLY}`);
    console.log(`Runtime stderr observed: ${stderrObserved}`);
  } finally {
    apiKey = "";
    lines.close();
    if (child.exitCode === null && child.signalCode === null) {
      child.kill();
    }
  }
}

main().catch((error) => {
  const message = error instanceof Error ? error.message : "Unknown DeepSeek smoke-test failure";
  console.error(message);
  process.exitCode = 1;
});
