import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  API_FAMILIES,
  isMeetingCommandKind,
  isMeetingEventKind,
  isRoleScope,
  MEETING_COMMAND_KINDS,
  MEETING_EVENT_KINDS,
  MODEL_CAPABILITIES,
  ROUNDTABLE_SESSION_VERSION,
  THINKING_LEVELS,
  validateRoundtableSession,
  validateWorkspaceProfile,
  WORKSPACE_CONFIGURATION_VERSION,
} from "../index.js";
import type { RoundtableSession, WorkspaceProfile } from "../index.js";

function createWorkspace(): WorkspaceProfile {
  return {
    configurationVersion: WORKSPACE_CONFIGURATION_VERSION,
    workspaceId: "workspace.default",
    displayName: "Default workspace",
    updatedAt: "2026-08-01T00:00:00.000Z",
    providers: [
      {
        providerProfileId: "provider.openai",
        displayName: "OpenAI",
        apiFamily: "openai_responses",
        runtimeProviderId: "openai",
        endpoint: "https://api.openai.com/v1",
        credentialRef: "wincred://PiRoundtable/provider/provider.openai",
        enabled: true,
      },
    ],
    models: [
      {
        modelProfileId: "model.primary",
        providerProfileId: "provider.openai",
        modelId: "gpt-example",
        displayName: "Primary model",
        capabilities: ["text", "reasoning", "tool_calling"],
        enabled: true,
      },
    ],
    skills: [
      {
        skillId: "skill.research",
        displayName: "Research",
        description: "Produces a sourced research summary.",
        source: { kind: "local", locator: "skills/research/SKILL.md" },
        enabled: true,
      },
    ],
    mcpServers: [],
    roles: [
      {
        roleProfileId: "role.secretary",
        displayName: "Secretary",
        description: "Meeting secretary",
        systemPrompt: "You are the meeting secretary.",
        responsibilities: ["Maintain the agenda"],
        autoJoin: true,
        modelRoute: {
          primaryModelProfileId: "model.primary",
          fallbackModelProfileIds: [],
          thinkingLevel: "medium",
        },
        capabilities: { skillIds: [], mcpGrants: [], toolGrants: [] },
        delegation: {
          networkAccess: "subagent_required",
          resultMode: "summary_with_citations",
          maxConcurrentSubagents: 2,
        },
        memory: {
          mode: "selective",
          writeApproval: "meeting_close",
          promptEvolution: "review_required",
        },
      },
    ],
  };
}

test("role lifecycle event kinds are part of protocol v1", () => {
  for (const kind of [
    "role.temporary_registered",
    "role.promoted",
    "role.archived",
  ]) {
    assert.equal(isMeetingEventKind(kind), true);
    assert.equal(MEETING_EVENT_KINDS.some((candidate) => candidate === kind), true);
  }
  assert.equal(isMeetingEventKind("omp.role.spawned"), false);
});

test("role lifecycle commands and scopes are recognized", () => {
  for (const kind of ["role.create_temporary", "role.promote", "role.archive"]) {
    assert.equal(isMeetingCommandKind(kind), true);
    assert.equal(MEETING_COMMAND_KINDS.some((candidate) => candidate === kind), true);
  }
  assert.equal(isMeetingCommandKind("role.self_modify_prompt"), false);
  assert.equal(isRoleScope("long_term"), true);
  assert.equal(isRoleScope("temporary"), true);
  assert.equal(isRoleScope("omp_subagent"), false);
});

test("TypeScript kind lists stay aligned with the JSON Schemas", () => {
  const eventSchema = JSON.parse(
    readFileSync(
      new URL("../../../../protocol/schema/meeting-event.schema.json", import.meta.url),
      "utf8",
    ),
  ) as { properties: { kind: { "x-knownValues": string[] } } };
  const commandSchema = JSON.parse(
    readFileSync(
      new URL("../../../../protocol/schema/meeting-command.schema.json", import.meta.url),
      "utf8",
    ),
  ) as { properties: { kind: { enum: string[] } } };

  assert.deepEqual(eventSchema.properties.kind["x-knownValues"], [...MEETING_EVENT_KINDS]);
  assert.deepEqual(commandSchema.properties.kind.enum, [...MEETING_COMMAND_KINDS]);
});

test("configuration constants stay aligned with the JSON Schemas", () => {
  const workspaceSchema = JSON.parse(
    readFileSync(
      new URL("../../../../protocol/schema/workspace-profile.schema.json", import.meta.url),
      "utf8",
    ),
  ) as {
    properties: { configurationVersion: { const: number } };
    $defs: {
      providerProfile: { properties: { apiFamily: { enum: string[] } } };
      modelProfile: { properties: { capabilities: { items: { enum: string[] } } } };
      modelRoute: { properties: { thinkingLevel: { enum: string[] } } };
    };
  };
  const sessionSchema = JSON.parse(
    readFileSync(
      new URL("../../../../protocol/schema/roundtable-session.schema.json", import.meta.url),
      "utf8",
    ),
  ) as { properties: { sessionVersion: { const: number } } };

  assert.equal(workspaceSchema.properties.configurationVersion.const, WORKSPACE_CONFIGURATION_VERSION);
  assert.equal(sessionSchema.properties.sessionVersion.const, ROUNDTABLE_SESSION_VERSION);
  assert.deepEqual(workspaceSchema.$defs.providerProfile.properties.apiFamily.enum, [...API_FAMILIES]);
  assert.deepEqual(workspaceSchema.$defs.modelProfile.properties.capabilities.items.enum, [...MODEL_CAPABILITIES]);
  assert.deepEqual(workspaceSchema.$defs.modelRoute.properties.thinkingLevel.enum, [...THINKING_LEVELS]);
});

test("workspace validation resolves catalogs without exposing secret values", () => {
  const workspace = createWorkspace();
  assert.deepEqual(validateWorkspaceProfile(workspace), []);

  workspace.providers[0]!.endpoint = "https://token@example.com/v1";
  workspace.providers[0]!.credentialRef = "sk-plaintext";
  workspace.models.push({ ...workspace.models[0]!, modelProfileId: "model.primary" });
  const issues = validateWorkspaceProfile(workspace);
  assert.equal(issues.some((issue) => issue.code === "invalid_endpoint"), true);
  assert.equal(issues.some((issue) => issue.code === "invalid_credential_ref"), true);
  assert.equal(issues.some((issue) => issue.code === "duplicate_id"), true);
});

test("session validation enforces frozen role references and invitation provenance", () => {
  const workspace = createWorkspace();
  const session: RoundtableSession = {
    sessionVersion: ROUNDTABLE_SESSION_VERSION,
    sessionId: "session.design-review",
    workspaceId: workspace.workspaceId,
    title: "Design review",
    phase: "draft",
    createdAt: "2026-08-01T00:00:00.000Z",
    updatedAt: "2026-08-01T00:02:00.000Z",
    agenda: { subject: "Review session-centered navigation", objectives: [], constraints: [] },
    participants: [
      {
        participantId: "participant.secretary",
        scope: "long_term",
        roleProfileId: "role.secretary",
        displayName: "Secretary",
        systemPromptSnapshot: "You are the meeting secretary.",
        modelRouteSnapshot: workspace.roles[0]!.modelRoute,
        capabilitiesSnapshot: workspace.roles[0]!.capabilities,
        delegationSnapshot: workspace.roles[0]!.delegation,
        memoryPolicySnapshot: workspace.roles[0]!.memory,
        retentionPolicy: "retain_profile",
      },
      {
        participantId: "participant.planner",
        scope: "temporary",
        displayName: "System planner",
        systemPromptSnapshot: "Plan the system for this meeting only.",
        modelRouteSnapshot: workspace.roles[0]!.modelRoute,
        capabilitiesSnapshot: {
          skillIds: ["skill.research"],
          mcpGrants: [],
          toolGrants: [],
        },
        delegationSnapshot: workspace.roles[0]!.delegation,
        memoryPolicySnapshot: {
          mode: "disabled",
          writeApproval: "always",
          promptEvolution: "disabled",
        },
        invitation: {
          invitationId: "invite.planner",
          inviterType: "role",
          inviterId: "participant.secretary",
          purpose: "Produce the system plan",
          status: "accepted",
          createdAt: "2026-08-01T00:00:30.000Z",
          acceptedAt: "2026-08-01T00:01:00.000Z",
        },
        retentionPolicy: "review_at_close",
      },
    ],
  };

  assert.deepEqual(validateRoundtableSession(session, workspace), []);
  session.participants[1]!.invitation!.inviterId = "participant.unknown";
  assert.equal(
    validateRoundtableSession(session, workspace).some((issue) => issue.code === "invalid_invitation"),
    true,
  );
  session.participants[1]!.invitation!.inviterId = "participant.planner";
  assert.equal(
    validateRoundtableSession(session, workspace).some((issue) => issue.code === "invalid_invitation"),
    true,
  );
  session.participants[1]!.invitation!.inviterType = "user";
  session.participants[1]!.invitation!.inviterId = "user.someone_else";
  assert.equal(
    validateRoundtableSession(session, workspace).some((issue) => issue.code === "invalid_invitation"),
    true,
  );
});

test("draft sessions may persist before participant bindings are complete", () => {
  const workspace = createWorkspace();
  const session: RoundtableSession = {
    sessionVersion: ROUNDTABLE_SESSION_VERSION,
    sessionId: "session.unconfigured-draft",
    workspaceId: workspace.workspaceId,
    title: "Unconfigured draft",
    phase: "draft",
    createdAt: "2026-08-01T00:00:00.000Z",
    updatedAt: "2026-08-01T00:00:00.000Z",
    agenda: { subject: "Choose participants later", objectives: [], constraints: [] },
    participants: [],
  };

  assert.deepEqual(validateRoundtableSession(session, workspace), []);
  session.phase = "live";
  assert.equal(
    validateRoundtableSession(session, workspace).some(
      (issue) => issue.code === "invalid_participant_count",
    ),
    true,
  );
});
