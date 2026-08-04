import assert from "node:assert/strict";
import test from "node:test";

import type { RoleScope } from "@pi-roundtable/protocol";

import { RoleSessionSupervisor } from "../role-session-supervisor.js";
import { RoleCredentialLease } from "../role-credential-lease.js";
import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeEventListener,
  RuntimeSessionInfo,
} from "../runtime-adapter.js";

interface TestConfiguration {
  credentialMarker: string;
}

class FakeAdapter implements RuntimeAdapter {
  readonly roleId: string;
  readonly listeners = new Set<RuntimeEventListener>();
  startCount = 0;
  stopCount = 0;
  executeCount = 0;
  retainListeners = false;
  throwOnSubscribe = false;
  throwOnUnsubscribe = false;
  onSubscribe: (() => void) | undefined;
  onStop: (() => Promise<void>) | undefined;
  onExecute: ((command: RuntimeCommand) => void) | undefined;
  startPromise: Promise<RuntimeSessionInfo> | undefined;
  executePromise: Promise<RuntimeCommandResult> | undefined;

  constructor(roleId: string) {
    this.roleId = roleId;
  }

  start(): Promise<RuntimeSessionInfo> {
    ++this.startCount;
    return this.startPromise ?? Promise.resolve(this.info());
  }

  stop(): Promise<void> {
    ++this.stopCount;
    return this.onStop?.() ?? Promise.resolve();
  }

  subscribe(listener: RuntimeEventListener): () => void {
    this.onSubscribe?.();
    if (this.throwOnSubscribe) {
      throw new Error("controlled subscribe failure");
    }
    this.listeners.add(listener);
    return () => {
      if (this.throwOnUnsubscribe) {
        throw new Error("controlled unsubscribe failure");
      }
      if (!this.retainListeners) {
        this.listeners.delete(listener);
      }
    };
  }

  execute(command: RuntimeCommand): Promise<RuntimeCommandResult> {
    ++this.executeCount;
    this.onExecute?.(command);
    return this.executePromise ?? Promise.resolve({ commandId: command.commandId, accepted: true });
  }

  emit(overrides: Partial<RuntimeEvent> = {}): void {
    const event: RuntimeEvent = {
      kind: "turn.started",
      runtimeId: this.info().runtimeId,
      sessionId: this.info().sessionId,
      occurredAt: "2026-08-04T00:00:00.000Z",
      roleId: this.roleId,
      correlationId: "command-1",
      payload: {},
      ...overrides,
    };
    for (const listener of this.listeners) {
      listener(event);
    }
  }

  info(): RuntimeSessionInfo {
    return {
      runtimeId: `runtime:${this.roleId}`,
      sessionId: `session:${this.roleId}`,
      engine: "test",
      capabilities: {
        steering: false,
        followUp: false,
        cancellation: true,
        tools: false,
        subagents: false,
      },
    };
  }
}

function definition(
  roleId: string,
  scope: RoleScope = "long_term",
): {
  roleId: string;
  displayName: string;
  scope: RoleScope;
  configuration: TestConfiguration;
} {
  return {
    roleId,
    displayName: roleId,
    scope,
    configuration: { credentialMarker: `credential:${roleId}` },
  };
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (error: unknown) => void;
} {
  let resolvePromise!: (value: T) => void;
  let rejectPromise!: (error: unknown) => void;
  const promise = new Promise<T>((resolve, reject) => {
    resolvePromise = resolve;
    rejectPromise = reject;
  });
  return { promise, resolve: resolvePromise, reject: rejectPromise };
}

test("fences retained callbacks by role session token and runtime generation", async () => {
  const root = new AbortController();
  const adapters: FakeAdapter[] = [];
  const observed: Array<{ generation: number; token: string; kind: string }> = [];
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 7,
    rootStopSignal: root.signal,
    adapterFactory: (identity) => {
      const adapter = new FakeAdapter(identity.roleId);
      adapter.retainListeners = true;
      adapters.push(adapter);
      return adapter;
    },
    onEvent: (session, event) => observed.push({
      generation: session.runtimeGeneration,
      token: session.sessionToken,
      kind: event.kind,
    }),
  });

  const first = await supervisor.startRole(definition("role.a"));
  assert.equal(first.status, "started");
  adapters[0]!.emit();
  assert.equal(observed.length, 1);
  assert.equal(observed[0]!.generation, 7);

  assert.equal((await supervisor.stopRole("role.a"))?.adapterStopped, true);
  const second = await supervisor.startRole(definition("role.a", "temporary"));
  assert.equal(second.status, "started");
  assert.notEqual(
    first.status === "started" ? first.session.sessionToken : undefined,
    second.status === "started" ? second.session.sessionToken : undefined,
  );
  adapters[0]!.emit({ kind: "tool.approval_requested" });
  adapters[1]!.emit({ kind: "turn.delta" });
  adapters[1]!.emit({ runtimeId: "runtime:stale" });
  assert.deepEqual(observed.map((event) => event.kind), ["turn.started", "turn.delta"]);
});

test("registers a stalled startup before root stop and disposes it once", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.a");
  const start = deferred<RuntimeSessionInfo>();
  adapter.startPromise = start.promise;
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 3,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
  });

  const starting = supervisor.startRole(definition("role.a"));
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(supervisor.has("role.a"), true);
  root.abort();
  assert.deepEqual(await starting, { status: "stop_requested" });
  assert.equal(supervisor.has("role.a"), false);
  assert.equal(adapter.stopCount, 1);
  start.reject(new Error("late controlled startup failure"));
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(adapter.stopCount, 1);
});

test("aborts execution and keeps adapter stop idempotent", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.a");
  adapter.executePromise = new Promise<RuntimeCommandResult>(() => undefined);
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 9,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
  });
  await supervisor.startRole(definition("role.a"));

  const execution = supervisor.execute("role.a", {
    kind: "turn.prompt",
    commandId: "pending-command",
    roleId: "role.a",
    message: "test",
    delivery: "immediate",
  });
  root.abort();
  const result = await execution;
  assert.equal(result.accepted, false);
  assert.equal(result.errorCode, "runtime_stopped");
  const stopped = await supervisor.stopAll();
  assert.equal(stopped[0]?.adapterStopped, true);
  assert.equal(adapter.stopCount, 1);
});

test("scopes child runtime resources to the parent role session", async () => {
  const root = new AbortController();
  const adapters = new Map<string, FakeAdapter>();
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 11,
    rootStopSignal: root.signal,
    adapterFactory: (identity) => {
      const adapter = new FakeAdapter(identity.roleId);
      adapters.set(identity.roleId, adapter);
      return adapter;
    },
    onEvent: () => undefined,
    stopGraceMs: 20,
  });
  await supervisor.startRole(definition("role.a"));
  await supervisor.startRole(definition("role.b"));
  const controllerA = new AbortController();
  const controllerB = new AbortController();
  const completionA = deferred<void>();
  const completionB = deferred<void>();
  const tokenA = supervisor.registerChild(
    "subagent",
    "child.a",
    "role.a",
    controllerA,
    completionA.promise,
  );
  const tokenB = supervisor.registerChild(
    "observer",
    "child.b",
    "role.b",
    controllerB,
    completionB.promise,
  );
  assert.equal(supervisor.countChildren("role.a"), 1);
  assert.equal(supervisor.isChildActive(tokenA), true);

  const stoppedA = await supervisor.stopRole("role.a");
  assert.equal(stoppedA?.childrenSettled, false);
  assert.equal(controllerA.signal.aborted, true);
  assert.equal(controllerB.signal.aborted, false);
  assert.equal(supervisor.isChildActive(tokenA), false);
  assert.equal(supervisor.isChildActive(tokenB), true);
  assert.equal(supervisor.has("role.b"), true);

  completionA.resolve();
  assert.equal(supervisor.releaseChild(tokenB), true);
  completionB.resolve();
  assert.equal(supervisor.countChildren("role.b"), 0);
});

test("rejects failed startup without retaining role resources", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.failed");
  adapter.startPromise = Promise.reject(new Error("controlled startup failure"));
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 1,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
  });

  await assert.rejects(
    supervisor.startRole(definition("role.failed")),
    /controlled startup failure/,
  );
  assert.equal(supervisor.has("role.failed"), false);
  assert.equal(adapter.stopCount, 1);
});

test("zeroizes a real credential lease when role startup fails", async () => {
  const adapter = new FakeAdapter("role.failed-secret");
  adapter.startPromise = Promise.reject(new Error("controlled startup failure"));
  const lease = new RoleCredentialLease({
    roleId: "role.failed-secret",
    runtimeGeneration: 21,
    providerId: "provider.test",
    apiKey: "failed-start-secret",
    mcpServers: [],
  });
  const supervisor = new RoleSessionSupervisor<{ credentialLease: RoleCredentialLease }>({
    runtimeGeneration: 21,
    rootStopSignal: new AbortController().signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
    releaseConfiguration: (configuration) => configuration.credentialLease.close(),
  });

  await assert.rejects(
    supervisor.startRole({
      roleId: "role.failed-secret",
      displayName: "Failed secret role",
      scope: "long_term",
      configuration: { credentialLease: lease },
    }),
    /controlled startup failure/,
  );
  assert.equal(lease.closed, true);
  assert.equal(lease.zeroizedSecretCount, lease.ownedSecretCount);
});

test("zeroizes a real credential lease when root cancellation wins startup", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.cancelled-secret");
  adapter.startPromise = new Promise<RuntimeSessionInfo>(() => undefined);
  const lease = new RoleCredentialLease({
    roleId: "role.cancelled-secret",
    runtimeGeneration: 22,
    providerId: "provider.test",
    apiKey: "cancelled-start-secret",
    mcpServers: [],
  });
  const supervisor = new RoleSessionSupervisor<{ credentialLease: RoleCredentialLease }>({
    runtimeGeneration: 22,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
    releaseConfiguration: (configuration) => configuration.credentialLease.close(),
  });
  const starting = supervisor.startRole({
    roleId: "role.cancelled-secret",
    displayName: "Cancelled secret role",
    scope: "long_term",
    configuration: { credentialLease: lease },
  });
  await new Promise<void>((resolve) => setImmediate(resolve));

  root.abort();
  assert.deepEqual(await starting, { status: "stop_requested" });
  for (let attempt = 0; attempt < 10 && !lease.closed; ++attempt) {
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  assert.equal(lease.closed, true);
  assert.equal(lease.zeroizedSecretCount, lease.ownedSecretCount);
  assert.equal(adapter.stopCount, 1);
  await supervisor.stopAll();
});

test("releases rejected definitions and keeps credential configuration off session views", async () => {
  const root = new AbortController();
  const released: TestConfiguration[] = [];
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 12,
    rootStopSignal: root.signal,
    adapterFactory: (identity) => new FakeAdapter(identity.roleId),
    onEvent: () => undefined,
    releaseConfiguration: (configuration) => released.push(configuration),
  });
  const active = definition("role.a");
  const started = await supervisor.startRole(active);
  assert.equal(started.status, "started");
  assert.equal("configuration" in started, false);
  assert.equal("configuration" in started.session, false);
  assert.equal(Object.isFrozen(started.session), true);
  assert.equal(
    supervisor.projectConfiguration("role.a", (configuration) => configuration.credentialMarker),
    "credential:role.a",
  );

  const duplicate = definition("role.a");
  await assert.rejects(supervisor.startRole(duplicate), /already exists|invalid identity/);
  const invalid = definition("");
  await assert.rejects(supervisor.startRole(invalid), /already exists|invalid identity/);
  assert.deepEqual(released, [duplicate.configuration, invalid.configuration]);

  await supervisor.stopAll();
  assert.deepEqual(released, [duplicate.configuration, invalid.configuration, active.configuration]);
  assert.equal(supervisor.projectConfiguration("role.a", () => "leaked"), undefined);
});

test("continues retirement when unsubscribe or configuration release throws", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.a");
  adapter.throwOnUnsubscribe = true;
  let releases = 0;
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 13,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
    releaseConfiguration: () => {
      ++releases;
      throw new Error("controlled release failure");
    },
  });
  await supervisor.startRole(definition("role.a"));

  const stopped = await supervisor.stopRole("role.a");
  assert.equal(stopped?.adapterStopped, true);
  assert.equal(adapter.stopCount, 1);
  assert.equal(releases, 1);
  assert.equal(supervisor.has("role.a"), false);

  const subscribeFailure = new FakeAdapter("role.failed");
  subscribeFailure.throwOnSubscribe = true;
  const failedSupervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 14,
    rootStopSignal: new AbortController().signal,
    adapterFactory: () => subscribeFailure,
    onEvent: () => undefined,
    releaseConfiguration: () => {
      throw new Error("controlled release failure");
    },
  });
  await assert.rejects(
    failedSupervisor.startRole(definition("role.failed")),
    /controlled subscribe failure/,
  );
  assert.equal(subscribeFailure.stopCount, 1);
});

test("root abort retires sessions without requiring a later explicit stopAll", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.a");
  let releases = 0;
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 15,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
    releaseConfiguration: () => {
      ++releases;
    },
  });
  await supervisor.startRole(definition("role.a"));

  root.abort();
  for (let attempt = 0; attempt < 10 && supervisor.has("role.a"); ++attempt) {
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  assert.equal(supervisor.has("role.a"), false);
  assert.equal(adapter.stopCount, 1);
  assert.equal(releases, 1);
  assert.equal((await supervisor.stopAll()).length, 1);
});

test("uses immutable role identities and validates startup events after start resolves", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.a");
  const start = deferred<RuntimeSessionInfo>();
  adapter.startPromise = start.promise;
  const observed: RuntimeEvent[] = [];
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 16,
    rootStopSignal: root.signal,
    adapterFactory: (identity) => {
      assert.equal(Object.isFrozen(identity), true);
      assert.throws(() => {
        (identity as { sessionToken: string }).sessionToken = "forged";
      }, TypeError);
      return adapter;
    },
    onEvent: (_session, event) => observed.push(event),
  });
  const starting = supervisor.startRole(definition("role.a"));
  await new Promise<void>((resolve) => setImmediate(resolve));
  adapter.emit({ kind: "turn.started" });
  adapter.emit({ kind: "runtime.ready", runtimeId: "runtime:wrong" });
  adapter.emit({ kind: "runtime.ready" });
  start.resolve(adapter.info());
  const outcome = await starting;

  assert.equal(outcome.status, "started");
  assert.deepEqual(observed.map((event) => event.kind), ["runtime.ready"]);
  await supervisor.stopAll();
});

test("fences reused child resource ids with a per-registration token", async () => {
  const supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 17,
    rootStopSignal: new AbortController().signal,
    adapterFactory: (identity) => new FakeAdapter(identity.roleId),
    onEvent: () => undefined,
  });
  await supervisor.startRole(definition("role.a"));
  const firstController = new AbortController();
  const first = supervisor.registerChild(
    "observer",
    "reused-child",
    "role.a",
    firstController,
    Promise.resolve(),
  );
  assert.equal(supervisor.releaseChild(first), true);
  const secondController = new AbortController();
  const second = supervisor.registerChild(
    "observer",
    "reused-child",
    "role.a",
    secondController,
    Promise.resolve(),
  );

  assert.notEqual(first.resourceToken, second.resourceToken);
  assert.equal(supervisor.isChildActive(first), false);
  assert.equal(supervisor.releaseChild(first), false);
  assert.equal(supervisor.isChildActive(second), true);
  secondController.abort();
  assert.equal(supervisor.isChildActive(second), false);
  assert.equal(supervisor.releaseChild(second), true);
  await supervisor.stopAll();
});

test("contains stopAll reentry from adapter factory and subscription setup", async () => {
  for (const reentryPoint of ["factory", "subscribe"] as const) {
    const root = new AbortController();
    const adapter = new FakeAdapter(`role.${reentryPoint}`);
    let releases = 0;
    let supervisor!: RoleSessionSupervisor<TestConfiguration>;
    if (reentryPoint === "subscribe") {
      adapter.onSubscribe = () => {
        void supervisor.stopAll();
      };
    }
    supervisor = new RoleSessionSupervisor<TestConfiguration>({
      runtimeGeneration: reentryPoint === "factory" ? 18 : 19,
      rootStopSignal: root.signal,
      adapterFactory: () => {
        if (reentryPoint === "factory") {
          void supervisor.stopAll();
        }
        return adapter;
      },
      onEvent: () => undefined,
      releaseConfiguration: () => {
        ++releases;
      },
    });

    assert.deepEqual(
      await supervisor.startRole(definition(`role.${reentryPoint}`)),
      { status: "stop_requested" },
    );
    assert.equal(supervisor.size, 0);
    assert.equal(adapter.startCount, 0);
    assert.equal(adapter.stopCount, 1);
    assert.equal(releases, 1);
    assert.deepEqual(await supervisor.stopAll(), []);
  }
});

test("a factory-reentrant stopAll promise waits for the early adapter retirement", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.factory-stop-barrier");
  const stopGate = deferred<void>();
  adapter.onStop = () => stopGate.promise;
  let releases = 0;
  let reentrantStop!: Promise<unknown>;
  let supervisor!: RoleSessionSupervisor<TestConfiguration>;
  supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 21,
    rootStopSignal: root.signal,
    adapterFactory: () => {
      reentrantStop = supervisor.stopAll();
      return adapter;
    },
    onEvent: () => undefined,
    releaseConfiguration: () => {
      ++releases;
    },
  });

  const starting = supervisor.startRole(definition("role.factory-stop-barrier"));
  await new Promise<void>((resolve) => setImmediate(resolve));
  let stopAllSettled = false;
  void reentrantStop.then(() => {
    stopAllSettled = true;
  });
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(adapter.stopCount, 1);
  assert.equal(releases, 1);
  assert.equal(stopAllSettled, false);

  stopGate.resolve();
  assert.deepEqual(await starting, { status: "stop_requested" });
  await reentrantStop;
  assert.equal(stopAllSettled, true);
});

test("publishes adapter stop single-flight before reentrant cleanup", async () => {
  const root = new AbortController();
  const adapter = new FakeAdapter("role.reentrant-stop");
  let supervisor!: RoleSessionSupervisor<TestConfiguration>;
  supervisor = new RoleSessionSupervisor<TestConfiguration>({
    runtimeGeneration: 20,
    rootStopSignal: root.signal,
    adapterFactory: () => adapter,
    onEvent: () => undefined,
    stopGraceMs: 20,
  });
  await supervisor.startRole(definition("role.reentrant-stop"));
  adapter.onExecute = () => root.abort();
  adapter.onStop = async () => {
    await supervisor.stopAll();
  };

  const result = await supervisor.execute("role.reentrant-stop", {
    kind: "turn.prompt",
    commandId: "trigger-reentrant-stop",
    roleId: "role.reentrant-stop",
    message: "stop",
    delivery: "immediate",
  });
  assert.equal(result.errorCode, "runtime_stopped");
  await supervisor.stopAll();
  assert.equal(adapter.stopCount, 1);
});
