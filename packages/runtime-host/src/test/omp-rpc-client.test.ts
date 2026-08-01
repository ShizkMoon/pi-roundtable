import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import test from "node:test";

import { OmpRpcClient } from "../omp-rpc-client.js";

test("launches an RPC process, negotiates v2, and correlates prompt ack", async (context) => {
  const mockPath = fileURLToPath(new URL("./fixtures/mock-omp.js", import.meta.url));
  const frames: string[] = [];
  const client = new OmpRpcClient({
    command: process.execPath,
    launchArgs: [mockPath],
    startupTimeoutMs: 2_000,
    requestTimeoutMs: 2_000,
  });
  context.after(async () => client.stop());
  client.subscribe((frame) => {
    if (typeof frame.type === "string") {
      frames.push(frame.type);
    }
  });

  const ready = await client.start();
  assert.deepEqual(ready.supportedProtocolVersions, [1, 2]);
  const response = await client.prompt("请评估这个方案");
  assert.equal(response.command, "prompt");
  assert.equal(response.success, true);

  await new Promise((resolve) => setImmediate(resolve));
  assert.ok(frames.includes("agent_start"));
  assert.ok(frames.includes("agent_end"));
});

test("can restart after the RPC process exits unexpectedly", async () => {
  const mockPath = fileURLToPath(new URL("./fixtures/mock-omp.js", import.meta.url));
  const client = new OmpRpcClient({
    command: process.execPath,
    launchArgs: [mockPath, "--exit-after-ready"],
    startupTimeoutMs: 2_000,
    requestTimeoutMs: 2_000,
  });

  let resolveExit: (() => void) | undefined;
  const exited = new Promise<void>((resolve) => {
    resolveExit = resolve;
  });
  client.subscribe((frame) => {
    if (frame.type === "runtime_error") {
      resolveExit?.();
    }
  });

  await client.start();
  await exited;
  const secondReady = await client.start();
  assert.equal(secondReady.type, "ready");
  await client.stop();
});

test("cleans up when protocol negotiation fails", async () => {
  const mockPath = fileURLToPath(new URL("./fixtures/mock-omp.js", import.meta.url));
  const client = new OmpRpcClient({
    command: process.execPath,
    launchArgs: [mockPath, "--reject-negotiate"],
    startupTimeoutMs: 2_000,
    requestTimeoutMs: 2_000,
  });

  await assert.rejects(() => client.start(), /mock rejected protocol negotiation/);
  await assert.rejects(() => client.start(), /mock rejected protocol negotiation/);
  await client.stop();
});
