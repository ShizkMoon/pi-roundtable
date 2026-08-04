import assert from "node:assert/strict";
import test from "node:test";

import { DefaultDiscussionOrchestrator } from "../discussion-orchestrator.js";

const LIMITS = {
  softTurnLimit: 8,
  hardTurnLimit: 12,
  softRoundLimit: 2,
  hardRoundLimit: 3,
  maxConsecutiveTurnsPerRole: 2,
  maxInterruptionsPerSegment: 2,
  maxInterruptionsPerRole: 1,
  noProgressTurnLimit: 2,
  maxObserverProbesPerSegment: 1,
};

test("preserves deterministic floor order and observer budgets behind the seam", () => {
  const orchestrator = new DefaultDiscussionOrchestrator();
  orchestrator.configure(["Runtime ownership"], 3, LIMITS);
  orchestrator.setMode("free_discussion", "test");
  orchestrator.requestFloor({
    requestId: "normal",
    roleId: "role.z",
    kind: "normal",
    reason: "normal",
    prompt: "Normal",
    requestedAtSequence: 2,
  });
  orchestrator.requestFloor({
    requestId: "critical-b",
    roleId: "role.b",
    kind: "critical",
    reason: "correction",
    prompt: "Correct B",
    requestedAtSequence: 1,
  });
  orchestrator.requestFloor({
    requestId: "critical-a",
    roleId: "role.a",
    kind: "critical",
    reason: "correction",
    prompt: "Correct A",
    requestedAtSequence: 1,
  });

  const active = new Set(["role.a", "role.b", "role.z"]);
  assert.equal(orchestrator.takeNextFloor(active)?.requestId, "critical-a");
  assert.equal(orchestrator.takeNextFloor(active)?.requestId, "critical-b");
  assert.equal(orchestrator.takeNextFloor(active)?.requestId, "normal");
  assert.equal(orchestrator.acceptObserverProbe(), true);
  assert.equal(orchestrator.acceptObserverProbe(), false);
  assert.equal(orchestrator.snapshot().counters.observerProbes, 1);
  assert.equal(orchestrator.beginSegment().observerProbes, 0);
  assert.equal(orchestrator.acceptObserverProbe(), true);
});

test("round-trips scheduler state without exposing runtime or event ownership", () => {
  const source = new DefaultDiscussionOrchestrator();
  source.configure(["A", "B"], 2, LIMITS);
  source.requestFloor({
    requestId: "reply",
    roleId: "role.a",
    kind: "reply",
    reason: "reply",
    prompt: "Reply",
    requestedAtSequence: 4,
  });
  const snapshot = source.snapshot();
  const restored = new DefaultDiscussionOrchestrator();
  restored.restore(snapshot);

  assert.deepEqual(restored.snapshot(), snapshot);
  assert.deepEqual(Object.keys(restored).sort(), []);
});
