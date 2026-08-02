import assert from "node:assert/strict";
import test from "node:test";

import { Pool } from "pg";

import { PostgresMeetingStore } from "../postgres-meeting-store.js";

const databaseUrl = process.env.TEST_DATABASE_URL;

test("PostgreSQL store survives process restart with fenced sequence and private audience", {
  skip: databaseUrl === undefined ? "TEST_DATABASE_URL is not configured" : false,
}, async () => {
  assert.ok(databaseUrl !== undefined);
  const suffix = `${process.pid}-${Date.now()}`;
  const meetingId = `meeting.pg-${suffix}`;
  const runtimeId = `runtime.pg-${suffix}`;

  const first = PostgresMeetingStore.fromConnectionString(databaseUrl);
  await first.initialize();
  const lease = (await first.acquireLease({ meetingId, ownerRuntimeId: runtimeId, ttlMs: 60_000 })).lease;
  await first.append({
    meetingId,
    ownerRuntimeId: runtimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.published",
    visibility: "public",
    payload: { message: "durable-public" },
  });
  await first.append({
    meetingId,
    ownerRuntimeId: runtimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.direct_sent",
    visibility: "private",
    audience: ["user.direct_host", "role.secretary"],
    payload: { message: "durable-private" },
  });
  await first.close();

  const restarted = PostgresMeetingStore.fromConnectionString(databaseUrl);
  await restarted.initialize();
  try {
    const replay = await restarted.eventsAfter(meetingId, 0);
    assert.deepEqual(replay.map((event) => event.sequence), [1, 2, 3]);
    assert.deepEqual(replay.map((event) => event.runtimeGeneration), [1, 1, 1]);
    assert.deepEqual(replay[2]?.audience, ["user.direct_host", "role.secretary"]);
    assert.equal((await restarted.currentLease(meetingId))?.ownerRuntimeId, runtimeId);
    const released = await restarted.releaseLease(meetingId, runtimeId);
    assert.equal(released.sequence, 4);

    const probe = new Pool({ connectionString: databaseUrl });
    try {
      const keyTable = await probe.query<{ name: string | null }>("SELECT to_regclass('public.pi_roundtable_key_envelopes')::text AS name");
      assert.equal(keyTable.rows[0]?.name, "pi_roundtable_key_envelopes");
      await probe.query("DELETE FROM pi_roundtable_meetings WHERE meeting_id = $1", [meetingId]);
    } finally {
      await probe.end();
    }
  } finally {
    await restarted.close();
  }
});
