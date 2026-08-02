import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createInterface } from "node:readline";
import { resolve } from "node:path";

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || index + 1 >= process.argv.length) {
    throw new Error(`Missing required argument ${name}`);
  }
  return process.argv[index + 1];
}

async function main() {
  const appDirectory = resolve(argument("--app-dir"));
  const workspace = JSON.parse(await readFile(resolve(argument("--workspace-file")), "utf8"));
  const session = JSON.parse(await readFile(resolve(argument("--session-file")), "utf8"));
  let apiKey = (await readFile(resolve(argument("--key-file")), "utf8")).trim();
  if (apiKey.length < 8 || /\s/u.test(apiKey)) {
    throw new Error("The key file does not contain one valid credential");
  }
  session.phase = "draft";
  session.messages = [];
  const credentialRefs = [...new Set(workspace.providers.map((provider) => provider.credentialRef))];
  const credentials = Object.fromEntries(credentialRefs.map((reference) => [reference, apiKey]));
  const child = spawn(
    resolve(appDirectory, "runtime", "node.exe"),
    [resolve(appDirectory, "runtime-host", "host-main.js")],
    {
      cwd: appDirectory,
      env: {
        ...process.env,
        PI_ROUNDTABLE_MEETING_ID: session.sessionId,
        PI_ROUNDTABLE_RUNTIME_ID: "runtime.manifest-probe",
        PI_ROUNDTABLE_RUNTIME_GENERATION: "1",
      },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    },
  );
  const frames = [];
  const waiters = new Set();
  let exited = false;
  let stderrObserved = false;
  child.stderr.on("data", () => { stderrObserved = true; });
  const lines = createInterface({ input: child.stdout, crlfDelay: Infinity });
  lines.on("line", (line) => {
    frames.push(JSON.parse(line));
    for (const wake of waiters) wake();
  });
  child.on("exit", () => {
    exited = true;
    for (const wake of waiters) wake();
  });
  const write = (frame) => child.stdin.write(`${JSON.stringify(frame)}\n`);
  const waitFor = async (predicate, label) => {
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
      const match = frames.find(predicate);
      if (match !== undefined) return match;
      if (exited) throw new Error(`Runtime exited before ${label}`);
      await new Promise((resolveWait) => {
        const timer = setTimeout(resolveWait, 250);
        waiters.add(() => { clearTimeout(timer); resolveWait(); });
      });
    }
    throw new Error(`Timed out waiting for ${label}`);
  };
  const command = (kind, commandId, actorId) => ({
    protocolVersion: 1,
    meetingId: session.sessionId,
    commandId,
    kind,
    issuedAt: new Date().toISOString(),
    runtimeGeneration: 1,
    ...(actorId === undefined ? {} : { actorId }),
    payload: {},
  });
  try {
    write({
      type: "initialize",
      requestId: "manifest-probe-initialize",
      workspace,
      session,
      credentials,
      initialSequence: 0,
    });
    await waitFor((frame) => frame.type === "ready", "runtime readiness");
    for (const participant of session.participants) {
      const roleCommand = command("role.add", `manifest-probe-${participant.participantId}`, participant.participantId);
      write({ type: "command", command: roleCommand });
      const receipt = await waitFor(
        (frame) => frame.type === "receipt" && frame.receipt?.commandId === roleCommand.commandId,
        `role receipt ${participant.participantId}`,
      );
      console.log(`${participant.participantId}: ${receipt.receipt.status}/${receipt.receipt.errorCode ?? "none"}`);
      if (receipt.receipt.status !== "accepted") break;
    }
    const open = command("meeting.open", "manifest-probe-open");
    write({ type: "command", command: open });
    await waitFor(
      (frame) => frame.type === "receipt" && frame.receipt?.commandId === open.commandId,
      "meeting receipt",
    );
    write({ type: "shutdown", requestId: "manifest-probe-shutdown", mode: "suspend" });
    await waitFor((frame) => frame.type === "stopped", "shutdown");
    console.log(`Events: ${frames.filter((frame) => frame.type === "event").map((frame) => `${frame.event.sequence}@g${frame.event.runtimeGeneration}:${frame.event.kind}`).join(",")}`);
    console.log(`Runtime stderr observed: ${stderrObserved}`);
  } finally {
    apiKey = "";
    lines.close();
    if (!exited) child.kill();
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : "Manifest probe failed");
  process.exitCode = 1;
});
