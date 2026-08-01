import assert from "node:assert/strict";
import test from "node:test";

import { InMemoryMeetingStore, MeetingStoreError } from "../meeting-store.js";

test("runtime generations fence an expired owner and preserve event order", () => {
  let nowMs = Date.parse("2026-08-01T00:00:00.000Z");
  let id = 0;
  const store = new InMemoryMeetingStore({
    now: () => new Date(nowMs),
    nextId: () => `event-${++id}`,
  });

  const first = store.acquireLease({
    meetingId: "meeting-1",
    ownerRuntimeId: "runtime-a",
    ttlMs: 10_000,
    expectedGeneration: 0,
  });
  assert.equal(first.lease.runtimeGeneration, 1);
  assert.equal(first.event?.sequence, 1);

  const opened = store.append({
    meetingId: "meeting-1",
    ownerRuntimeId: "runtime-a",
    runtimeGeneration: 1,
    kind: "meeting.opened",
  });
  assert.equal(opened.sequence, 2);

  assert.throws(
    () =>
      store.acquireLease({
        meetingId: "meeting-1",
        ownerRuntimeId: "runtime-b",
        ttlMs: 10_000,
      }),
    (error: unknown) =>
      error instanceof MeetingStoreError && error.code === "lease_conflict",
  );

  nowMs += 10_001;
  const takeover = store.acquireLease({
    meetingId: "meeting-1",
    ownerRuntimeId: "runtime-b",
    ttlMs: 10_000,
    expectedGeneration: 1,
  });
  assert.equal(takeover.lease.runtimeGeneration, 2);
  assert.equal(takeover.event?.sequence, 3);

  assert.throws(
    () =>
      store.append({
        meetingId: "meeting-1",
        ownerRuntimeId: "runtime-a",
        runtimeGeneration: 1,
        kind: "speech.started",
        actorId: "role-a",
      }),
    (error: unknown) =>
      error instanceof MeetingStoreError && error.code === "stale_runtime_generation",
  );

  assert.deepEqual(
    store.eventsAfter("meeting-1", 1).map((event) => event.sequence),
    [2, 3],
  );
});

test("renewing the same owner keeps the runtime generation", () => {
  const store = new InMemoryMeetingStore();
  const first = store.acquireLease({
    meetingId: "meeting-renew",
    ownerRuntimeId: "runtime-a",
    ttlMs: 10_000,
  });
  const renewed = store.acquireLease({
    meetingId: "meeting-renew",
    ownerRuntimeId: "runtime-a",
    ttlMs: 20_000,
    expectedGeneration: 1,
  });

  assert.equal(first.lease.runtimeGeneration, 1);
  assert.equal(renewed.lease.runtimeGeneration, 1);
  assert.equal(renewed.renewed, true);
  assert.equal(renewed.event, null);
  assert.equal(store.eventsAfter("meeting-renew", 0).length, 1);
});

test("an expired owner cannot append a release event", () => {
  let nowMs = Date.parse("2026-08-01T00:00:00.000Z");
  const store = new InMemoryMeetingStore({ now: () => new Date(nowMs) });
  store.acquireLease({
    meetingId: "meeting-expired",
    ownerRuntimeId: "runtime-a",
    ttlMs: 1_000,
  });
  nowMs += 1_001;

  assert.throws(
    () => store.releaseLease("meeting-expired", "runtime-a"),
    (error: unknown) =>
      error instanceof MeetingStoreError && error.code === "lease_expired",
  );
  assert.equal(store.eventsAfter("meeting-expired", 0).length, 1);
});

test("role lifecycle events retain generation fencing and replay order", () => {
  let id = 0;
  const store = new InMemoryMeetingStore({ nextId: () => `role-event-${++id}` });
  store.acquireLease({
    meetingId: "meeting-roles",
    ownerRuntimeId: "runtime-windows",
    ttlMs: 10_000,
  });

  for (const kind of [
    "role.temporary_registered",
    "role.promoted",
    "role.archived",
  ] as const) {
    store.append({
      meetingId: "meeting-roles",
      ownerRuntimeId: "runtime-windows",
      runtimeGeneration: 1,
      kind,
      actorId: "role-researcher",
    });
  }

  assert.deepEqual(
    store.eventsAfter("meeting-roles", 1).map((event) => event.kind),
    ["role.temporary_registered", "role.promoted", "role.archived"],
  );
});

test("event visibility obeys the public and private audience contract", () => {
  const store = new InMemoryMeetingStore();
  const lease = store.acquireLease({
    meetingId: "meeting-visibility",
    ownerRuntimeId: "runtime-windows",
    ttlMs: 10_000,
  }).lease;

  assert.throws(
    () => store.append({
      meetingId: lease.meetingId,
      ownerRuntimeId: lease.ownerRuntimeId,
      runtimeGeneration: lease.runtimeGeneration,
      kind: "message.direct_sent",
      visibility: "private",
    }),
    (error: unknown) =>
      error instanceof MeetingStoreError && error.code === "invalid_argument",
  );
  assert.throws(
    () => store.append({
      meetingId: lease.meetingId,
      ownerRuntimeId: lease.ownerRuntimeId,
      runtimeGeneration: lease.runtimeGeneration,
      kind: "message.published",
      visibility: "public",
      audience: ["role.secretary"],
    }),
    (error: unknown) =>
      error instanceof MeetingStoreError && error.code === "invalid_argument",
  );
  const privateEvent = store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.direct_sent",
    actorId: "user.direct_host",
    targetId: "role.secretary",
    visibility: "private",
    audience: ["user.direct_host", "role.secretary"],
  });
  assert.equal(privateEvent.visibility, "private");
});
