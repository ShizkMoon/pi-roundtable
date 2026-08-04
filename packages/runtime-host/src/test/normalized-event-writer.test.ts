import assert from "node:assert/strict";
import test from "node:test";

import { validateMeetingEvent } from "@pi-roundtable/protocol";

import {
  SynchronousNormalizedEventWriter,
  type NormalizedEventWriteRequest,
} from "../normalized-event-writer.js";

test("assigns one meeting-wide sequence across public and private lanes", () => {
  let stopped = false;
  let eventCounter = 0;
  const writer = new SynchronousNormalizedEventWriter({
    meetingId: "meeting.writer",
    runtimeGeneration: 3,
    now: () => new Date("2026-08-04T00:00:00.000Z"),
    eventIdFactory: () => `event.${++eventCounter}`,
    shouldWrite: (allowDuringStop) => !stopped || allowDuringStop,
  });
  const events: NonNullable<ReturnType<typeof writer.write>>[] = [];
  writer.subscribe((event) => events.push(event));
  writer.reset(8);

  const publicEvent = writer.write({
    kind: "message.published",
    actorId: "user.direct_host",
    targetId: null,
    causationId: "command.public",
    payload: { message: "Public", mentions: [] },
  });
  const privateEvent = writer.write({
    kind: "message.direct_sent",
    actorId: "user.direct_host",
    targetId: "role.review",
    causationId: "command.private",
    payload: { message: "Private" },
    visibility: "private",
    audience: ["user.direct_host", "role.review"],
  });

  assert.deepEqual(events.map((event) => event.sequence), [9, 10]);
  assert.equal(publicEvent?.audience, undefined);
  assert.deepEqual(privateEvent?.audience, ["user.direct_host", "role.review"]);
  assert.deepEqual(events.map((event) => validateMeetingEvent(event)), [[], []]);

  stopped = true;
  assert.equal(writer.write({
    kind: "speech.delta",
    actorId: "role.review",
    targetId: null,
    causationId: null,
    payload: { delta: "late" },
  }), undefined);
  assert.equal(writer.sequence, 10);
  assert.equal(writer.write({
    kind: "runtime.lease_released",
    actorId: "runtime.windows",
    targetId: null,
    causationId: null,
    payload: {},
    allowDuringStop: true,
  })?.sequence, 11);
});

test("publishes synchronously, isolates listener failures, and preserves reentrant order", () => {
  let eventCounter = 0;
  const writer = new SynchronousNormalizedEventWriter({
    meetingId: "meeting.writer",
    runtimeGeneration: 1,
    now: () => new Date("2026-08-04T00:00:00.000Z"),
    eventIdFactory: () => `event.${++eventCounter}`,
  });
  const observed: string[] = [];
  writer.subscribe((event) => {
    observed.push(`first:${event.kind}:${event.sequence}`);
    if (event.kind === "meeting.opened") {
      writer.write({
        kind: "discussion.mode_changed",
        actorId: "runtime.windows",
        targetId: null,
        causationId: event.eventId,
        payload: { mode: "agenda" },
      });
    }
  });
  writer.subscribe(() => {
    throw new Error("presentation failure");
  });
  writer.subscribe((event) => observed.push(`last:${event.kind}:${event.sequence}`));

  writer.write({
    kind: "meeting.opened",
    actorId: "runtime.windows",
    targetId: null,
    causationId: null,
    payload: {},
  });

  assert.deepEqual(observed, [
    "first:meeting.opened:1",
    "first:discussion.mode_changed:2",
    "last:discussion.mode_changed:2",
    "last:meeting.opened:1",
  ]);
  assert.equal(writer.sequence, 2);
});

test("rejects invalid initial sequence and generation values", () => {
  assert.throws(
    () => new SynchronousNormalizedEventWriter({
      meetingId: "meeting.writer",
      runtimeGeneration: 0,
    }),
    /positive safe integer/,
  );
  const writer = new SynchronousNormalizedEventWriter({
    meetingId: "meeting.writer",
    runtimeGeneration: 1,
  });
  assert.throws(() => writer.reset(-1), /non-negative safe integer/);
});

test("rejects invalid public and private audience pairings before consuming sequence", () => {
  const writer = new SynchronousNormalizedEventWriter({
    meetingId: "meeting.writer",
    runtimeGeneration: 1,
  });
  const writeUntrusted = (request: unknown) =>
    writer.write(request as NormalizedEventWriteRequest);

  assert.throws(() => writeUntrusted({
    kind: "meeting.opened",
    actorId: "runtime.windows",
    targetId: null,
    causationId: null,
    payload: {},
    visibility: "public",
    audience: ["role.a"],
  }), /Public normalized events cannot carry an audience/);
  assert.throws(() => writeUntrusted({
    kind: "message.direct_sent",
    actorId: "user.direct_host",
    targetId: "role.a",
    causationId: null,
    payload: { message: "private" },
    visibility: "private",
    audience: ["role.a", "role.a"],
  }), /non-empty unique audience/);
  assert.equal(writer.sequence, 0);
});
