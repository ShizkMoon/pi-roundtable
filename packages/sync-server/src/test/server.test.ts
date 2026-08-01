import assert from "node:assert/strict";
import { once } from "node:events";
import type { AddressInfo } from "node:net";
import test from "node:test";

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
