import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  isMeetingCommandKind,
  isMeetingEventKind,
  isRoleScope,
  MEETING_COMMAND_KINDS,
  MEETING_EVENT_KINDS,
} from "../index.js";

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
