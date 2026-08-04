import assert from "node:assert/strict";
import { once } from "node:events";
import { readFileSync } from "node:fs";
import { get, type IncomingMessage } from "node:http";
import type { AddressInfo } from "node:net";
import test from "node:test";

import type { MeetingEvent } from "@pi-roundtable/protocol";

import { createDeviceToken, DeviceTokenAuthenticator, type DeviceTokenPayload } from "../device-auth.js";
import { InMemoryMeetingStore, type MeetingStore } from "../meeting-store.js";
import { createSyncServer } from "../server.js";

const AUTH_KEY = Buffer.alloc(32, 0x5a);
const authenticator = new DeviceTokenAuthenticator(
  new Map([["test-key", AUTH_KEY]]),
  () => new Date("2026-08-02T00:00:00.000Z"),
);

function token(
  meetingId: string,
  options: { userId?: string; deviceId?: string; audienceIds?: string[]; runtimeIds?: string[] } = {},
): string {
  const payload: DeviceTokenPayload = {
    version: 1,
    userId: options.userId ?? "user.direct_host",
    deviceId: options.deviceId ?? "device.windows",
    meetingIds: [meetingId],
    audienceIds: options.audienceIds ?? ["role.secretary"],
    runtimeIds: options.runtimeIds ?? ["runtime.windows"],
    expiresAt: "2026-08-03T00:00:00.000Z",
  };
  return createDeviceToken("test-key", AUTH_KEY, payload);
}

function bearer(value: string): { authorization: string } {
  return { authorization: `Bearer ${value}` };
}

test("device-token environment configuration fails closed without leaking key material", () => {
  const secret = Buffer.alloc(32, 0x41).toString("base64");
  for (const raw of [undefined, "", "not-json", "[]", "{}", JSON.stringify({ dev: "short" })]) {
    assert.throws(
      () => DeviceTokenAuthenticator.fromEnvironment(raw),
      (error: unknown) => {
        assert.ok(error instanceof Error);
        assert.match(error.message, /PI_ROUNDTABLE_AUTH_KEYS_JSON/);
        assert.doesNotMatch(error.message, new RegExp(secret));
        return true;
      },
    );
  }

  assert.doesNotThrow(() => DeviceTokenAuthenticator.fromEnvironment(JSON.stringify({ dev: secret })));
});

test("development deployment requires an explicit auth key and stays memory-only", () => {
  const compose = readFileSync(new URL("../../../../deploy/compose.yaml", import.meta.url), "utf8");
  const environmentExample = readFileSync(new URL("../../../../.env.example", import.meta.url), "utf8");

  assert.match(compose, /PI_ROUNDTABLE_AUTH_KEYS_JSON:\s*"\$\{PI_ROUNDTABLE_AUTH_KEYS_JSON:\?/);
  assert.doesNotMatch(compose, /DATABASE_URL/);
  assert.match(environmentExample, /^PI_ROUNDTABLE_AUTH_KEYS_JSON=\s*$/m);
  assert.doesNotMatch(environmentExample, /is unauthenticated/i);
});

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
    authentication: "device_token",
  });
});

test("event replay rejects a partially numeric cursor", async (context) => {
  const server = createSyncServer(new InMemoryMeetingStore(), authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());

  const address = server.address() as AddressInfo;
  const response = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/meeting-1/events?after=12junk`,
    { headers: bearer(token("meeting-1")) },
  );
  assert.equal(response.status, 400);
  const body = (await response.json()) as { error: string };
  assert.equal(body.error, "bad_request");
});

test("authenticated runtime can append a private event with an explicit audience", async (context) => {
  const server = createSyncServer(new InMemoryMeetingStore(), authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;

  const leaseResponse = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/private-meeting/leases`,
    {
      method: "POST",
      headers: { "content-type": "application/json", ...bearer(token("private-meeting")) },
      body: JSON.stringify({ ownerRuntimeId: "runtime.windows", ttlMs: 30_000 }),
    },
  );
  const lease = (await leaseResponse.json()) as { lease: { runtimeGeneration: number } };
  const response = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/private-meeting/events`,
    {
      method: "POST",
      headers: { "content-type": "application/json", ...bearer(token("private-meeting")) },
      body: JSON.stringify({
        ownerRuntimeId: "runtime.windows",
        runtimeGeneration: lease.lease.runtimeGeneration,
        kind: "message.direct_sent",
        visibility: "private",
        audience: ["user.direct_host", "role.secretary"],
        payload: { message: "secret" },
      }),
    },
  );
  assert.equal(response.status, 201);
  const event = (await response.json()) as { visibility: string; audience: string[] };
  assert.equal(event.visibility, "private");
  assert.deepEqual(event.audience, ["user.direct_host", "role.secretary"]);
});

test("sync relay preserves additive namespaced event kinds and rejects malformed kinds", async (context) => {
  const server = createSyncServer(new InMemoryMeetingStore(), authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;
  const meetingId = "future-event-meeting";
  const headers = {
    "content-type": "application/json",
    ...bearer(token(meetingId)),
  };

  const leaseResponse = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/${meetingId}/leases`,
    {
      method: "POST",
      headers,
      body: JSON.stringify({ ownerRuntimeId: "runtime.windows", ttlMs: 30_000 }),
    },
  );
  const lease = (await leaseResponse.json()) as { lease: { runtimeGeneration: number } };
  const appendResponse = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/${meetingId}/events`,
    {
      method: "POST",
      headers,
      body: JSON.stringify({
        ownerRuntimeId: "runtime.windows",
        runtimeGeneration: lease.lease.runtimeGeneration,
        kind: "vendor.future_event",
        visibility: "public",
        payload: { value: "preserved" },
      }),
    },
  );
  assert.equal(appendResponse.status, 201);
  const appended = (await appendResponse.json()) as MeetingEvent;
  assert.equal(appended.kind, "vendor.future_event");

  const replayResponse = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/${meetingId}/events?after=0`,
    { headers: bearer(token(meetingId)) },
  );
  const replay = (await replayResponse.json()) as { events: MeetingEvent[] };
  assert.equal(replay.events.at(-1)?.kind, "vendor.future_event");
  assert.equal(replay.events.at(-1)?.sequence, appended.sequence);

  const malformedResponse = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/${meetingId}/events`,
    {
      method: "POST",
      headers,
      body: JSON.stringify({
        ownerRuntimeId: "runtime.windows",
        runtimeGeneration: lease.lease.runtimeGeneration,
        kind: "vendor",
        visibility: "public",
        payload: {},
      }),
    },
  );
  assert.equal(malformedResponse.status, 400);

  for (const body of [
    {
      ownerRuntimeId: "runtime.windows",
      runtimeGeneration: lease.lease.runtimeGeneration,
      kind: "message.published",
      visibility: "public",
      audience: ["role.secretary"],
      payload: {},
    },
    {
      ownerRuntimeId: "runtime.windows",
      runtimeGeneration: lease.lease.runtimeGeneration,
      kind: "message.direct_sent",
      actorId: "bad id",
      visibility: "private",
      audience: ["role.secretary"],
      payload: {},
    },
  ]) {
    const invalidEnvelopeResponse = await fetch(
      `http://127.0.0.1:${address.port}/v1/meetings/${meetingId}/events`,
      { method: "POST", headers, body: JSON.stringify(body) },
    );
    assert.equal(invalidEnvelopeResponse.status, 400);
  }
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

  const observerToken = token("private-replay", {
    userId: "user.observer",
    deviceId: "device.observer",
    audienceIds: [],
    runtimeIds: [],
  });
  const server = createSyncServer(store, authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;
  const baseUrl = `http://127.0.0.1:${address.port}/v1/meetings/private-replay`;

  const replayResponse = await fetch(
    `${baseUrl}/events?after=${leaseResult.event?.sequence ?? 0}`,
    { headers: bearer(observerToken) },
  );
  const replay = (await replayResponse.json()) as {
    events: Array<{ visibility: string; payload: { message?: string } }>;
  };
  assert.deepEqual(replay.events.map((event) => event.payload.message), ["public-replay"]);
  assert.equal(replay.events.every((event) => event.visibility === "public"), true);

  let streamRequest: ReturnType<typeof get> | undefined;
  const streamResponse = await new Promise<IncomingMessage>((resolve, reject) => {
    streamRequest = get(`${baseUrl}/stream?after=${publicEvent.sequence}`, {
      headers: bearer(observerToken),
    }, resolve);
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

test("authenticated user or role audience receives private replay while unrelated devices do not", async (context) => {
  const store = new InMemoryMeetingStore();
  const lease = store.acquireLease({ meetingId: "audience-replay", ownerRuntimeId: "runtime.windows", ttlMs: 30_000 }).lease;
  store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.direct_sent",
    visibility: "private",
    audience: ["user.direct_host", "role.secretary"],
    payload: { message: "audience-secret" },
  });
  const server = createSyncServer(store, authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;
  const response = await fetch(
    `http://127.0.0.1:${address.port}/v1/meetings/audience-replay/events?after=0`,
    { headers: bearer(token("audience-replay")) },
  );
  const body = (await response.json()) as { events: MeetingEvent[] };
  assert.equal(body.events.some((event) => event.payload.message === "audience-secret"), true);
});

test("SSE buffers events appended while the initial cursor replay is in progress", async (context) => {
  const store = new InMemoryMeetingStore();
  const delayedStore: MeetingStore = store;
  const originalEventsAfter = store.eventsAfter.bind(store);
  let releaseReplay!: () => void;
  const replayReleased = new Promise<void>((resolve) => { releaseReplay = resolve; });
  let markReplayStarted!: () => void;
  const replayStarted = new Promise<void>((resolve) => { markReplayStarted = resolve; });
  delayedStore.eventsAfter = async (meetingId, sequence) => {
    const snapshot = originalEventsAfter(meetingId, sequence);
    markReplayStarted();
    await replayReleased;
    return snapshot;
  };
  const lease = store.acquireLease({
    meetingId: "sse-gap",
    ownerRuntimeId: "runtime.windows",
    ttlMs: 30_000,
  }).lease;
  const server = createSyncServer(delayedStore, authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;

  let streamRequest: ReturnType<typeof get> | undefined;
  const streamResponsePromise = new Promise<IncomingMessage>((resolve, reject) => {
    streamRequest = get(`http://127.0.0.1:${address.port}/v1/meetings/sse-gap/stream?after=0`, {
      headers: bearer(token("sse-gap")),
    }, resolve);
    streamRequest.once("error", reject);
  });
  context.after(() => streamRequest?.destroy());
  await replayStarted;
  store.append({
    meetingId: lease.meetingId,
    ownerRuntimeId: lease.ownerRuntimeId,
    runtimeGeneration: lease.runtimeGeneration,
    kind: "message.published",
    actorId: "user.direct_host",
    payload: { message: "bridged-event" },
  });
  releaseReplay();

  const streamResponse = await streamResponsePromise;
  streamResponse.setEncoding("utf8");
  let streamText = "";
  await new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error("timed out waiting for buffered SSE event")), 2_000);
    streamResponse.on("data", (chunk: string) => {
      streamText += chunk;
      if (streamText.includes("bridged-event")) {
        clearTimeout(timeout);
        resolve();
      }
    });
    streamResponse.once("error", reject);
  });
  streamResponse.destroy();
  assert.equal((streamText.match(/bridged-event/g) ?? []).length, 1);
});

test("device token rejects missing, tampered, and cross-meeting access", async (context) => {
  const server = createSyncServer(new InMemoryMeetingStore(), authenticator);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  context.after(() => server.close());
  const address = server.address() as AddressInfo;
  const url = `http://127.0.0.1:${address.port}/v1/meetings/meeting-auth/events?after=0`;

  assert.equal((await fetch(url)).status, 401);
  assert.equal((await fetch(url, { headers: bearer(`${token("meeting-auth")}x`) })).status, 401);
  assert.equal((await fetch(url, { headers: bearer(token("meeting-other")) })).status, 403);
});
