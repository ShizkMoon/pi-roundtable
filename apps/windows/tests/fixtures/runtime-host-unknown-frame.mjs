import { createInterface } from "node:readline";

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  const frame = JSON.parse(line);
  if (frame.type !== "initialize") return;
  process.stdout.write(`${JSON.stringify({
    type: "ready",
    protocolVersion: 3,
    meetingId: process.env.PI_ROUNDTABLE_MEETING_ID,
    runtimeId: process.env.PI_ROUNDTABLE_RUNTIME_ID,
    runtimeGeneration: Number(process.env.PI_ROUNDTABLE_RUNTIME_GENERATION),
    sequence: Number(frame.initialSequence ?? 0) + 1,
  })}\n`);
  setImmediate(() => process.stdout.write(`${JSON.stringify({ type: "event_v2" })}\n`));
});
