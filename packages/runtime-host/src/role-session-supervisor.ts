import type { RoleScope } from "@pi-roundtable/protocol";

import type {
  RuntimeAdapter,
  RuntimeCommand,
  RuntimeCommandResult,
  RuntimeEvent,
  RuntimeSessionInfo,
} from "./runtime-adapter.js";

const DEFAULT_STOP_GRACE_MS = 2_000;
const STARTUP_STOP_GRACE_MS = 250;
const CHILD_STOP_GRACE_MS = 250;

export interface RoleSessionDefinition<TConfiguration> {
  roleId: string;
  displayName: string;
  scope: RoleScope;
  configuration?: TConfiguration;
}

export interface RoleSessionView {
  readonly roleId: string;
  readonly displayName: string;
  readonly scope: RoleScope;
  readonly runtimeGeneration: number;
  readonly sessionToken: string;
}

export interface RoleSessionIdentity {
  readonly roleId: string;
  readonly runtimeGeneration: number;
  readonly sessionToken: string;
}

export type RoleChildKind = "subagent" | "observer" | "planner";

export interface RoleChildToken {
  readonly resourceId: string;
  readonly resourceToken: string;
  readonly kind: RoleChildKind;
  readonly parentRoleId: string;
  readonly runtimeGeneration: number;
  readonly parentSessionToken: string;
}

export interface RoleSessionStopResult {
  session: RoleSessionView;
  adapterStopped: boolean;
  childrenSettled: boolean;
}

export interface RoleSessionSupervisorOptions<TConfiguration> {
  runtimeGeneration: number;
  rootStopSignal: AbortSignal;
  adapterFactory: (
    identity: RoleSessionIdentity,
    configuration: TConfiguration | undefined,
  ) => RuntimeAdapter;
  onEvent: (session: RoleSessionView, event: RuntimeEvent) => void;
  releaseConfiguration?: (configuration: TConfiguration) => void;
  stopGraceMs?: number;
}

interface RoleSessionRecord<TConfiguration> {
  roleId: string;
  displayName: string;
  scope: RoleScope;
  runtimeGeneration: number;
  sessionToken: string;
  configuration?: TConfiguration;
  adapter: RuntimeAdapter;
  controller: AbortController;
  unsubscribe: () => void;
  info?: RuntimeSessionInfo;
  startupEvents: RuntimeEvent[];
  configurationReleased: boolean;
  retired: boolean;
  retirement?: Promise<RoleSessionStopResult>;
}

interface RoleChildRecord extends RoleChildToken {
  controller: AbortController;
  completion: Promise<void>;
}

/**
 * Owns runtime resources that must never outlive one role identity in one
 * runtime generation. Meeting state, command ordering, event sequencing, and
 * protocol projection deliberately remain in LocalRoundtableHost.
 */
export class RoleSessionSupervisor<TConfiguration> {
  readonly #runtimeGeneration: number;
  readonly #rootStopSignal: AbortSignal;
  readonly #adapterFactory: RoleSessionSupervisorOptions<TConfiguration>["adapterFactory"];
  readonly #onEvent: RoleSessionSupervisorOptions<TConfiguration>["onEvent"];
  readonly #releaseConfiguration: (configuration: TConfiguration) => void;
  readonly #stopGraceMs: number;
  readonly #sessions = new Map<string, RoleSessionRecord<TConfiguration>>();
  readonly #children = new Map<string, RoleChildRecord>();
  readonly #adapterStopPromises = new WeakMap<RuntimeAdapter, Promise<void>>();
  readonly #retirements = new Set<Promise<RoleSessionStopResult>>();
  readonly #startupSettlements = new Set<Promise<void>>();
  readonly #onRootAbort = (): void => {
    this.#stopping = true;
    this.#abortAllScopes();
    // Keep abort fencing synchronous while leaving potentially user-supplied
    // adapter cleanup outside the AbortSignal dispatch stack.
    queueMicrotask(() => void this.stopAll());
  };
  #nextSessionToken = 0;
  #nextChildToken = 0;
  #rootAbortListenerAttached = false;
  #stopping = false;
  #stopAllPromise: Promise<RoleSessionStopResult[]> | undefined;

  constructor(options: RoleSessionSupervisorOptions<TConfiguration>) {
    if (!Number.isSafeInteger(options.runtimeGeneration) || options.runtimeGeneration < 1) {
      throw new RangeError("runtimeGeneration must be a positive safe integer");
    }
    const stopGraceMs = options.stopGraceMs ?? DEFAULT_STOP_GRACE_MS;
    if (!Number.isSafeInteger(stopGraceMs) || stopGraceMs < 1 || stopGraceMs > 60_000) {
      throw new RangeError("stopGraceMs must be between 1 and 60000");
    }
    this.#runtimeGeneration = options.runtimeGeneration;
    this.#rootStopSignal = options.rootStopSignal;
    this.#adapterFactory = options.adapterFactory;
    this.#onEvent = options.onEvent;
    this.#releaseConfiguration = options.releaseConfiguration ?? (() => undefined);
    this.#stopGraceMs = stopGraceMs;
    if (this.#rootStopSignal.aborted) {
      this.#stopping = true;
    } else {
      this.#rootStopSignal.addEventListener("abort", this.#onRootAbort, { once: true });
      this.#rootAbortListenerAttached = true;
    }
  }

  get runtimeGeneration(): number {
    return this.#runtimeGeneration;
  }

  get size(): number {
    return this.#sessions.size;
  }

  has(roleId: string): boolean {
    return this.#sessions.has(roleId);
  }

  get(roleId: string): RoleSessionView | undefined {
    const record = this.#sessions.get(roleId);
    return record === undefined ? undefined : this.#view(record);
  }

  keys(): IterableIterator<string> {
    return this.#sessions.keys();
  }

  *entries(): IterableIterator<[string, RoleSessionView]> {
    for (const [roleId, record] of this.#sessions) {
      yield [roleId, this.#view(record)];
    }
  }

  [Symbol.iterator](): IterableIterator<[string, RoleSessionView]> {
    return this.entries();
  }

  updateScope(roleId: string, scope: RoleScope): RoleSessionView | undefined {
    const record = this.#sessions.get(roleId);
    if (record === undefined || record.retired) {
      return undefined;
    }
    record.scope = scope;
    return this.#view(record);
  }

  /**
   * Projects role configuration without placing its credential-bearing object
   * on a durable session view. The callback must not retain the borrowed value.
   */
  projectConfiguration<TResult>(
    roleId: string,
    project: (configuration: Readonly<TConfiguration>, session: RoleSessionView) => TResult,
  ): TResult | undefined {
    const record = this.#sessions.get(roleId);
    if (
      record === undefined ||
      record.configuration === undefined ||
      record.retired ||
      record.controller.signal.aborted
    ) {
      return undefined;
    }
    return project(record.configuration, this.#view(record));
  }

  async startRole(
    definition: RoleSessionDefinition<TConfiguration>,
  ): Promise<{ status: "started"; session: RoleSessionView } | {
    status: "stop_requested";
  }> {
    if (this.#stopping || this.#rootStopSignal.aborted) {
      if (definition.configuration !== undefined) {
        this.#tryReleaseConfiguration(definition.configuration);
      }
      return { status: "stop_requested" };
    }
    if (definition.roleId.length === 0 || this.#sessions.has(definition.roleId)) {
      if (definition.configuration !== undefined) {
        this.#tryReleaseConfiguration(definition.configuration);
      }
      throw new Error("Role session already exists or has an invalid identity");
    }
    let settleStartup!: () => void;
    const startupSettlement = new Promise<void>((resolve) => {
      settleStartup = resolve;
    });
    this.#startupSettlements.add(startupSettlement);
    try {
      const roleId = definition.roleId;
      const sessionToken = `${this.#runtimeGeneration}.${++this.#nextSessionToken}`;
      const identity: RoleSessionIdentity = Object.freeze({
        roleId: definition.roleId,
        runtimeGeneration: this.#runtimeGeneration,
        sessionToken,
      });
      let adapter: RuntimeAdapter;
      try {
        adapter = this.#adapterFactory(identity, definition.configuration);
      } catch (error) {
        if (definition.configuration !== undefined) {
          this.#tryReleaseConfiguration(definition.configuration);
        }
        throw error;
      }
      if (this.#stopping || this.#rootStopSignal.aborted) {
        if (definition.configuration !== undefined) {
          this.#tryReleaseConfiguration(definition.configuration);
        }
        await this.#stopAdapterWithGrace(
          adapter,
          Math.min(this.#stopGraceMs, STARTUP_STOP_GRACE_MS),
        );
        return { status: "stop_requested" };
      }
      const record: RoleSessionRecord<TConfiguration> = {
        roleId,
        displayName: definition.displayName,
        scope: definition.scope,
        ...(definition.configuration === undefined
          ? {}
          : { configuration: definition.configuration }),
        runtimeGeneration: this.#runtimeGeneration,
        sessionToken,
        adapter,
        controller: new AbortController(),
        unsubscribe: () => undefined,
        startupEvents: [],
        configurationReleased: false,
        retired: false,
      };
      try {
        record.unsubscribe = adapter.subscribe((event) => this.#forwardEvent(record, event));
      } catch (error) {
        if (record.configuration !== undefined) {
          record.configurationReleased = true;
          this.#tryReleaseConfiguration(record.configuration);
        }
        await this.#stopAdapterWithGrace(
          adapter,
          Math.min(this.#stopGraceMs, STARTUP_STOP_GRACE_MS),
        );
        throw error;
      }
      if (this.#stopping || this.#rootStopSignal.aborted) {
        record.retired = true;
        record.controller.abort();
        this.#tryUnsubscribe(record.unsubscribe);
        this.#releaseRecordConfiguration(record);
        await this.#stopAdapterWithGrace(
          adapter,
          Math.min(this.#stopGraceMs, STARTUP_STOP_GRACE_MS),
        );
        return { status: "stop_requested" };
      }
      // Register before start so root stop and stopAll own a stalled startup.
      this.#sessions.set(record.roleId, record);
      try {
        const outcome = await raceWithAbort(
          Promise.resolve().then(() => adapter.start()),
          record.controller.signal,
        );
        if (
          outcome.kind === "aborted" ||
          this.#stopping ||
          this.#rootStopSignal.aborted ||
          !this.#isCurrent(record)
        ) {
          void this.#retire(record);
          return { status: "stop_requested" };
        }
        record.info = outcome.value;
        this.#flushStartupEvents(record);
        if (this.#stopping || this.#rootStopSignal.aborted || !this.#isCurrent(record)) {
          await this.#retire(record);
          return { status: "stop_requested" };
        }
        return { status: "started", session: this.#view(record) };
      } catch (error) {
        await this.#retire(record);
        throw error;
      }
    } finally {
      settleStartup();
      this.#startupSettlements.delete(startupSettlement);
    }
  }

  async execute(roleId: string, command: RuntimeCommand): Promise<RuntimeCommandResult> {
    const record = this.#sessions.get(roleId);
    if (
      record === undefined ||
      record.retired ||
      record.controller.signal.aborted ||
      this.#stopping ||
      this.#rootStopSignal.aborted ||
      command.roleId !== roleId
    ) {
      return runtimeStoppedResult(command.commandId);
    }
    const outcome = await raceWithAbort(
      Promise.resolve().then(() => record.adapter.execute(command)),
      record.controller.signal,
    );
    if (
      outcome.kind === "aborted" ||
      this.#rootStopSignal.aborted ||
      !this.#isCurrent(record)
    ) {
      this.#requestAdapterStop(record.adapter);
      return runtimeStoppedResult(command.commandId);
    }
    return outcome.value;
  }

  async stopRole(roleId: string): Promise<RoleSessionStopResult | undefined> {
    const record = this.#sessions.get(roleId);
    return record === undefined ? undefined : this.#retire(record);
  }

  stopAll(): Promise<RoleSessionStopResult[]> {
    if (this.#stopAllPromise !== undefined) {
      return this.#stopAllPromise;
    }
    this.#stopping = true;
    this.#abortAllScopes();
    const records = [...this.#sessions.values()];
    const current = records.map((record) => this.#retire(record));
    const retirements = Promise.all([...new Set([...this.#retirements, ...current])]);
    const startupSettlements = Promise.all([...this.#startupSettlements]);
    const operation = Promise.all([retirements, startupSettlements]).then(([results]) => results);
    this.#stopAllPromise = operation.finally(() => this.#detachRootAbortListener());
    return this.#stopAllPromise;
  }

  countChildren(parentRoleId: string, kind?: RoleChildKind): number {
    let count = 0;
    for (const child of this.#children.values()) {
      if (
        !child.controller.signal.aborted &&
        child.parentRoleId === parentRoleId &&
        (kind === undefined || child.kind === kind)
      ) {
        ++count;
      }
    }
    return count;
  }

  registerChild(
    kind: RoleChildKind,
    resourceId: string,
    parentRoleId: string,
    controller: AbortController,
    completion: Promise<void>,
  ): RoleChildToken {
    const parent = this.#sessions.get(parentRoleId);
    if (
      resourceId.length === 0 ||
      parent === undefined ||
      parent.retired ||
      parent.controller.signal.aborted ||
      this.#children.has(resourceId)
    ) {
      throw new Error("Child runtime resource cannot be attached to this role session");
    }
    const record: RoleChildRecord = {
      resourceId,
      resourceToken: `${this.#runtimeGeneration}.child.${++this.#nextChildToken}`,
      kind,
      parentRoleId,
      runtimeGeneration: this.#runtimeGeneration,
      parentSessionToken: parent.sessionToken,
      controller,
      completion,
    };
    this.#children.set(resourceId, record);
    return this.#childToken(record);
  }

  isChildActive(token: RoleChildToken): boolean {
    const child = this.#children.get(token.resourceId);
    const parent = this.#sessions.get(token.parentRoleId);
    return this.#matchesChild(child, token) &&
      parent !== undefined &&
      !parent.retired &&
      !parent.controller.signal.aborted &&
      !child.controller.signal.aborted &&
      parent.sessionToken === token.parentSessionToken;
  }

  releaseChild(token: RoleChildToken): boolean {
    const child = this.#children.get(token.resourceId);
    if (!this.#matchesChild(child, token)) {
      return false;
    }
    return this.#children.delete(token.resourceId);
  }

  #retire(
    record: RoleSessionRecord<TConfiguration>,
  ): Promise<RoleSessionStopResult> {
    if (record.retirement !== undefined) {
      return record.retirement;
    }
    const retirement = this.#retireNow(record);
    record.retirement = retirement;
    this.#retirements.add(retirement);
    void retirement.then(
      () => this.#retirements.delete(retirement),
      () => this.#retirements.delete(retirement),
    );
    return retirement;
  }

  async #retireNow(
    record: RoleSessionRecord<TConfiguration>,
  ): Promise<RoleSessionStopResult> {
    const session = this.#view(record);
    if (this.#isCurrent(record)) {
      this.#sessions.delete(record.roleId);
    }
    if (!record.retired) {
      record.retired = true;
      record.controller.abort();
      record.startupEvents.length = 0;
      this.#tryUnsubscribe(record.unsubscribe);
      this.#releaseRecordConfiguration(record);
    }
    const children = this.#detachChildren(record);
    const [adapterStopped, childrenSettled] = await Promise.all([
      this.#stopAdapterWithGrace(
        record.adapter,
        record.info === undefined ? Math.min(this.#stopGraceMs, STARTUP_STOP_GRACE_MS) : this.#stopGraceMs,
      ),
      this.#settleChildrenWithGrace(children),
    ]);
    return { session, adapterStopped, childrenSettled };
  }

  #detachChildren(record: RoleSessionRecord<TConfiguration>): RoleChildRecord[] {
    const detached: RoleChildRecord[] = [];
    for (const [resourceId, child] of this.#children) {
      if (
        child.parentRoleId === record.roleId &&
        child.parentSessionToken === record.sessionToken
      ) {
        this.#children.delete(resourceId);
        child.controller.abort();
        detached.push(child);
      }
    }
    return detached;
  }

  async #settleChildrenWithGrace(children: readonly RoleChildRecord[]): Promise<boolean> {
    if (children.length === 0) {
      return true;
    }
    return withDeadline(
      Promise.allSettled(children.map((child) => child.completion)).then(() => true),
      Math.min(this.#stopGraceMs, CHILD_STOP_GRACE_MS),
      false,
    );
  }

  #forwardEvent(record: RoleSessionRecord<TConfiguration>, event: RuntimeEvent): void {
    if (!this.#isCurrent(record) || record.controller.signal.aborted || this.#rootStopSignal.aborted) {
      return;
    }
    if (event.roleId !== undefined && event.roleId !== null && event.roleId !== record.roleId) {
      return;
    }
    if (record.info === undefined) {
      if (
        event.kind === "runtime.ready" ||
        event.kind === "runtime.failed" ||
        event.kind === "runtime.stopped"
      ) {
        if (record.startupEvents.length < 8) {
          record.startupEvents.push(event);
        }
      }
      return;
    }
    if (event.runtimeId !== record.info.runtimeId || event.sessionId !== record.info.sessionId) {
      return;
    }
    this.#onEvent(this.#view(record), event);
  }

  #flushStartupEvents(record: RoleSessionRecord<TConfiguration>): void {
    const events = record.startupEvents.splice(0);
    for (const event of events) {
      this.#forwardEvent(record, event);
    }
  }

  #isCurrent(record: RoleSessionRecord<TConfiguration>): boolean {
    return record.runtimeGeneration === this.#runtimeGeneration &&
      this.#sessions.get(record.roleId) === record;
  }

  #view(record: RoleSessionRecord<TConfiguration>): RoleSessionView {
    return Object.freeze({
      roleId: record.roleId,
      displayName: record.displayName,
      scope: record.scope,
      runtimeGeneration: record.runtimeGeneration,
      sessionToken: record.sessionToken,
    });
  }

  #childToken(record: RoleChildRecord): RoleChildToken {
    return Object.freeze({
      resourceId: record.resourceId,
      resourceToken: record.resourceToken,
      kind: record.kind,
      parentRoleId: record.parentRoleId,
      runtimeGeneration: record.runtimeGeneration,
      parentSessionToken: record.parentSessionToken,
    });
  }

  #abortAllScopes(): void {
    for (const record of this.#sessions.values()) {
      record.controller.abort();
    }
    for (const child of this.#children.values()) {
      child.controller.abort();
    }
  }

  #matchesChild(
    child: RoleChildRecord | undefined,
    token: RoleChildToken,
  ): child is RoleChildRecord {
    return child !== undefined &&
      child.resourceToken === token.resourceToken &&
      child.kind === token.kind &&
      child.parentRoleId === token.parentRoleId &&
      child.runtimeGeneration === token.runtimeGeneration &&
      child.parentSessionToken === token.parentSessionToken;
  }

  #releaseRecordConfiguration(record: RoleSessionRecord<TConfiguration>): void {
    if (record.configuration === undefined || record.configurationReleased) {
      return;
    }
    record.configurationReleased = true;
    this.#tryReleaseConfiguration(record.configuration);
  }

  #tryReleaseConfiguration(configuration: TConfiguration): void {
    try {
      this.#releaseConfiguration(configuration);
    } catch {
      // Cleanup callbacks are not allowed to prevent adapter/child retirement.
    }
  }

  #tryUnsubscribe(unsubscribe: () => void): void {
    try {
      unsubscribe();
    } catch {
      // A broken adapter subscription must not strand the adapter or children.
    }
  }

  #detachRootAbortListener(): void {
    if (!this.#rootAbortListenerAttached) {
      return;
    }
    this.#rootStopSignal.removeEventListener("abort", this.#onRootAbort);
    this.#rootAbortListenerAttached = false;
  }

  #requestAdapterStop(adapter: RuntimeAdapter): void {
    void this.#stopAdapter(adapter).catch(() => undefined);
  }

  #stopAdapter(adapter: RuntimeAdapter): Promise<void> {
    const active = this.#adapterStopPromises.get(adapter);
    if (active !== undefined) {
      return active;
    }
    let resolveStop!: () => void;
    let rejectStop!: (error: unknown) => void;
    const stopPromise = new Promise<void>((resolve, reject) => {
      resolveStop = resolve;
      rejectStop = reject;
    });
    // Publish the single-flight promise before invoking external adapter code:
    // stop() is allowed to re-enter supervisor cleanup synchronously.
    this.#adapterStopPromises.set(adapter, stopPromise);
    try {
      Promise.resolve(adapter.stop()).then(resolveStop, rejectStop);
    } catch (error) {
      rejectStop(error);
    }
    void stopPromise.then(
      () => {
        if (this.#adapterStopPromises.get(adapter) === stopPromise) {
          this.#adapterStopPromises.delete(adapter);
        }
      },
      () => {
        if (this.#adapterStopPromises.get(adapter) === stopPromise) {
          this.#adapterStopPromises.delete(adapter);
        }
      },
    );
    return stopPromise;
  }

  #stopAdapterWithGrace(adapter: RuntimeAdapter, graceMs: number): Promise<boolean> {
    return withDeadline(
      this.#stopAdapter(adapter).then(
        () => true,
        () => false,
      ),
      graceMs,
      false,
    );
  }
}

function runtimeStoppedResult(commandId: string): RuntimeCommandResult {
  return {
    commandId,
    accepted: false,
    errorCode: "runtime_stopped",
    message: "Runtime is stopped",
  };
}

function raceWithAbort<T>(
  operation: Promise<T>,
  signal: AbortSignal,
): Promise<{ kind: "value"; value: T } | { kind: "aborted" }> {
  if (signal.aborted) {
    return Promise.resolve({ kind: "aborted" });
  }
  return new Promise((resolve, reject) => {
    const onAbort = (): void => {
      signal.removeEventListener("abort", onAbort);
      resolve({ kind: "aborted" });
    };
    signal.addEventListener("abort", onAbort, { once: true });
    operation.then(
      (value) => {
        signal.removeEventListener("abort", onAbort);
        resolve({ kind: "value", value });
      },
      (error: unknown) => {
        signal.removeEventListener("abort", onAbort);
        reject(error);
      },
    );
  });
}

async function withDeadline<T>(operation: Promise<T>, timeoutMs: number, fallback: T): Promise<T> {
  let timeout: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<T>((resolve) => {
    timeout = setTimeout(() => resolve(fallback), timeoutMs);
  });
  try {
    return await Promise.race([operation, deadline]);
  } finally {
    if (timeout !== undefined) {
      clearTimeout(timeout);
    }
  }
}
