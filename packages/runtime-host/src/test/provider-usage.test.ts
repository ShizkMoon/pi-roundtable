import assert from "node:assert/strict";
import test from "node:test";

import { mergeProviderUsageSamples, parseProviderUsageSample } from "../provider-usage.js";

const CONTEXT = {
  providerId: "provider.test",
  modelId: "model.test",
  observedAt: "2026-08-01T00:00:00.000Z",
  source: "synthetic_fixture" as const,
};

test("normalizes provider aliases and nested cache fields", () => {
  const fixtures = [
    [{ input_tokens: 100, output_tokens: 20, cache_read_input_tokens: 60 }, 60],
    [{ prompt_tokens: 100, completion_tokens: 20, prompt_tokens_details: { cached_tokens: 70 } }, 70],
    [{ inputTokens: 100, outputTokens: 20, cacheReadTokens: 80 }, 80],
  ] as const;
  for (const [raw, expectedCacheRead] of fixtures) {
    const sample = parseProviderUsageSample(raw, CONTEXT);
    assert.equal(sample?.inputTokens, 100);
    assert.equal(sample?.outputTokens, 20);
    assert.equal(sample?.cacheReadTokens, expectedCacheRead);
    assert.equal(sample?.totalTokens, 120);
  }
});

test("preserves unknown SDK-normalized fields instead of fabricating zero usage", () => {
  const sample = parseProviderUsageSample({
    input: 500,
    output: 0,
    cacheRead: 0,
    cacheWrite: 0,
    totalTokens: 500,
  }, { ...CONTEXT, source: "sdk_normalized" });
  assert.equal(sample?.inputTokens, 500);
  assert.equal(sample?.outputTokens, undefined);
  assert.equal(sample?.cacheReadTokens, undefined);
  assert.equal(sample?.totalTokens, 500);
  assert.equal(parseProviderUsageSample({ input: -1, output: Number.NaN }, CONTEXT), undefined);
});

test("merges partial streaming samples without erasing known fields", () => {
  const first = parseProviderUsageSample({ input_tokens: 100 }, { ...CONTEXT, partial: true });
  const second = parseProviderUsageSample(
    { output_tokens: 25, cache_read_tokens: 50 },
    { ...CONTEXT, partial: false, observedAt: "2026-08-01T00:00:01.000Z" },
  );
  assert.ok(first !== undefined && second !== undefined);
  const merged = mergeProviderUsageSamples(first, second);
  assert.equal(merged.inputTokens, 100);
  assert.equal(merged.outputTokens, 25);
  assert.equal(merged.cacheReadTokens, 50);
  assert.equal(merged.partial, false);
});
