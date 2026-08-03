import assert from "node:assert/strict";
import test from "node:test";

import { RuntimeGenerationOwner } from "../runtime-generation-owner.js";

test("requires an externally assigned positive safe runtime generation", () => {
  for (const runtimeGeneration of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
    assert.throws(
      () => new RuntimeGenerationOwner({ runtimeId: "runtime.windows", runtimeGeneration }),
      /runtimeGeneration must be a positive safe integer/,
    );
  }

  const owner = new RuntimeGenerationOwner({
    runtimeId: "runtime.windows",
    runtimeGeneration: 7,
  });
  assert.equal(owner.runtimeId, "runtime.windows");
  assert.equal(owner.runtimeGeneration, 7);
  assert.equal(owner.matchesGeneration(7), true);
  assert.equal(owner.matchesGeneration(6), false);
  assert.equal(owner.matchesGeneration(undefined), false);
});

test("initializes configuration and acquires one lease for the fixed generation", () => {
  const owner = new RuntimeGenerationOwner({
    runtimeId: "runtime.windows",
    runtimeGeneration: 2,
  });

  assert.throws(
    () => owner.acquireLease(false),
    /Runtime configuration is not initialized/,
  );
  owner.assertCanInitializeConfiguration();
  owner.markConfigurationInitialized();
  assert.equal(owner.configurationInitialized, true);
  assert.throws(
    () => owner.markConfigurationInitialized(),
    /Runtime configuration is already initialized/,
  );

  owner.acquireLease(false);
  assert.equal(owner.leaseActive, true);
  assert.throws(
    () => owner.acquireLease(false),
    /Local Roundtable Host cannot be started again/,
  );
});

test("stop requests synchronously fence lifecycle entry and serialize cleanup once", async () => {
  const owner = new RuntimeGenerationOwner({
    runtimeId: "runtime.windows",
    runtimeGeneration: 3,
  });
  owner.markConfigurationInitialized();
  owner.acquireLease(false);

  owner.requestStop();
  assert.equal(owner.stopRequested, true);
  assert.equal(owner.stopped, false);
  assert.equal(owner.leaseActive, true);
  assert.throws(
    () => owner.acquireLease(false),
    /Local Roundtable Host cannot be started again/,
  );
  assert.throws(
    () => owner.assertCanInitializeConfiguration(),
    /Runtime configuration is already initialized/,
  );

  assert.equal(owner.beginStop(), true);
  assert.equal(owner.stopped, true);
  assert.equal(owner.beginStop(), false);
  assert.equal(owner.leaseActive, true);
  assert.equal(owner.releaseLease(), true);
  assert.equal(owner.releaseLease(), false);
  owner.clearConfiguration();
  assert.equal(owner.configurationInitialized, false);

  const idleOwner = new RuntimeGenerationOwner({
    runtimeId: "runtime.idle",
    runtimeGeneration: 4,
  });
  const stopObserved = idleOwner.waitForStopRequest();
  assert.equal(idleOwner.stopSignal.aborted, false);
  idleOwner.requestStop();
  await stopObserved;
  assert.equal(idleOwner.stopSignal.aborted, true);
  assert.throws(
    () => idleOwner.markConfigurationInitialized(),
    /Runtime configuration is already initialized/,
  );
  assert.throws(
    () => idleOwner.acquireLease(true),
    /Local Roundtable Host cannot be started again/,
  );
});

test("test adapters may acquire a lease without production configuration", () => {
  const owner = new RuntimeGenerationOwner({
    runtimeId: "runtime.test",
    runtimeGeneration: 1,
  });

  owner.acquireLease(true);
  assert.equal(owner.leaseActive, true);
});
