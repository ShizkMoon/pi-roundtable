import assert from "node:assert/strict";
import test from "node:test";

import {
  buildStableRoleSystemPrompt,
  resolveRuntimeContextPolicy,
} from "../runtime-context-policy.js";

test("default context policy compacts early enough to preserve provider headroom", () => {
  assert.deepEqual(resolveRuntimeContextPolicy(128_000), {
    autoCompaction: true,
    contextWindow: 128_000,
    compactAtTokens: 79_360,
    reserveTokens: 48_640,
    keepRecentTokens: 25_600,
    cacheRetention: "short",
  });
});

test("context policy accepts bounded overrides without leaking into the protocol", () => {
  assert.deepEqual(resolveRuntimeContextPolicy(32_000, {
    autoCompaction: false,
    compactAtRatio: 0.75,
    keepRecentRatio: 0.25,
    cacheRetention: "long",
  }), {
    autoCompaction: false,
    contextWindow: 32_000,
    compactAtTokens: 24_000,
    reserveTokens: 8_000,
    keepRecentTokens: 8_000,
    cacheRetention: "long",
  });
});

test("context policy rejects unsafe or contradictory thresholds", () => {
  assert.throws(() => resolveRuntimeContextPolicy(2_048), /contextWindow/u);
  assert.throws(
    () => resolveRuntimeContextPolicy(128_000, { compactAtRatio: 0.3 }),
    /compactAtRatio/u,
  );
  assert.throws(
    () => resolveRuntimeContextPolicy(128_000, {
      compactAtRatio: 0.4,
      keepRecentRatio: 0.4,
    }),
    /keepRecentRatio/u,
  );
});

test("stable role prompt is deterministic and excludes turn-specific meeting state", () => {
  const prompt = buildStableRoleSystemPrompt(
    "Keep the meeting focused.\n",
    "role.architect",
    "体系架构师",
  );
  assert.equal(prompt, buildStableRoleSystemPrompt(
    "Keep the meeting focused.\n",
    "role.architect",
    "体系架构师",
  ));
  assert.match(prompt, /^Keep the meeting focused\./u);
  assert.match(prompt, /体系架构师 \(role\.architect\)/u);
  assert.match(prompt, /only assignments addressed to your display name or role id/u);
  assert.doesNotMatch(prompt, /agenda|free_discussion|latest public message/u);
});
