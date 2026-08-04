import assert from "node:assert/strict";
import test from "node:test";

import {
  createProviderCacheDiagnostic,
  resolveProviderCacheRequestPolicy,
} from "../provider-cache-adapter.js";
import { resolveProviderCapabilityProfile } from "../provider-capability-profile.js";

test("maps supported providers to explicit hints and unsupported providers to safe no-op", () => {
  const openai = resolveProviderCapabilityProfile({
    providerId: "openai",
    modelId: "model",
    apiFamily: "openai_responses",
  });
  assert.deepEqual(resolveProviderCacheRequestPolicy(openai, "long", "session-1"), {
    policyVersion: 1,
    mode: "automatic_prefix",
    cacheRetention: "long",
    sessionId: "session-1",
    sendsExplicitCacheHints: true,
    safeNoOp: false,
  });

  const unknown = resolveProviderCapabilityProfile({
    providerId: "unknown",
    modelId: "model",
    apiFamily: "custom",
  });
  assert.deepEqual(resolveProviderCacheRequestPolicy(unknown, "long", "session-1"), {
    policyVersion: 1,
    mode: "unsupported",
    cacheRetention: "none",
    sendsExplicitCacheHints: false,
    safeNoOp: true,
  });

  const anthropic = resolveProviderCapabilityProfile({
    providerId: "anthropic",
    modelId: "model",
    apiFamily: "anthropic_messages",
  });
  assert.equal(
    resolveProviderCacheRequestPolicy(anthropic, "short", "session-1").cacheRetention,
    "short",
  );
  const deepseek = resolveProviderCapabilityProfile({
    providerId: "deepseek",
    modelId: "model",
    apiFamily: "openai_chat_completions",
  });
  const deepseekPolicy = resolveProviderCacheRequestPolicy(deepseek, "long", "session-1");
  assert.equal(deepseekPolicy.mode, "automatic_disk");
  assert.equal(deepseekPolicy.cacheRetention, "none");
  assert.equal(deepseekPolicy.sendsExplicitCacheHints, false);
});

test("computes cache hit rate only from known bounded usage", () => {
  const profile = resolveProviderCapabilityProfile({
    providerId: "anthropic",
    modelId: "model",
    apiFamily: "anthropic_messages",
  });
  const diagnostic = createProviderCacheDiagnostic(profile, 4096, "memory_changed", {
    sampleVersion: 1,
    kind: "provider_usage",
    providerId: "anthropic",
    modelId: "model",
    observedAt: "2026-08-01T00:00:00.000Z",
    source: "raw_provider",
    inputTokens: 1_000,
    cacheReadTokens: 750,
    cacheWriteTokens: 0,
    partial: false,
  });
  assert.equal(diagnostic.eligible, true);
  assert.equal(diagnostic.hitRate, 750 / 1_750);
  assert.equal(diagnostic.tokenSavingsEstimate, 750);
});

test("keeps hit rate unknown when compatible input accounting is ambiguous", () => {
  const profile = resolveProviderCapabilityProfile({
    providerId: "compatible",
    modelId: "model",
    apiFamily: "openai_chat_completions",
    endpoint: "https://gateway.example/v1",
    compatibleCacheMode: "automatic_prefix",
  });
  const diagnostic = createProviderCacheDiagnostic(profile, 1024, "initial_session", {
    sampleVersion: 1,
    kind: "provider_usage",
    providerId: "compatible",
    modelId: "model",
    observedAt: "2026-08-01T00:00:00.000Z",
    source: "raw_provider",
    inputTokens: 100,
    cacheReadTokens: 80,
    cacheWriteTokens: 0,
    partial: false,
  });
  assert.equal(diagnostic.hitRate, undefined);
});
