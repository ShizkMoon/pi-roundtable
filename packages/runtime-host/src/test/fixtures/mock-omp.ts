import { createInterface } from "node:readline";

function write(value: Record<string, unknown>): void {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

write({
  type: "ready",
  protocolVersion: 1,
  supportedProtocolVersions: [1, 2],
  maxFrameBytes: 1_048_576,
  maxReassembledFrameBytes: 67_108_864,
});

if (process.argv.includes("--exit-after-ready")) {
  setTimeout(() => process.exit(0), 50).unref();
}

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  const command = JSON.parse(line) as Record<string, unknown>;
  if (command.type === "negotiate_protocol") {
    if (process.argv.includes("--reject-negotiate")) {
      write({
        id: command.id,
        type: "response",
        command: "negotiate_protocol",
        success: false,
        error: "mock rejected protocol negotiation",
      });
      return;
    }
    write({
      id: command.id,
      type: "response",
      command: "negotiate_protocol",
      success: true,
      data: { protocolVersion: 2 },
    });
    return;
  }
  if (command.type === "prompt") {
    write({ type: "agent_start" });
    write({
      id: command.id,
      type: "response",
      command: "prompt",
      success: true,
      data: { agentInvoked: true },
    });
    write({ type: "agent_end" });
    return;
  }
  write({
    id: command.id,
    type: "response",
    command: String(command.type),
    success: true,
  });
});
