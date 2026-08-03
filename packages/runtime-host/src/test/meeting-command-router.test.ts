import assert from "node:assert/strict";
import test from "node:test";

import {
  MEETING_COMMAND_KINDS,
  PROTOCOL_VERSION,
  type CommandReceipt,
  type JsonObject,
  type MeetingCommand,
  type MeetingCommandKind,
} from "@pi-roundtable/protocol";

import {
  MeetingCommandRouter,
  type MeetingCommandHandler,
  type MeetingCommandRouterState,
} from "../meeting-command-router.js";

function command(
  commandId: string,
  overrides: Partial<MeetingCommand> = {},
): MeetingCommand {
  return {
    protocolVersion: PROTOCOL_VERSION,
    meetingId: "meeting-router-test",
    commandId,
    kind: "meeting.open",
    issuedAt: "2026-08-04T00:00:00.000Z",
    runtimeGeneration: 7,
    payload: {},
    ...overrides,
  };
}

function handlersFor(handler: MeetingCommandHandler): Record<MeetingCommandKind, MeetingCommandHandler> {
  return Object.fromEntries(MEETING_COMMAND_KINDS.map((kind) => [kind, handler])) as
    Record<MeetingCommandKind, MeetingCommandHandler>;
}

function accepted(commandValue: MeetingCommand, state: MeetingCommandRouterState): CommandReceipt {
  return {
    protocolVersion: PROTOCOL_VERSION,
    meetingId: state.meetingId,
    commandId: commandValue.commandId,
    status: "accepted",
    acknowledgedAt: "2026-08-04T00:00:00.000Z",
    sequence: state.sequence,
  };
}

function createState(): MeetingCommandRouterState {
  return {
    meetingId: "meeting-router-test",
    runtimeGeneration: 7,
    sequence: 1,
    leaseActive: true,
    stopRequested: false,
    stopped: false,
  };
}

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolvePromise: (() => void) | undefined;
  const promise = new Promise<void>((resolve) => {
    resolvePromise = resolve;
  });
  return { promise, resolve: () => resolvePromise?.() };
}

test("serializes commands and lifecycle work through one FIFO that survives failure", async () => {
  const state = createState();
  const firstGate = deferred();
  const entered: string[] = [];
  const router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor(async (commandValue) => {
      entered.push(commandValue.commandId);
      if (commandValue.commandId === "first") {
        await firstGate.promise;
      }
      return accepted(commandValue, state);
    }),
  });

  const first = router.execute(command("first"));
  const lifecycle = router.serializeOperation(() => {
    entered.push("lifecycle");
    throw new Error("controlled lifecycle failure");
  });
  const second = router.execute(command("second"));
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.deepEqual(entered, ["first"]);

  firstGate.resolve();
  assert.equal((await first).status, "accepted");
  await assert.rejects(lifecycle, /controlled lifecycle failure/);
  assert.equal((await second).status, "accepted");
  assert.deepEqual(entered, ["first", "lifecycle", "second"]);
});

test("deduplicates canonical commands and preserves the original receipt", async () => {
  const state = createState();
  let handlerCalls = 0;
  const router = new MeetingCommandRouter({
    readState: () => state,
    now: () => new Date("2026-08-04T01:02:03.000Z"),
    handlers: handlersFor((commandValue) => {
      ++handlerCalls;
      return accepted(commandValue, state);
    }),
  });
  const original = command("same", { payload: { alpha: 1, beta: 2 } });
  const reordered = {
    payload: { beta: 2, alpha: 1 },
    runtimeGeneration: 7,
    issuedAt: original.issuedAt,
    kind: original.kind,
    commandId: original.commandId,
    meetingId: original.meetingId,
    protocolVersion: original.protocolVersion,
  } satisfies MeetingCommand;

  const first = await router.execute(original);
  first.message = "caller mutation";
  state.sequence = 99;
  state.stopRequested = true;
  const replay = await router.execute(reordered);

  assert.equal(first.status, "accepted");
  assert.equal(replay.status, "duplicate");
  assert.equal(replay.message, undefined);
  assert.equal(replay.sequence, 1);
  assert.equal(handlerCalls, 1);
});

test("rejects command ID conflicts without replacing the remembered command", async () => {
  const state = createState();
  let handlerCalls = 0;
  const router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor((commandValue) => {
      ++handlerCalls;
      return accepted(commandValue, state);
    }),
  });
  const original = command("conflict", { payload: { version: 1 } });

  assert.equal((await router.execute(original)).status, "accepted");
  const conflict = await router.execute(command("conflict", { payload: { version: 2 } }));
  assert.equal(conflict.status, "rejected");
  assert.equal(conflict.errorCode, "command_id_conflict");
  assert.equal((await router.execute(original)).status, "duplicate");
  assert.equal(handlerCalls, 1);
});

test("fails closed on generation before sequence and requires the active generation", async () => {
  const state = createState();
  state.sequence = 4;
  let handlerCalls = 0;
  const router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor((commandValue) => {
      ++handlerCalls;
      return accepted(commandValue, state);
    }),
  });

  for (const [index, runtimeGeneration] of [undefined, null, 6, 8].entries()) {
    const candidate = command(`generation-${index}`);
    if (runtimeGeneration === undefined) {
      delete candidate.runtimeGeneration;
    } else {
      candidate.runtimeGeneration = runtimeGeneration;
    }
    const receipt = await router.execute(candidate);
    assert.equal(receipt.errorCode, "runtime_generation_mismatch");
  }
  const precedence = await router.execute(command("precedence", {
    runtimeGeneration: 6,
    expectedSequence: 99,
  }));
  assert.equal(precedence.errorCode, "runtime_generation_mismatch");
  assert.equal(handlerCalls, 0);

  assert.equal((await router.execute(command("matching", {
    runtimeGeneration: 7,
    expectedSequence: 4,
  }))).status, "accepted");
  assert.equal(handlerCalls, 1);
});

test("checks expected sequence while preserving null and omitted bypass", async () => {
  const state = createState();
  state.sequence = 4;
  let handlerCalls = 0;
  const router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor((commandValue) => {
      ++handlerCalls;
      return accepted(commandValue, state);
    }),
  });

  for (const [commandId, expectedSequence] of [
    ["sequence-past", 3],
    ["sequence-future", 5],
  ] as const) {
    const receipt = await router.execute(command(commandId, { expectedSequence }));
    assert.equal(receipt.errorCode, "sequence_mismatch");
  }
  assert.equal((await router.execute(command("sequence-null", {
    expectedSequence: null,
  }))).status, "accepted");
  assert.equal((await router.execute(command("sequence-omitted"))).status, "accepted");
  assert.equal(handlerCalls, 2);
});

test("contains handler failures and bounds remembered receipts", async () => {
  const state = createState();
  const calls = new Map<string, number>();
  const router = new MeetingCommandRouter({
    readState: () => state,
    maxRememberedReceipts: 2,
    handlers: handlersFor((commandValue) => {
      calls.set(commandValue.commandId, (calls.get(commandValue.commandId) ?? 0) + 1);
      if (commandValue.commandId === "throws") {
        throw new Error("private handler details");
      }
      return accepted(commandValue, state);
    }),
  });

  const failed = await router.execute(command("throws"));
  assert.equal(failed.errorCode, "host_execution_failed");
  assert.doesNotMatch(failed.message ?? "", /private handler details/);
  assert.equal((await router.execute(command("two"))).status, "accepted");
  assert.equal((await router.execute(command("three"))).status, "accepted");
  assert.equal((await router.execute(command("two"))).status, "duplicate");
  assert.equal((await router.execute(command("throws"))).errorCode, "host_execution_failed");
  assert.equal(calls.get("throws"), 2);
});

test("rejects reentrant public execution instead of deadlocking the FIFO", async () => {
  const state = createState();
  let router!: MeetingCommandRouter;
  let nestedHandlerCalls = 0;
  router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor(async (commandValue) => {
      if (commandValue.commandId === "outer") {
        const nested = await router.execute(command("nested"));
        assert.equal(nested.errorCode, "reentrant_command");
      } else {
        ++nestedHandlerCalls;
      }
      return accepted(commandValue, state);
    }),
  });

  assert.equal((await router.execute(command("outer"))).status, "accepted");
  assert.equal(nestedHandlerCalls, 0);
  assert.equal((await router.execute(command("after-reentrancy"))).status, "accepted");
  assert.equal(nestedHandlerCalls, 1);
});

test("fingerprints deeply nested JSON iteratively and rejects malformed values", async () => {
  const state = createState();
  let handlerCalls = 0;
  const router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor((commandValue) => {
      ++handlerCalls;
      return accepted(commandValue, state);
    }),
  });
  let payload: Record<string, unknown> = {};
  for (let depth = 0; depth < 20_000; ++depth) {
    payload = { child: payload };
  }
  const deep = command("deep-json", { payload: payload as JsonObject });
  assert.equal((await router.execute(deep)).status, "accepted");

  const circular: Record<string, unknown> = {};
  circular.self = circular;
  const circularReceipt = await router.execute(command("circular", {
    payload: circular as JsonObject,
  }));
  assert.equal(circularReceipt.errorCode, "invalid_command_fingerprint");
  const nonJsonReceipt = await router.execute(command("non-json", {
    payload: { value: new Date() } as unknown as JsonObject,
  }));
  assert.equal(nonJsonReceipt.errorCode, "invalid_command_fingerprint");
  assert.equal(handlerCalls, 1);
});

test("rejects command input larger than the canonical serialization limit", async () => {
  const state = createState();
  let handlerCalls = 0;
  const router = new MeetingCommandRouter({
    readState: () => state,
    handlers: handlersFor((commandValue) => {
      ++handlerCalls;
      return accepted(commandValue, state);
    }),
  });

  const receipt = await router.execute(command("oversized-fingerprint", {
    payload: { value: "x".repeat(1_048_577) },
  }));
  assert.equal(receipt.errorCode, "invalid_command_fingerprint");
  assert.equal(handlerCalls, 0);
});

test("converts malformed handler receipts into stable host failures", async () => {
  const state = createState();
  const base = accepted(command("unused"), state);
  const malformed = [
    undefined,
    { ...base, sequence: 0 },
    { ...base, acknowledgedAt: "not-a-timestamp" },
    { ...base, message: { nested: true } },
    { ...base, unexpected: true },
  ];

  for (const [index, candidate] of malformed.entries()) {
    const commandValue = command(`invalid-handler-receipt-${index}`);
    const router = new MeetingCommandRouter({
      readState: () => state,
      handlers: handlersFor(() => ({
        ...candidate,
        commandId: commandValue.commandId,
      }) as unknown as CommandReceipt),
    });
    const receipt = await router.execute(commandValue);
    assert.equal(receipt.status, "rejected");
    assert.equal(receipt.errorCode, "host_execution_failed");
  }
});
