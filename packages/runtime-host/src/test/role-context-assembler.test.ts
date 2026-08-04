import assert from "node:assert/strict";
import test from "node:test";

import type { ParticipantManifest, WorkspaceProfile } from "@pi-roundtable/protocol";

import type { CapabilityResolver } from "../capability-resolver.js";
import type { ProviderCapabilityRegistry } from "../provider-capability-registry.js";
import { DefaultRoleContextAssembler } from "../role-context-assembler.js";

test("assembles a raw frozen role context and a generation-scoped credential lease", () => {
  const providerCapabilityRegistry: ProviderCapabilityRegistry = {
    resolve: () => ({
      providerId: "provider.runtime",
      providerName: "Provider",
      apiFamily: "openai_responses",
      credentialRef: "secret://provider",
      modelId: "model-runtime",
      modelName: "Model",
      modelCapabilities: ["text", "vision"],
      contextWindow: 128_000,
      maxOutputTokens: 8_192,
      thinkingLevel: "medium",
    }),
  };
  const capabilityResolver: CapabilityResolver = {
    resolve: () => ({ skillPaths: ["C:\\approved\\SKILL.md"], mcpServers: [] }),
  };
  const workspace = createWorkspace();
  const participant = createParticipant();
  const assembler = new DefaultRoleContextAssembler({
    providerCapabilityRegistry,
    capabilityResolver,
  });

  const resolved = assembler.assemble({
    workspace,
    participant,
    roleId: "participant.reviewer",
    scope: "long_term",
    runtimeGeneration: 7,
    resolveCredential: (reference) =>
      reference === "secret://provider" ? "provider-secret" : undefined,
  });

  assert.equal(resolved.systemPrompt, "Review the meeting.");
  assert.equal(resolved.systemPrompt.includes("participant.reviewer"), false);
  assert.equal(resolved.contextWindow, 128_000);
  assert.equal(resolved.maxOutputTokens, 8_192);
  assert.equal(resolved.thinkingLevel, "medium");
  assert.equal(resolved.delegation.maxConcurrentSubagents, 2);
  assert.equal(resolved.credentialLease.resolveApiKey("provider.runtime"), "provider-secret");
  resolved.credentialLease.close();
  assert.equal(resolved.credentialLease.closed, true);
});

test("validates participant identity before resolving provider or capabilities", () => {
  let providerCalled = false;
  let capabilitiesCalled = false;
  const assembler = new DefaultRoleContextAssembler({
    providerCapabilityRegistry: {
      resolve: () => {
        providerCalled = true;
        throw new Error("unexpected provider lookup");
      },
    },
    capabilityResolver: {
      resolve: () => {
        capabilitiesCalled = true;
        throw new Error("unexpected capability lookup");
      },
    },
  });

  assert.throws(
    () => assembler.assemble({
      workspace: createWorkspace(),
      participant: createParticipant(),
      roleId: "participant.other",
      scope: "long_term",
      runtimeGeneration: 1,
      resolveCredential: () => "secret",
    }),
    /identity or scope/,
  );
  assert.equal(providerCalled, false);
  assert.equal(capabilitiesCalled, false);
});

test("resolves the provider credential before capability credential references", () => {
  const credentialLookups: string[] = [];
  const assembler = new DefaultRoleContextAssembler({
    providerCapabilityRegistry: {
      resolve: () => ({
        providerId: "provider.runtime",
        providerName: "Provider",
        apiFamily: "custom",
        credentialRef: "secret://provider",
        modelId: "model-runtime",
        modelName: "Model",
        modelCapabilities: ["text"],
      }),
    },
    capabilityResolver: {
      resolve: (request) => {
        assert.equal(request.resolveCredential("secret://mcp"), "mcp-secret");
        return { skillPaths: [], mcpServers: [] };
      },
    },
  });
  const resolved = assembler.assemble({
    workspace: createWorkspace(),
    participant: createParticipant(),
    roleId: "participant.reviewer",
    scope: "long_term",
    runtimeGeneration: 1,
    resolveCredential: (reference) => {
      credentialLookups.push(reference);
      return reference === "secret://provider" ? "provider-secret" : "mcp-secret";
    },
  });

  assert.deepEqual(credentialLookups, ["secret://provider", "secret://mcp"]);
  resolved.credentialLease.close();
});

function createWorkspace(): WorkspaceProfile {
  return {
    configurationVersion: 1,
    workspaceId: "workspace.context",
    displayName: "Context fixture",
    updatedAt: "2026-08-04T00:00:00.000Z",
    providers: [],
    models: [],
    skills: [],
    mcpServers: [],
    roles: [{
      roleProfileId: "role.reviewer",
      displayName: "Reviewer",
      description: "Review",
      systemPrompt: "Review the meeting.",
      responsibilities: ["Review"],
      autoJoin: true,
      modelRoute: {
        primaryModelProfileId: "model.primary",
        fallbackModelProfileIds: [],
        thinkingLevel: "medium",
      },
      capabilities: { skillIds: [], mcpGrants: [], toolGrants: [] },
      delegation: {
        networkAccess: "subagent_preferred",
        resultMode: "summary_with_citations",
        maxConcurrentSubagents: 9,
      },
      memory: {
        mode: "selective",
        writeApproval: "meeting_close",
        promptEvolution: "review_required",
      },
    }],
  };
}

function createParticipant(): ParticipantManifest {
  return {
    participantId: "participant.reviewer",
    scope: "long_term",
    roleProfileId: "role.reviewer",
    displayName: "Reviewer",
    systemPromptSnapshot: "Review the meeting.",
    modelRouteSnapshot: {
      primaryModelProfileId: "model.primary",
      fallbackModelProfileIds: [],
      thinkingLevel: "medium",
    },
    capabilitiesSnapshot: { skillIds: [], mcpGrants: [], toolGrants: [] },
    delegationSnapshot: {
      networkAccess: "subagent_preferred",
      resultMode: "summary_with_citations",
      maxConcurrentSubagents: 9,
    },
    memoryPolicySnapshot: {
      mode: "selective",
      writeApproval: "meeting_close",
      promptEvolution: "review_required",
    },
    retentionPolicy: "retain_profile",
  };
}
