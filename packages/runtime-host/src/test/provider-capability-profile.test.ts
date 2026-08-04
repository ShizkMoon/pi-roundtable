import assert from "node:assert/strict";
import test from "node:test";

import { resolveProviderCapabilityProfile } from "../provider-capability-profile.js";

test("resolves official provider cache modes without display-name inference", () => {
  const fixtures = [
    ["openai", "openai_responses", "automatic_prefix"],
    ["anthropic", "anthropic_messages", "explicit_breakpoints"],
    ["deepseek", "openai_chat_completions", "automatic_disk"],
  ] as const;
  for (const [providerId, apiFamily, cacheMode] of fixtures) {
    const profile = resolveProviderCapabilityProfile({ providerId, modelId: "model", apiFamily });
    assert.equal(profile.family, providerId);
    assert.equal(profile.cacheMode, cacheMode);
  }
});

test("keeps custom and unknown endpoints conservative unless explicitly reviewed", () => {
  const redirectedOfficialId = resolveProviderCapabilityProfile({
    providerId: "openai",
    modelId: "compatible-model",
    apiFamily: "openai_chat_completions",
    endpoint: "https://gateway.example/v1",
  });
  assert.equal(redirectedOfficialId.family, "compatible");
  assert.equal(redirectedOfficialId.cacheMode, "unsupported");

  const reviewed = resolveProviderCapabilityProfile({
    providerId: "reviewed-compatible",
    modelId: "model",
    apiFamily: "openai_chat_completions",
    endpoint: "https://gateway.example/v1",
    compatibleCacheMode: "automatic_prefix",
  });
  assert.equal(reviewed.cacheMode, "automatic_prefix");

  const unknown = resolveProviderCapabilityProfile({
    providerId: "opaque-provider",
    modelId: "model",
    apiFamily: "custom",
  });
  assert.equal(unknown.family, "unknown");
  assert.equal(unknown.supportsPromptCache, false);
});

test("uses the smaller context window when declared and runtime metadata disagree", () => {
  const profile = resolveProviderCapabilityProfile({
    providerId: "openai",
    modelId: "model",
    apiFamily: "openai_responses",
    declaredContextWindow: 128_000,
    runtimeContextWindow: 64_000,
  });
  assert.equal(profile.resolvedContextWindow, 64_000);
  assert.equal(profile.contextWindowSource, "conservative_min");
});
