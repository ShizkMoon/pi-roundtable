import assert from "node:assert/strict";
import test from "node:test";

import {
  classifyPrefixInvalidation,
  createRoleContextSnapshot,
  validateRoleContextSnapshot,
  type RoleContextSnapshotInput,
} from "../role-context-snapshot.js";

function input(overrides: Partial<RoleContextSnapshotInput> = {}): RoleContextSnapshotInput {
  return {
    meetingId: "meeting-1",
    roleId: "role-1",
    runtimeGeneration: 2,
    sourceSequence: 10,
    policyVersion: "context-v1",
    stableRolePrefix: "immutable role prompt",
    sessionFrozenMemoryContext: ["approved memory"],
    dynamicAgendaRouting: ["agenda item 1"],
    recentTurns: [{ turnId: "turn-1", visibility: "public", content: "hello" }],
    largeToolResults: [{ toolCallId: "tool-1", toolName: "read", content: "result" }],
    providerPrivateState: { cacheSlot: "session-1" },
    ...overrides,
  };
}

test("fingerprints only stable role and frozen memory lanes", () => {
  const first = createRoleContextSnapshot(input());
  const dynamicChange = createRoleContextSnapshot(input({
    sourceSequence: 11,
    dynamicAgendaRouting: ["agenda item 2"],
    recentTurns: [{ turnId: "turn-2", visibility: "private", content: "private" }],
  }));
  assert.equal(first.prefixFingerprint, dynamicChange.prefixFingerprint);
  assert.equal(classifyPrefixInvalidation(first, dynamicChange), "manual");

  const memoryChange = createRoleContextSnapshot(input({
    sourceSequence: 11,
    sessionFrozenMemoryContext: ["new approved memory"],
  }));
  assert.equal(classifyPrefixInvalidation(first, memoryChange), "memory_changed");
});

test("rejects stale or corrupt snapshots before reuse", () => {
  const snapshot = createRoleContextSnapshot(input());
  const expected = {
    meetingId: snapshot.meetingId,
    roleId: snapshot.roleId,
    runtimeGeneration: snapshot.runtimeGeneration,
    sourceSequence: snapshot.sourceSequence,
    policyVersion: snapshot.policyVersion,
    prefixFingerprint: snapshot.prefixFingerprint,
  };
  assert.deepEqual(validateRoleContextSnapshot(snapshot, expected), { accepted: true });
  assert.deepEqual(validateRoleContextSnapshot(snapshot, { ...expected, runtimeGeneration: 3 }), {
    accepted: false,
    reason: "generation_mismatch",
  });
  const corrupt = { ...snapshot, stableRolePrefix: "tampered" };
  assert.deepEqual(validateRoleContextSnapshot(corrupt, expected), {
    accepted: false,
    reason: "snapshot_corrupt",
  });
  assert.throws(() => createRoleContextSnapshot(input({
    recentTurns: [{ turnId: "turn", visibility: "secret" as never, content: "bad" }],
  })), /visibility/);
});
