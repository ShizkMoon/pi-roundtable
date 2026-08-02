import { appendFileSync } from "node:fs";
import { createInterface } from "node:readline";

const meetingId = process.env.PI_ROUNDTABLE_MEETING_ID ?? "meeting-recovery";
const runtimeId = process.env.PI_ROUNDTABLE_RUNTIME_ID ?? "runtime-recovery";
const runtimeGeneration = Number(process.env.PI_ROUNDTABLE_RUNTIME_GENERATION ?? "1");
const sideEffectFile = process.env.PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE;
let sequence = 0;

function write(frame) {
  process.stdout.write(`${JSON.stringify(frame)}\n`);
}

function event(kind, causationId, options = {}) {
  sequence += 1;
  return {
    protocolVersion: 1,
    meetingId,
    eventId: `recovery-${runtimeGeneration}-${sequence}-${kind.replaceAll(".", "-")}`,
    sequence,
    runtimeGeneration,
    kind,
    occurredAt: new Date().toISOString(),
    actorId: options.actorId ?? runtimeId,
    targetId: options.targetId ?? null,
    causationId,
    visibility: options.visibility ?? "public",
    audience: options.audience ?? [],
    payload: options.payload ?? {},
  };
}

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  const frame = JSON.parse(line);
  if (frame.type === "initialize") {
    sequence = Number(frame.initialSequence ?? 0);
    write({
      type: "event",
      event: event("runtime.lease_acquired", frame.requestId, {
        payload: { runtimeId },
      }),
    });
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
    const command = frame.command;
    const commandId = String(command.commandId);
    if (sideEffectFile) {
      appendFileSync(sideEffectFile, `${commandId}\n`, "utf8");
    }
    const emitted = command.kind === "speech.direct"
      ? event("message.direct_sent", commandId, {
          actorId: "user.direct_host",
          targetId: command.targetId,
          visibility: "private",
          audience: ["user.direct_host", command.targetId],
          payload: {
            messageId: `message-${commandId}`,
            message: command.payload.message,
          },
        })
      : event("message.published", commandId, {
          actorId: "user.direct_host",
          payload: {
            messageId: `message-${commandId}`,
            message: command.payload.message,
          },
        });
    write({ type: "event", event: emitted });
    write({
      type: "receipt",
      receipt: {
        commandId,
        status: "accepted",
        sequence: emitted.sequence,
      },
    });
    return;
  }
  if (frame.type === "shutdown") {
    write({
      type: "event",
      event: event("runtime.lease_released", frame.requestId, {
        payload: { runtimeId },
      }),
    });
    write({ type: "stopped", requestId: frame.requestId });
    input.close();
    process.exitCode = 0;
  }
});
