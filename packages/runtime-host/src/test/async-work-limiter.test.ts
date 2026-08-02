import assert from "node:assert/strict";
import test from "node:test";

import { AsyncWorkLimiter } from "../async-work-limiter.js";

test("bounds concurrent work and starts queued work in FIFO order", async () => {
  const limiter = new AsyncWorkLimiter(2);
  const controllers = [new AbortController(), new AbortController(), new AbortController()];
  const releases: Array<() => void> = [];
  const started: number[] = [];

  const runs = controllers.map((controller, index) => limiter.run(controller.signal, async () => {
    started.push(index);
    await new Promise<void>((resolve) => releases.push(resolve));
    return index;
  }));

  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.deepEqual(started, [0, 1]);
  assert.equal(limiter.activeCount, 2);
  assert.equal(limiter.waitingCount, 1);

  releases[0]?.();
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.deepEqual(started, [0, 1, 2]);
  releases[1]?.();
  releases[2]?.();
  assert.deepEqual(await Promise.all(runs), [0, 1, 2]);
  assert.equal(limiter.activeCount, 0);
});

test("removes aborted queued work without consuming a permit", async () => {
  const limiter = new AsyncWorkLimiter(1);
  let releaseFirst: (() => void) | undefined;
  const first = limiter.run(new AbortController().signal, async () => {
    await new Promise<void>((resolve) => {
      releaseFirst = resolve;
    });
  });
  const queuedController = new AbortController();
  const queued = limiter.run(queuedController.signal, async () => undefined);

  await new Promise<void>((resolve) => setImmediate(resolve));
  queuedController.abort(new Error("meeting stopped"));
  await assert.rejects(queued, /meeting stopped/);
  assert.equal(limiter.waitingCount, 0);
  releaseFirst?.();
  await first;
  assert.equal(limiter.activeCount, 0);
});
