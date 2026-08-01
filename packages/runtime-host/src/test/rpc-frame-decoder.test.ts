import assert from "node:assert/strict";
import { Buffer } from "node:buffer";
import test from "node:test";

import { RpcFrameDecoder } from "../rpc-frame-decoder.js";

function chunkLines(value: Record<string, unknown>, chunkSize: number): string[] {
  const bytes = Buffer.from(JSON.stringify(value), "utf8");
  const parts: Buffer[] = [];
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    parts.push(bytes.subarray(offset, Math.min(offset + chunkSize, bytes.length)));
  }
  return parts.map((part, index) =>
    JSON.stringify({
      type: "rpc_chunk",
      chunkId: "rpc-test",
      index,
      count: parts.length,
      byteLength: bytes.length,
      data: part.toString("base64"),
    }),
  );
}

test("decodes a regular JSONL frame", () => {
  const decoder = new RpcFrameDecoder();
  assert.deepEqual(decoder.pushLine('{"type":"agent_start"}'), [{ type: "agent_start" }]);
});

test("reassembles a UTF-8 protocol v2 chunk sequence", () => {
  const decoder = new RpcFrameDecoder();
  const logical = { type: "message_update", text: "数值策划正在发言" };
  const lines = chunkLines(logical, 7);
  const frames = lines.flatMap((line) => decoder.pushLine(line));
  assert.deepEqual(frames, [logical]);
});

test("rejects an interrupted chunk sequence and resets", () => {
  const decoder = new RpcFrameDecoder();
  const first = chunkLines({ type: "message_update", text: "long enough" }, 3)[0]!;
  assert.deepEqual(decoder.pushLine(first), []);
  assert.throws(
    () => decoder.pushLine('{"type":"agent_end"}'),
    /interrupted/,
  );
  assert.deepEqual(decoder.pushLine('{"type":"agent_end"}'), [{ type: "agent_end" }]);
});

test("enforces the advertised logical frame limit", () => {
  const decoder = new RpcFrameDecoder(1024, 8);
  const line = JSON.stringify({
    type: "rpc_chunk",
    chunkId: "too-large",
    index: 0,
    count: 1,
    byteLength: 9,
    data: Buffer.from("123456789").toString("base64"),
  });
  assert.throws(() => decoder.pushLine(line), /reassembly limit/);
});
