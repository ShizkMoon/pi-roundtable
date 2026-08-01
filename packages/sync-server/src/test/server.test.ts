import assert from "node:assert/strict";
import { once } from "node:events";
import { get, type IncomingMessage } from "node:http";
import type { AddressInfo } from "node:net";
import test from "node:test";

import { InMemoryMeetingStore } from "../meeting-store.js";
import { createSyncServer } from "../server.js";

test("health endpoint advertises development persistence", async (context) => {
  const server = createSyncServer();
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());

  const address = server.address() as AddressInfo;
  const response = await fetch(`http://127.0.0.1:${address.port}/healthz`);
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), {
    status: "ok",
    service: "pi-roundtable-sync",
    protocolVersion: 1,
    persistence: "memory",
  });
});

test("event replay rejects a partially numeric cursor", async (context) => {
  const server = createSyncServer();
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());

  const address = server.address() as AddressInfo;
  const response = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/meeting-1/events?after=12junk`,
  );
  assert.equal(response.status, 400);
  const body = (await response.json()) as { error: string };
  assert.equal(body.error, "bad_request");
});

test("development sync server rejects private events before authentication exists", async (context) => {
  const server = createSyncServer();
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;

  const leaseResponse = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/private-meeting/leases`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ownerRuntimeId: "runtime.windows", ttlMs: 30_000 }),
    },
  );
  const lease = (await leaseResponse.json()) as { lease: { runtimeGeneration: number } };
  const response = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/private-meeting/events`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        ownerRuntimeId: "runtime.windows",
        runtimeGeneration: lease.lease.runtimeGeneration,
        kind: "message.direct_sent",
        visibility: "private",
        payload: { message: "secret" },
      }),
    },
  );
  assert.equal(response.status, 400);
  assert.match(JSON.stringify(await response.json()), /private events require authenticated audience filtering/);
});

test("development sync server never replays or streams private store events", async (context) => {
  const store = new InMemoryMeetingStore();
  const leaseResult = store.acquireLease({
    meetingId: "private-replay",
    ownerRuntimeId: "runtime.windows",
    ttlMs: 30_000,
  });
  const lease = leaseResult.lease;
  store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.direct_sent",
    actorId: "user.direct_host",
    targetId: "role.secretary",
    visibility: "private",
    audience: ["user.direct_host", "role.secretary"],
    payload: { message: "replay-secret" },
  });
  const publicEvent = store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.published",
    actorId: "user.direct_host",
    payload: { message: "public-replay" },
  });

  const server = createSyncServer(store);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;
  const baseUrl = `http://127.0.0.1:${address.port}/v1/meetings/private-replay`;

  const replayResponse = await fetch(
    `${baseUrl}/events?after=${leaseResult.event?.sequence ?? 0}`,
  );
  const replay = (await replayResponse.json()) as {
    events: Array<{ visibility: string; payload: { message?: string } }>;
  };
  assert.deepEqual(replay.events.map((event) => event.payload.message), ["public-replay"]);
  assert.equal(replay.events.every((event) => event.visibility === "public"), true);

  let streamRequest: ReturnType<typeof get> | undefined;
  const streamResponse = await new Promise<IncomingMessage>((resolve, reject) => {
    streamRequest = get(`${baseUrl}/stream?after=${publicEvent.sequence}`, resolve);
    streamRequest.once("error", reject);
  });
  context.after(() => streamRequest?.destroy());
  streamResponse.setEncoding("utf8");
  let streamText = "";
  const publicStreamObserved = new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error("timed out waiting for public SSE event")), 2_000);
    streamResponse.on("data", (chunk: string) => {
      streamText += chunk;
      if (streamText.includes("public-live")) {
        clearTimeout(timeout);
        resolve();
      }
    });
    streamResponse.once("error", reject);
  });
  store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.direct_sent",
    actorId: "user.direct_host",
    targetId: "role.secretary",
    visibility: "private",
    audience: ["user.direct_host", "role.secretary"],
    payload: { message: "live-secret" },
  });
  store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.published",
    actorId: "user.direct_host",
    payload: { message: "public-live" },
  });
  await publicStreamObserved;
  streamResponse.destroy();
  assert.equal(streamText.includes("live-secret"), false);
  assert.equal(streamText.includes("public-live"), true);
});
