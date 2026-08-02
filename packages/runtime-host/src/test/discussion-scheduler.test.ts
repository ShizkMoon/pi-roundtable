import assert from "node:assert/strict";
import test from "node:test";

import {
  FacilitatedDiscussionScheduler,
  type DiscussionFloorRequest,
} from "../discussion-scheduler.js";

function request(
  requestId: string,
  roleId: string,
  kind: DiscussionFloorRequest["kind"],
  requestedAtSequence: number,
): DiscussionFloorRequest {
  return {
    requestId,
    roleId,
    kind,
    reason: `${roleId} requests ${kind}`,
    prompt: `Respond as ${roleId}`,
    requestedAtSequence,
  };
}

test("orders floor requests by authority, correction priority, sequence, and role id", () => {
  const scheduler = new FacilitatedDiscussionScheduler();
  scheduler.configure(["Architecture"], 4);
  scheduler.setMode("free_discussion", "agenda_completed");
  assert.equal(scheduler.requestFloor(request("normal", "role.d", "normal", 10)).accepted, true);
  assert.equal(scheduler.requestFloor(request("reply", "role.c", "reply", 9)).accepted, true);
  assert.equal(scheduler.requestFloor(request("critical-b", "role.b", "critical", 8)).accepted, true);
  assert.equal(scheduler.requestFloor(request("critical-a", "role.a", "critical", 8)).accepted, true);
  assert.equal(scheduler.requestFloor(request("host", "role.host", "host", 11)).accepted, true);

  const active = new Set(["role.a", "role.b", "role.c", "role.d", "role.host"]);
  assert.equal(scheduler.takeNextFloor(active)?.requestId, "host");
  assert.equal(scheduler.takeNextFloor(active)?.requestId, "critical-a");
  assert.equal(scheduler.takeNextFloor(active)?.requestId, "critical-b");
  assert.equal(scheduler.takeNextFloor(active)?.requestId, "reply");
  assert.equal(scheduler.takeNextFloor(active)?.requestId, "normal");
});

test("applies a recent-speaker fairness penalty without overriding a host grant", () => {
  const scheduler = new FacilitatedDiscussionScheduler();
  scheduler.configure(["Review"], 3, { maxConsecutiveTurnsPerRole: 1 });
  scheduler.setMode("free_discussion", "agenda_completed");
  scheduler.recordTurn("role.a", ["decision"]);
  scheduler.requestFloor(request("a-again", "role.a", "normal", 20));
  scheduler.requestFloor(request("b", "role.b", "normal", 21));
  scheduler.requestFloor(request("host-a", "role.host", "host", 22));
  const active = new Set(["role.a", "role.b", "role.host"]);

  assert.equal(scheduler.takeNextFloor(active)?.requestId, "host-a");
  assert.equal(scheduler.takeNextFloor(active)?.requestId, "b");
  assert.equal(scheduler.takeNextFloor(active), undefined);
});

test("bounds critical interruptions per segment and per role, then queues excess as normal", () => {
  const scheduler = new FacilitatedDiscussionScheduler();
  scheduler.configure(["Safety"], 3, {
    maxInterruptionsPerSegment: 2,
    maxInterruptionsPerRole: 1,
  });
  scheduler.setMode("free_discussion", "agenda_completed");

  assert.equal(scheduler.acceptInterruption("role.a"), true);
  const repeated = scheduler.requestFloor(request("repeat", "role.a", "critical", 12));
  assert.equal(repeated.accepted, true);
  assert.equal(repeated.downgradedFromCritical, true);
  assert.equal(repeated.request?.kind, "normal");
  assert.equal(scheduler.acceptInterruption("role.b"), true);
  assert.equal(scheduler.canAcceptInterruption("role.c"), false);
  scheduler.acceptObserverProbe();
  scheduler.recordTurn("role.a", []);
  const nextSegment = scheduler.beginSegment();
  assert.equal(nextSegment.publicTurns, 0);
  assert.equal(nextSegment.rounds, 0);
  assert.equal(nextSegment.noProgressTurns, 0);
  assert.equal(nextSegment.interruptions, 0);
  assert.equal(nextSegment.observerProbes, 0);
  assert.equal(nextSegment.consecutiveRoleId, undefined);
  assert.equal(nextSegment.consecutiveTurns, 0);
  assert.deepEqual(nextSegment.interruptionsByRole, {});
  assert.equal(scheduler.canAcceptInterruption("role.a"), true);
});

test("soft and no-progress limits converge while the hard limit pauses automation", () => {
  const soft = new FacilitatedDiscussionScheduler();
  soft.configure(["One"], 2, {
    softTurnLimit: 3,
    hardTurnLimit: 5,
    softRoundLimit: 2,
    hardRoundLimit: 3,
    noProgressTurnLimit: 2,
  });
  soft.setMode("free_discussion", "agenda_completed");
  assert.equal(soft.recordTurn("role.a", []).transition, undefined);
  assert.equal(soft.recordTurn("role.b", []).transition?.mode, "convergence");
  assert.equal(soft.mode, "convergence");

  const hard = new FacilitatedDiscussionScheduler();
  hard.configure(["One"], 2, {
    softTurnLimit: 3,
    hardTurnLimit: 5,
    softRoundLimit: 2,
    hardRoundLimit: 3,
    noProgressTurnLimit: 2,
  });
  hard.setMode("convergence", "host_requested");
  hard.recordTurn("role.a", ["decision"]);
  hard.recordTurn("role.b", ["decision"]);
  hard.recordTurn("role.a", ["decision"]);
  hard.recordTurn("role.b", ["decision"]);
  const result = hard.recordTurn("role.a", ["decision"]);
  assert.equal(result.transition?.mode, "paused");
  assert.equal(hard.mode, "paused");
  assert.equal(hard.snapshot().pauseReason, "hard_limit");
});

test("round-trips a paused scheduler snapshot without losing queue or budgets", () => {
  const scheduler = new FacilitatedDiscussionScheduler();
  scheduler.configure(["One", "Two"], 3);
  scheduler.setMode("free_discussion", "agenda_completed");
  scheduler.recordTurn("role.a", ["action"]);
  scheduler.requestFloor(request("role-b", "role.b", "reply", 14));
  scheduler.pause("host_pause");

  const restored = new FacilitatedDiscussionScheduler(scheduler.snapshot());
  assert.deepEqual(restored.snapshot(), scheduler.snapshot());
  assert.equal(restored.resume("host_resume")?.mode, "free_discussion");
  assert.equal(restored.takeNextFloor(new Set(["role.a", "role.b"]))?.roleId, "role.b");
});

test("ignores terminal turns that arrive after automation has paused or completed", () => {
  const paused = new FacilitatedDiscussionScheduler();
  paused.configure(["One"], 2);
  paused.pause("host_pause");
  const pausedBefore = paused.snapshot().counters;
  assert.deepEqual(paused.recordTurn("role.late", ["decision"]).counters, pausedBefore);

  const completed = new FacilitatedDiscussionScheduler();
  completed.configure(["One"], 2);
  completed.setMode("completed", "host_completed");
  const completedBefore = completed.snapshot().counters;
  assert.deepEqual(completed.recordTurn("role.late", []).counters, completedBefore);
});

test("advances agenda deterministically and removes requests for departed roles", () => {
  const scheduler = new FacilitatedDiscussionScheduler();
  scheduler.configure(["One", "Two"], 2);
  scheduler.requestFloor(request("leaving", "role.a", "normal", 4));
  assert.equal(scheduler.removeRole("role.a").length, 1);
  assert.equal(scheduler.pendingRequestCount, 0);
  assert.equal(scheduler.advanceAgenda("item_done").active?.title, "Two");
  assert.equal(scheduler.advanceAgenda("agenda_done").transition?.mode, "free_discussion");
});
