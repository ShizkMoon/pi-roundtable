import assert from "node:assert/strict";
import test from "node:test";

import type { ModelRoute, WorkspaceProfile } from "@pi-roundtable/protocol";

import { WorkspaceProviderCapabilityRegistry } from "../provider-capability-registry.js";

function createWorkspace(): WorkspaceProfile {
  return {
    configurationVersion: 1,
    workspaceId: "workspace.registry",
    displayName: "Registry fixture",
    updatedAt: "2026-08-04T00:00:00.000Z",
    providers: [{
      providerProfileId: "provider.primary",
      displayName: "Primary provider",
      apiFamily: "anthropic_messages",
      runtimeProviderId: "anthropic-compatible",
      endpoint: "https://provider.example/v1",
      credentialRef: "secret://primary",
      enabled: true,
    }, {
      providerProfileId: "provider.fallback",
      displayName: "Fallback provider",
      apiFamily: "openai_responses",
      runtimeProviderId: "openai-compatible",
      credentialRef: "secret://fallback",
      enabled: true,
    }],
    models: [{
      modelProfileId: "model.primary",
      providerProfileId: "provider.primary",
      modelId: "primary-model",
      displayName: "Primary model",
      capabilities: ["text", "reasoning", "tool_calling"],
      contextWindow: 200_000,
      enabled: true,
    }, {
      modelProfileId: "model.fallback",
      providerProfileId: "provider.fallback",
      modelId: "fallback-model",
      displayName: "Fallback model",
      capabilities: ["text"],
      enabled: true,
    }],
    skills: [],
    mcpServers: [],
    roles: [],
  };
}

const ROUTE: ModelRoute = {
  primaryModelProfileId: "model.primary",
  fallbackModelProfileIds: ["model.fallback"],
  thinkingLevel: "high",
  maxOutputTokens: 16_384,
};

test("resolves declared primary provider and model metadata without credentials", () => {
  const resolved = new WorkspaceProviderCapabilityRegistry().resolve({
    workspace: createWorkspace(),
    modelRoute: ROUTE,
  });

  assert.deepEqual(resolved, {
    providerId: "anthropic-compatible",
    providerName: "Primary provider",
    apiFamily: "anthropic_messages",
    endpoint: "https://provider.example/v1",
    credentialRef: "secret://primary",
    modelId: "primary-model",
    modelName: "Primary model",
    modelCapabilities: ["text", "reasoning", "tool_calling"],
    contextWindow: 200_000,
    maxOutputTokens: 16_384,
    thinkingLevel: "high",
  });
  assert.equal(JSON.stringify(resolved).includes("runtime-secret"), false);
});

test("does not activate a fallback route when the primary model is disabled", () => {
  const workspace = createWorkspace();
  workspace.models[0]!.enabled = false;

  assert.throws(
    () => new WorkspaceProviderCapabilityRegistry().resolve({ workspace, modelRoute: ROUTE }),
    /Participant model route cannot be resolved/,
  );
});
