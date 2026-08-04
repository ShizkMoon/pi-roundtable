import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  API_FAMILIES,
  canObserveMeetingEvent,
  DISCUSSION_MODES,
  DISCUSSION_PROGRESS_KINDS,
  FLOOR_REQUEST_KINDS,
  isMeetingCommandKind,
  isMeetingEventKind,
  isValidMeetingEventKind,
  isRoleScope,
  MEETING_COMMAND_KINDS,
  MEETING_EVENT_KINDS,
  MODEL_CAPABILITIES,
  PROTOCOL_VERSION,
  ROUNDTABLE_SESSION_VERSION,
  SESSION_EXPORT_PACKAGE_VERSION,
  THINKING_LEVELS,
  validateRoundtableSession,
  validateMeetingCommand,
  validateMeetingEvent,
  validateSessionExportPackage,
  validateWorkspaceProfile,
  WORKSPACE_CONFIGURATION_VERSION,
} from "../index.js";
import type { RoundtableSession, SessionExportPackage, WorkspaceProfile } from "../index.js";

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

test("runtime event validation enforces private audiences and public omission", () => {
  const privateEvent = {
    protocolVersion: 1,
    meetingId: "meeting.validation",
    eventId: "event.private",
    sequence: 1,
    runtimeGeneration: 1,
    kind: "message.direct_sent",
    occurredAt: "2026-08-03T00:00:00.000Z",
    actorId: "user.direct_host",
    targetId: "role.secretary",
    visibility: "private",
    audience: ["user.direct_host", "role.secretary"],
    payload: {},
  };
  assert.deepEqual(validateMeetingEvent(privateEvent), []);
  assert.ok(validateMeetingEvent({ ...privateEvent, audience: [] }).some((issue) => issue.path === "audience"));
  assert.ok(validateMeetingEvent({ ...privateEvent, visibility: "public" }).some((issue) => issue.path === "audience"));
  assert.ok(validateMeetingEvent({ ...privateEvent, runtimeGeneration: 0 }).some((issue) => issue.path === "runtimeGeneration"));
});

test("runtime command validation rejects unknown fields and invalid generations", () => {
  const command = {
    protocolVersion: 1,
    meetingId: "meeting.validation",
    commandId: "command.open",
    kind: "meeting.open",
    issuedAt: "2026-08-03T00:00:00.000Z",
    runtimeGeneration: 1,
    payload: {},
  };
  assert.deepEqual(validateMeetingCommand(command), []);
  const issues = validateMeetingCommand({ ...command, runtimeGeneration: 0, secret: "not allowed" });
  assert.ok(issues.some((issue) => issue.path === "runtimeGeneration"));
  assert.ok(issues.some((issue) => issue.code === "additional_property"));
});

test("role lifecycle event kinds are part of protocol v1", () => {
  for (const kind of [
    "role.temporary_registered",
    "role.promoted",
    "role.archived",
  ]) {
    assert.equal(isMeetingEventKind(kind), true);
    assert.equal(MEETING_EVENT_KINDS.some((candidate) => candidate === kind), true);
  }
  assert.equal(isMeetingEventKind("vendor.role.spawned"), false);
  assert.equal(isValidMeetingEventKind("vendor.role_spawned"), true);
  assert.equal(isValidMeetingEventKind("vendor"), false);
});

test("wire validation accepts additive namespaced event kinds without treating them as known reducer transitions", () => {
  const event = {
    protocolVersion: 1,
    meetingId: "meeting.future",
    eventId: "event.future",
    sequence: 1,
    runtimeGeneration: 1,
    kind: "vendor.future_event",
    occurredAt: "2026-08-04T00:00:00.000Z",
    visibility: "public",
    payload: {},
  };
  assert.deepEqual(validateMeetingEvent(event), []);
  assert.equal(isMeetingEventKind(event.kind), false);
  assert.ok(validateMeetingEvent({ ...event, kind: "vendor" }).some((issue) => issue.path === "kind"));
  assert.ok(validateMeetingEvent({ ...event, kind: "Vendor.future" }).some((issue) => issue.path === "kind"));
  assert.ok(validateMeetingEvent({ ...event, occurredAt: "2026-08-04" }).some((issue) => issue.path === "occurredAt"));
  assert.ok(validateMeetingEvent({ ...event, occurredAt: "0" }).some((issue) => issue.path === "occurredAt"));
});

test("workspace endpoints reject query and fragment while accepting the full IPv4 loopback range", () => {
  const workspace = createWorkspace();
  workspace.providers[0]!.endpoint = "http://127.0.2.4/v1";
  assert.deepEqual(validateWorkspaceProfile(workspace), []);
  workspace.providers[0]!.endpoint = "https://api.example.com/v1?token=not-allowed";
  assert.ok(validateWorkspaceProfile(workspace).some((issue) => issue.code === "invalid_endpoint"));
  workspace.providers[0]!.endpoint = "https://api.example.com/v1#fragment";
  assert.ok(validateWorkspaceProfile(workspace).some((issue) => issue.code === "invalid_endpoint"));
  for (const endpoint of [
    "https://api.example.com/v1?",
    "https://api.example.com/v1#",
    "https://api.example.com/v1 x",
    `https://api.example.com/${"x".repeat(2049)}`,
  ]) {
    workspace.providers[0]!.endpoint = endpoint;
    assert.ok(validateWorkspaceProfile(workspace).some((issue) => issue.code === "invalid_endpoint"));
  }
  workspace.providers[0]!.endpoint = "http://[::ffff:127.0.0.1]/v1";
  assert.ok(validateWorkspaceProfile(workspace).some((issue) => issue.code === "invalid_endpoint"));
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

test("conversation visibility and routing commands are explicit", () => {
  for (const kind of ["speech.broadcast", "speech.direct"]) {
    assert.equal(isMeetingCommandKind(kind), true);
  }
  for (const kind of ["message.published", "message.direct_sent"]) {
    assert.equal(isMeetingEventKind(kind), true);
  }
  const privateEvent = {
    protocolVersion: PROTOCOL_VERSION,
    meetingId: "meeting.private",
    eventId: "event.private",
    sequence: 1,
    runtimeGeneration: 1,
    kind: "message.direct_sent" as const,
    occurredAt: "2026-08-01T00:00:00.000Z",
    visibility: "private" as const,
    audience: ["user.direct_host", "role.secretary"],
    payload: {},
  };
  assert.equal(canObserveMeetingEvent(privateEvent, "role.secretary"), true);
  assert.equal(canObserveMeetingEvent(privateEvent, "role.host"), false);
});

test("facilitated discussion commands, events, and stable enums are explicit", () => {
  for (const kind of [
    "discussion.configure",
    "discussion.mode.set",
    "discussion.resume",
    "agenda.advance",
    "floor.request",
    "floor.grant",
    "floor.reject",
    "convergence.record",
  ]) {
    assert.equal(isMeetingCommandKind(kind), true);
  }
  for (const kind of [
    "discussion.configured",
    "discussion.mode_changed",
    "agenda.item_changed",
    "floor.requested",
    "floor.granted",
    "floor.rejected",
    "discussion.budget_updated",
    "convergence.recorded",
  ]) {
    assert.equal(isMeetingEventKind(kind), true);
  }
  assert.deepEqual(DISCUSSION_MODES, [
    "agenda", "free_discussion", "convergence", "paused", "completed",
  ]);
  assert.deepEqual(FLOOR_REQUEST_KINDS, [
    "host", "critical", "facilitator", "reply", "normal",
  ]);
  assert.deepEqual(DISCUSSION_PROGRESS_KINDS, [
    "decision", "objection", "evidence_request", "action",
  ]);
});

test("meeting event schema requires explicit visibility and a private audience", () => {
  const schema = JSON.parse(
    readFileSync(
      new URL("../../../../protocol/schema/meeting-event.schema.json", import.meta.url),
      "utf8",
    ),
  ) as {
    required: string[];
    allOf: Array<{ then?: { required?: string[]; not?: { required?: string[] } } }>;
  };
  assert.equal(schema.required.includes("visibility"), true);
  assert.equal(schema.allOf.some((rule) => rule.then?.required?.includes("audience")), true);
  assert.equal(schema.allOf.some((rule) => rule.then?.not?.required?.includes("audience")), true);
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
      skillProfile: { properties: { importStatus: { enum: string[] } } };
      mcpServerProfile: { properties: { importStatus: { enum: string[] } } };
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
  assert.deepEqual(workspaceSchema.$defs.skillProfile.properties.importStatus.enum, [
    "registered", "installed", "review_required", "blocked",
  ]);
  assert.deepEqual(workspaceSchema.$defs.mcpServerProfile.properties.importStatus.enum, [
    "registered", "installed", "review_required", "blocked",
  ]);
  assert.deepEqual(workspaceSchema.$defs.modelRoute.properties.thinkingLevel.enum, [...THINKING_LEVELS]);
});

test("workspace validation resolves catalogs without exposing secret values", () => {
  const workspace = createWorkspace();
  workspace.skills[0]!.source = {
    kind: "git",
    locator: "https://github.com/example/research-skill",
    contentDigest: "sha256:test",
  };
  workspace.skills[0]!.importStatus = "installed";
  workspace.skills[0]!.installDirectory = "C:\\Users\\Example\\AppData\\Local\\PiRoundtable\\catalog\\skills\\skill.research";
  workspace.skills[0]!.auditSummary = "Reviewed bounded repository snapshot.";
  workspace.skills[0]!.auditedAt = "2026-08-02T00:00:00.000Z";
  assert.deepEqual(validateWorkspaceProfile(workspace), []);

  workspace.providers[0]!.endpoint = "https://token@example.com/v1";
  workspace.providers[0]!.credentialRef = "sk-plaintext";
  workspace.models.push({ ...workspace.models[0]!, modelProfileId: "model.primary" });
  const issues = validateWorkspaceProfile(workspace);
  assert.equal(issues.some((issue) => issue.code === "invalid_endpoint"), true);
  assert.equal(issues.some((issue) => issue.code === "invalid_credential_ref"), true);
  assert.equal(issues.some((issue) => issue.code === "duplicate_id"), true);
});

test("MCP grants are exact subsets of the reviewed workspace tool catalog", () => {
  const workspace = createWorkspace();
  workspace.mcpServers.push({
    mcpServerId: "mcp.files",
    displayName: "Files",
    transport: "stdio",
    command: "node",
    toolCatalog: [
      { name: "read_file", displayName: "Read file" },
      { name: "write_file", displayName: "Write file" },
    ],
    enabled: true,
  });
  workspace.roles[0]!.capabilities.mcpGrants = [{
    mcpServerId: "mcp.files",
    toolAllowlist: ["read_file"],
    approvalMode: "always",
    executionMode: "direct",
  }];
  assert.deepEqual(validateWorkspaceProfile(workspace), []);

  workspace.roles[0]!.capabilities.mcpGrants[0]!.toolAllowlist.push("delete_everything");
  assert.equal(
    validateWorkspaceProfile(workspace).some((issue) => issue.code === "unlisted_tool"),
    true,
  );
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
  session.messages = [{
    messageId: "message.private-planner",
    kind: "host",
    speakerId: "user.direct_host",
    speakerName: "Host",
    visibility: "private",
    audienceRoleIds: ["participant.planner"],
    text: "Keep this in the planner thread.",
    state: "completed",
    occurredAt: "2026-08-01T00:01:30.000Z",
  }];
  assert.deepEqual(validateRoundtableSession(session, workspace), []);
  session.messages[0]!.audienceRoleIds = ["participant.unknown"];
  assert.equal(
    validateRoundtableSession(session, workspace).some(
      (issue) => issue.path.includes("audienceRoleIds") && issue.code === "missing_reference",
    ),
    true,
  );
  session.messages[0]!.audienceRoleIds = ["participant.planner"];
  session.messages[0]!.visibility = "public";
  assert.equal(
    validateRoundtableSession(session, workspace).some(
      (issue) => issue.path.includes("audienceRoleIds") && issue.code === "invalid_audience",
    ),
    true,
  );
  session.messages[0]!.visibility = "private";
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

test("validates normalized session export scope, ordering, and exact fields", () => {
  const exported: SessionExportPackage = {
    packageVersion: SESSION_EXPORT_PACKAGE_VERSION,
    protocolVersion: PROTOCOL_VERSION,
    sourceSessionId: "session.exported",
    title: "Exported meeting",
    exportedAt: "2026-08-02T00:00:00.000Z",
    includesPrivateMessages: false,
    messages: [{
      messageId: "message.public-1",
      kind: "host",
      speakerId: "user.direct_host",
      speakerName: "Host",
      visibility: "public",
      audienceRoleIds: [],
      text: "Normalized public content",
      state: "completed",
      occurredAt: "2026-08-02T00:00:01.000Z",
    }],
  };

  assert.deepEqual(validateSessionExportPackage(exported), []);
  exported.messages[0]!.audienceRoleIds = ["role.secretary"];
  assert.equal(
    validateSessionExportPackage(exported).some((issue) => issue.code === "invalid_visibility"),
    true,
  );
  const withSecretField = { ...exported, credentialRef: "wincred://must-not-export" };
  assert.equal(
    validateSessionExportPackage(withSecretField).some((issue) => issue.code === "invalid_shape"),
    true,
  );
});
