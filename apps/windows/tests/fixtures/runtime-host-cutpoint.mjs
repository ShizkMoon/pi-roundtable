import { appendFileSync } from "node:fs";
import { createInterface } from "node:readline";

const meetingId = process.env.PI_ROUNDTABLE_MEETING_ID ?? "meeting-cutpoint";
const runtimeId = process.env.PI_ROUNDTABLE_RUNTIME_ID ?? "runtime-cutpoint";
const runtimeGeneration = Number(process.env.PI_ROUNDTABLE_RUNTIME_GENERATION ?? "1");
const sideEffectFile = process.env.PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE;
const eventKind = process.env.PI_ROUNDTABLE_TEST_EVENT_KIND ?? "message.published";
let sequence = 0;

function write(frame) {
  process.stdout.write(`${JSON.stringify(frame)}\n`);
}

function event(kind, causationId, payload) {
  sequence += 1;
  return {
    protocolVersion: 1,
    meetingId,
    eventId: `cutpoint-${runtimeGeneration}-${sequence}-${kind.replaceAll(".", "-")}`,
    sequence,
    runtimeGeneration,
    kind,
    occurredAt: new Date().toISOString(),
    actorId: runtimeId,
    causationId,
    visibility: "public",
    audience: [],
    payload,
  };
}

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  const frame = JSON.parse(line);
  if (frame.type === "initialize") {
    sequence = Number(frame.initialSequence ?? 0);
    const lease = event("runtime.lease_acquired", frame.requestId, { runtimeId });
    write({ type: "event", event: lease });
    write({
      type: "ready",
      protocolVersion: 3,
      meetingId,
      runtimeId,
      runtimeGeneration,
      sequence,
    });
    return;
  }
  if (frame.type === "command") {
    const commandId = String(frame.command.commandId);
    if (sideEffectFile) {
      appendFileSync(sideEffectFile, `${commandId}\n`, "utf8");
    }
    const emitted = event(eventKind, commandId, {
      messageId: `message-${commandId}`,
      text: "controlled cutpoint output",
      state: eventKind === "speech.delta" ? "streaming" : "completed",
    });
    write({ type: "event", event: emitted });
    // Intentionally never write the command receipt. The Windows test kills this
    // process at a precise persistence boundary and verifies replay is fenced.
    return;
  }
  if (frame.type === "shutdown") {
    write({ type: "stopped", requestId: frame.requestId, mode: frame.mode });
    input.close();
    process.exitCode = 0;
  }
});
