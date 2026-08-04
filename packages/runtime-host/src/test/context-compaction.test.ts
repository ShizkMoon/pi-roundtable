import assert from "node:assert/strict";
import test from "node:test";

import { finishContextCompaction, startContextCompaction } from "../context-compaction.js";

test("records bounded compaction evidence without retaining summary plaintext", () => {
  const tracker = startContextCompaction({
    startedAtMs: Date.parse("2026-08-01T00:00:00.000Z"),
    trigger: "threshold",
    triggerRatio: 0.62,
    providerId: "openai",
    modelId: "model",
    roleId: "role-1",
    sessionId: "session-1",
    runtimeGeneration: 4,
  });
  const record = finishContextCompaction(tracker, {
    finishedAtMs: Date.parse("2026-08-01T00:00:01.250Z"),
    status: "completed",
    tokensBefore: 62_000,
    estimatedTokensAfter: 20_000,
    firstKeptEntryId: "entry-10",
    summary: "private summary text",
  });
  assert.equal(record.durationMs, 1_250);
  assert.equal(record.runtimeGeneration, 4);
  assert.equal(record.summaryDigest?.length, 64);
  assert.ok(!JSON.stringify(record).includes("private summary text"));
});

test("validates ratios, timestamps, token bounds, and closed diagnostic codes", () => {
  assert.throws(() => startContextCompaction({
    startedAtMs: 0,
    trigger: "threshold",
    triggerRatio: 1.1,
    providerId: "provider",
    modelId: "model",
    roleId: "role",
    sessionId: "session",
  }), /triggerRatio/);
  const tracker = startContextCompaction({
    startedAtMs: 10,
    trigger: "overflow",
    triggerRatio: 0.8,
    providerId: "provider",
    modelId: "model",
    roleId: "role",
    sessionId: "session",
  });
  assert.throws(() => finishContextCompaction(tracker, {
    finishedAtMs: 9,
    status: "failed",
  }), /precede/);
  assert.throws(() => finishContextCompaction(tracker, {
    finishedAtMs: 11,
    status: "failed",
    failureCode: "contains secret data",
  }), /closed diagnostic code/);
});
