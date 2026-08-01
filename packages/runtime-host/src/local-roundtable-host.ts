import { randomUUID } from "node:crypto";

import {
  PROTOCOL_VERSION,
  type CommandReceipt,
  type JsonObject,
  type MeetingCommand,
  type MeetingEvent,
  type MeetingEventKind,
  type RoleScope,
} from "@pi-roundtable/protocol";

import { PiRuntimeAdapter } from "./pi-runtime-adapter.js";
import type {
  RuntimeAdapter,
  RuntimeCommandResult,
  RuntimeEvent,
} from "./runtime-adapter.js";

export interface LocalRoundtableHostOptions {
  meetingId: string;
  runtimeId?: string;
  runtimeGeneration?: number;
  providerId: string;
  modelId: string;
  apiKey?: string;
  cwd?: string;
  now?: () => Date;
  adapterFactory?: (roleId: string) => RuntimeAdapter;
}

interface HostedRole {
  displayName: string;
  scope: RoleScope;
  adapter: RuntimeAdapter;
  unsubscribe: () => void;
}

interface PendingHandoff {
  interruptorId: string;
  targetId: string;
  message: string;
  commandId: string;
}

interface RememberedReceipt {
  fingerprint: string;
  receipt: CommandReceipt;
}

export type MeetingEventListener = (event: MeetingEvent) => void;
export type HostDiagnosticListener = (errorCode: string, message: string) => void;

export class LocalRoundtableHost {
  readonly #options: Omit<LocalRoundtableHostOptions, "apiKey">;
  readonly #runtimeId: string;
  readonly #runtimeGeneration: number;
  readonly #now: () => Date;
  readonly #roles = new Map<string, HostedRole>();
  readonly #receipts = new Map<string, RememberedReceipt>();
  readonly #eventListeners = new Set<MeetingEventListener>();
  readonly #diagnosticListeners = new Set<HostDiagnosticListener>();
  readonly #expectedTurnIds = new Map<string, string>();
  #phase: "created" | "live" | "closed" = "created";
  #sequence = 0;
  #leaseActive = false;
  #activeRoleId: string | undefined;
  #activeTurnCorrelationId: string | undefined;
  #pendingHandoff: PendingHandoff | undefined;
  #deferredTerminalEvents:
    | { roleId: string; events: RuntimeEvent[] }
    | undefined;
  #operationTail: Promise<void> = Promise.resolve();
  #apiKey: string | undefined;
  #stopped = false;

  constructor(options: LocalRoundtableHostOptions) {
    if (options.meetingId.length === 0) {
      throw new Error("meetingId is required");
    }
    if (options.providerId.length === 0 || options.modelId.length === 0) {
      throw new Error("providerId and modelId are required");
    }
    if (options.apiKey !== undefined && options.apiKey.length === 0) {
      throw new Error("apiKey is required");
    }
    const { apiKey, ...hostOptions } = options;
    this.#options = hostOptions;
    this.#apiKey = apiKey;
    this.#runtimeId = options.runtimeId ?? randomUUID();
    this.#runtimeGeneration = options.runtimeGeneration ?? 1;
    this.#now = options.now ?? (() => new Date());
  }

  get meetingId(): string {
    return this.#options.meetingId;
  }

  get runtimeId(): string {
    return this.#runtimeId;
  }

  get runtimeGeneration(): number {
    return this.#runtimeGeneration;
  }

  get sequence(): number {
    return this.#sequence;
  }

  subscribe(listener: MeetingEventListener): () => void {
    this.#eventListeners.add(listener);
    return () => this.#eventListeners.delete(listener);
  }

  subscribeDiagnostics(listener: HostDiagnosticListener): () => void {
    this.#diagnosticListeners.add(listener);
    return () => this.#diagnosticListeners.delete(listener);
  }

  initializeCredential(apiKey: string): void {
    if (apiKey.length === 0) {
      throw new Error("apiKey is required");
    }
    if (this.#leaseActive || this.#stopped || this.#apiKey !== undefined) {
      throw new Error("Runtime credential is already initialized");
    }
    this.#apiKey = apiKey;
  }

  start(): void {
    if (this.#leaseActive || this.#stopped) {
      throw new Error("Local Roundtable Host cannot be started again");
    }
    if (this.#apiKey === undefined) {
      throw new Error("Runtime credential is not initialized");
    }
    this.#leaseActive = true;
    this.#emit("runtime.lease_acquired", this.#runtimeId, null, null, {});
  }

  execute(command: MeetingCommand): Promise<CommandReceipt> {
    return this.#enqueueOperation(() => this.#executeSerialized(command));
  }

  async #executeSerialized(command: MeetingCommand): Promise<CommandReceipt> {
    const fingerprint = JSON.stringify(command);
    const remembered = this.#receipts.get(command.commandId);
    if (remembered !== undefined) {
      if (remembered.fingerprint !== fingerprint) {
        return this.#receipt(
          command,
          "rejected",
          "command_id_conflict",
          "The command ID was already used with different content",
        );
      }
      return { ...remembered.receipt, status: "duplicate" };
    }

    const validation = this.#validateEnvelope(command);
    if (validation !== undefined) {
      return this.#remember(command, fingerprint, validation);
    }

    let receipt: CommandReceipt;
    try {
      receipt = await this.#executeNew(command);
    } catch {
      receipt = this.#receipt(
        command,
        "rejected",
        "host_execution_failed",
        "Local Runtime Host could not execute the command",
      );
    }
    return this.#remember(command, fingerprint, receipt);
  }

  stop(): Promise<void> {
    return this.#enqueueOperation(() => this.#stopNow());
  }

  async #stopNow(): Promise<void> {
    if (this.#stopped) {
      return;
    }
    this.#stopped = true;
    this.#pendingHandoff = undefined;
    this.#activeRoleId = undefined;
    this.#activeTurnCorrelationId = undefined;
    this.#expectedTurnIds.clear();
    const roles = [...this.#roles.values()];
    this.#roles.clear();
    await Promise.all(
      roles.map(async (role) => {
        role.unsubscribe();
        try {
          await role.adapter.stop();
        } catch {
          this.#diagnose("role_stop_failed", "A role runtime did not stop cleanly");
        }
      }),
    );
    if (this.#phase === "live") {
      this.#phase = "closed";
      this.#emit("meeting.closed", this.#runtimeId, null, null, {});
    }
    if (this.#leaseActive) {
      this.#leaseActive = false;
      this.#emit("runtime.lease_released", this.#runtimeId, null, null, {});
    }
    this.#apiKey = undefined;
  }

  #validateEnvelope(command: MeetingCommand): CommandReceipt | undefined {
    if (command.protocolVersion !== PROTOCOL_VERSION) {
      return this.#receipt(
        command,
        "rejected",
        "unsupported_protocol",
        `Expected protocol version ${PROTOCOL_VERSION}`,
      );
    }
    if (command.meetingId !== this.meetingId) {
      return this.#receipt(command, "rejected", "meeting_mismatch", "Meeting ID mismatch");
    }
    if (command.runtimeGeneration !== this.runtimeGeneration) {
      return this.#receipt(
        command,
        "rejected",
        "runtime_generation_mismatch",
        "Command does not carry the active runtime generation",
      );
    }
    if (
      command.expectedSequence !== undefined &&
      command.expectedSequence !== null &&
      command.expectedSequence !== this.#sequence
    ) {
      return this.#receipt(
        command,
        "rejected",
        "sequence_mismatch",
        `Expected sequence ${this.#sequence}`,
      );
    }
    if (!this.#leaseActive || this.#stopped) {
      return this.#receipt(command, "rejected", "runtime_stopped", "Runtime is stopped");
    }
    return undefined;
  }

  async #executeNew(command: MeetingCommand): Promise<CommandReceipt> {
    switch (command.kind) {
      case "meeting.open":
        if (this.#phase !== "created") {
          return this.#invalidTransition(command);
        }
        this.#phase = "live";
        this.#emit("meeting.opened", this.#runtimeId, null, command.commandId, {});
        return this.#accepted(command);

      case "meeting.close":
        if (this.#phase !== "live") {
          return this.#invalidTransition(command);
        }
        this.#phase = "closed";
        this.#activeRoleId = undefined;
        this.#activeTurnCorrelationId = undefined;
        this.#pendingHandoff = undefined;
        this.#expectedTurnIds.clear();
        await this.#stopAllRoles();
        this.#emit("meeting.closed", this.#runtimeId, null, command.commandId, {});
        return this.#accepted(command);

      case "role.add":
        return this.#addRole(command, "long_term", "role.registered");

      case "role.create_temporary":
        return this.#addRole(command, "temporary", "role.temporary_registered");

      case "role.promote":
        return this.#promoteRole(command);

      case "role.archive":
        return this.#removeRole(command, true);

      case "role.remove":
        return this.#removeRole(command, false);

      case "speech.prompt":
        return this.#promptRole(command);

      case "speech.interrupt":
        return this.#interrupt(command);

      case "generation.cancel":
        return this.#cancel(command);

      case "subagent.spawn":
      case "tool.invoke":
        return this.#receipt(
          command,
          "rejected",
          "unsupported_command",
          "Tools and subagents require an explicit capability policy",
        );
    }
  }

  async #addRole(
    command: MeetingCommand,
    scope: RoleScope,
    eventKind: "role.registered" | "role.temporary_registered",
  ): Promise<CommandReceipt> {
    if (this.#phase === "closed") {
      return this.#invalidTransition(command);
    }
    const roleId = command.actorId;
    if (roleId === undefined || roleId === null || roleId.length === 0) {
      return this.#receipt(command, "rejected", "invalid_role", "actorId is required");
    }
    if (this.#roles.has(roleId)) {
      return this.#receipt(command, "rejected", "duplicate_role", "Role already exists");
    }
    const displayName = this.#readString(command.payload, "displayName") ?? roleId;
    const adapter = this.#createAdapter(roleId);
    const unsubscribe = adapter.subscribe((event) => this.#onRuntimeEvent(roleId, event));
    try {
      await adapter.start();
    } catch {
      unsubscribe();
      try {
        await adapter.stop();
      } catch {
        // The stable receipt below is the public failure surface.
      }
      return this.#receipt(
        command,
        "rejected",
        "role_runtime_failed",
        "The role runtime could not be started",
      );
    }
    this.#roles.set(roleId, { displayName, scope, adapter, unsubscribe });
    this.#emit(eventKind, roleId, null, command.commandId, { displayName, scope });
    return this.#accepted(command);
  }

  #promoteRole(command: MeetingCommand): CommandReceipt {
    const roleId = command.actorId;
    const role = roleId === undefined || roleId === null ? undefined : this.#roles.get(roleId);
    if (roleId === undefined || roleId === null || role === undefined) {
      return this.#receipt(command, "rejected", "unknown_role", "Role does not exist");
    }
    if (role.scope !== "temporary") {
      return this.#receipt(
        command,
        "rejected",
        "role_not_temporary",
        "Only a temporary role can be promoted",
      );
    }
    role.scope = "long_term";
    this.#emit("role.promoted", roleId, null, command.commandId, {});
    return this.#accepted(command);
  }

  async #removeRole(command: MeetingCommand, archive: boolean): Promise<CommandReceipt> {
    const roleId = command.actorId;
    const role = roleId === undefined || roleId === null ? undefined : this.#roles.get(roleId);
    if (roleId === undefined || roleId === null || role === undefined) {
      return this.#receipt(command, "rejected", "unknown_role", "Role does not exist");
    }
    if (this.#activeRoleId === roleId) {
      await role.adapter.execute({
        kind: "turn.cancel",
        commandId: `${command.commandId}:archive-cancel`,
        roleId,
      });
      this.#activeRoleId = undefined;
      this.#activeTurnCorrelationId = undefined;
    }
    this.#expectedTurnIds.delete(roleId);
    role.unsubscribe();
    try {
      await role.adapter.stop();
    } catch {
      this.#diagnose("role_stop_failed", "The role runtime did not stop cleanly");
    }
    this.#roles.delete(roleId);
    this.#emit(archive ? "role.archived" : "role.left", roleId, null, command.commandId, {
      displayName: role.displayName,
      scope: role.scope,
    });
    return this.#accepted(command);
  }

  async #promptRole(command: MeetingCommand): Promise<CommandReceipt> {
    if (this.#phase !== "live") {
      return this.#invalidTransition(command);
    }
    const roleId = command.actorId;
    const role = roleId === undefined || roleId === null ? undefined : this.#roles.get(roleId);
    const message = this.#readString(command.payload, "message");
    if (roleId === undefined || roleId === null || role === undefined) {
      return this.#receipt(command, "rejected", "unknown_role", "Role does not exist");
    }
    if (message === undefined || message.length === 0) {
      return this.#receipt(command, "rejected", "invalid_prompt", "Prompt message is required");
    }
    if (this.#activeRoleId !== undefined && this.#activeRoleId !== roleId) {
      return this.#receipt(command, "rejected", "floor_busy", "Another role is speaking");
    }
    this.#expectedTurnIds.set(roleId, command.commandId);
    const result = await role.adapter.execute({
      kind: "turn.prompt",
      commandId: command.commandId,
      roleId,
      message,
      delivery: "immediate",
    });
    if (!result.accepted && this.#expectedTurnIds.get(roleId) === command.commandId) {
      this.#expectedTurnIds.delete(roleId);
    }
    return this.#fromRuntimeResult(command, result);
  }

  async #interrupt(command: MeetingCommand): Promise<CommandReceipt> {
    if (this.#phase !== "live") {
      return this.#invalidTransition(command);
    }
    const interruptorId = command.actorId;
    const targetId = command.targetId ?? this.#activeRoleId;
    const message = this.#readString(command.payload, "message");
    if (
      interruptorId === undefined ||
      interruptorId === null ||
      !this.#roles.has(interruptorId)
    ) {
      return this.#receipt(command, "rejected", "unknown_role", "Interruptor does not exist");
    }
    if (targetId === undefined || targetId !== this.#activeRoleId) {
      return this.#receipt(command, "rejected", "invalid_target", "Target is not speaking");
    }
    if (interruptorId === targetId) {
      return this.#receipt(command, "rejected", "invalid_role", "A role cannot interrupt itself");
    }
    if (message === undefined || message.length === 0) {
      return this.#receipt(
        command,
        "rejected",
        "invalid_prompt",
        "An interruption requires the next prompt",
      );
    }
    const target = this.#roles.get(targetId);
    if (target === undefined) {
      return this.#receipt(command, "rejected", "unknown_role", "Target role does not exist");
    }
    this.#pendingHandoff = { interruptorId, targetId, message, commandId: command.commandId };
    const deferred = { roleId: targetId, events: [] as RuntimeEvent[] };
    this.#deferredTerminalEvents = deferred;
    let result: RuntimeCommandResult;
    try {
      result = await target.adapter.execute({
        kind: "turn.cancel",
        commandId: `${command.commandId}:cancel`,
        roleId: targetId,
      });
    } catch (error) {
      this.#deferredTerminalEvents = undefined;
      this.#pendingHandoff = undefined;
      throw error;
    }
    this.#deferredTerminalEvents = undefined;
    if (!result.accepted && deferred.events.length === 0) {
      this.#pendingHandoff = undefined;
      return this.#fromRuntimeResult(command, result);
    }
    this.#emit(
      "interruption.requested",
      interruptorId,
      targetId,
      command.commandId,
      {},
    );
    for (const event of deferred.events) {
      this.#onRuntimeEvent(targetId, event);
    }
    if (!result.accepted) {
      this.#diagnose(
        this.#safeRuntimeErrorCode(result.errorCode),
        "The role runtime reported a cancellation failure after ending the turn",
      );
    }
    return this.#accepted(command);
  }

  async #cancel(command: MeetingCommand): Promise<CommandReceipt> {
    const roleId = command.targetId ?? command.actorId ?? this.#activeRoleId;
    const role = roleId === undefined ? undefined : this.#roles.get(roleId);
    if (roleId === undefined || role === undefined) {
      return this.#receipt(command, "rejected", "unknown_role", "Role does not exist");
    }
    const result = await role.adapter.execute({
      kind: "turn.cancel",
      commandId: command.commandId,
      roleId,
    });
    if (result.accepted) {
      this.#expectedTurnIds.delete(roleId);
    }
    return this.#fromRuntimeResult(command, result);
  }

  async #continueHandoff(completedRoleId: string): Promise<void> {
    const handoff = this.#pendingHandoff;
    if (handoff === undefined || handoff.targetId !== completedRoleId) {
      return;
    }
    this.#pendingHandoff = undefined;
    const interruptor = this.#roles.get(handoff.interruptorId);
    if (interruptor === undefined || this.#phase !== "live") {
      return;
    }
    this.#expectedTurnIds.set(handoff.interruptorId, handoff.commandId);
    const result = await interruptor.adapter.execute({
      kind: "turn.prompt",
      commandId: handoff.commandId,
      roleId: handoff.interruptorId,
      message: handoff.message,
      delivery: "immediate",
    });
    if (!result.accepted) {
      if (this.#expectedTurnIds.get(handoff.interruptorId) === handoff.commandId) {
        this.#expectedTurnIds.delete(handoff.interruptorId);
      }
      this.#diagnose(
        this.#safeRuntimeErrorCode(result.errorCode ?? "handoff_failed"),
        "The interrupting role could not take the floor",
      );
    }
  }

  #onRuntimeEvent(roleId: string, event: RuntimeEvent): void {
    if (
      this.#deferredTerminalEvents?.roleId === roleId &&
      (event.kind === "turn.completed" || event.kind === "turn.cancelled")
    ) {
      this.#deferredTerminalEvents.events.push(event);
      return;
    }

    switch (event.kind) {
      case "turn.started": {
        const expectedCorrelationId = this.#expectedTurnIds.get(roleId);
        if (
          expectedCorrelationId === undefined ||
          this.#activeRoleId !== undefined ||
          (event.correlationId !== undefined &&
            event.correlationId !== null &&
            expectedCorrelationId !== undefined &&
            event.correlationId !== expectedCorrelationId)
        ) {
          return;
        }
        this.#expectedTurnIds.delete(roleId);
        this.#activeRoleId = roleId;
        this.#activeTurnCorrelationId = event.correlationId ?? expectedCorrelationId;
        this.#emit("speech.started", roleId, null, event.correlationId ?? null, {});
        break;
      }
      case "turn.delta":
        if (this.#isActiveTurnEvent(roleId, event)) {
          this.#emit("speech.delta", roleId, null, event.correlationId ?? null, event.payload);
        }
        break;
      case "turn.completed":
      case "turn.cancelled": {
        if (!this.#isActiveTurnEvent(roleId, event)) {
          return;
        }
        const kind = event.kind === "turn.completed" ? "speech.completed" : "speech.cancelled";
        const handoff = this.#pendingHandoff;
        this.#emit(
          kind,
          event.kind === "turn.cancelled" && handoff?.targetId === roleId
            ? handoff.interruptorId
            : roleId,
          event.kind === "turn.cancelled" ? roleId : null,
          event.correlationId ?? null,
          {},
        );
        this.#activeRoleId = undefined;
        this.#activeTurnCorrelationId = undefined;
        this.#enqueueInternal(() => this.#continueHandoff(roleId));
        break;
      }
      case "tool.started":
        this.#emit("tool.started", roleId, null, event.correlationId ?? null, event.payload);
        break;
      case "tool.completed":
        this.#emit("tool.completed", roleId, null, event.correlationId ?? null, event.payload);
        break;
      case "tool.failed":
        this.#emit("tool.failed", roleId, null, event.correlationId ?? null, event.payload);
        break;
      case "runtime.failed":
        this.#diagnose(
          this.#safeRuntimeErrorCode(
            typeof event.payload.errorCode === "string"
              ? event.payload.errorCode
              : "runtime_failed",
          ),
          "The role runtime reported an error. Check provider, model, and credential settings.",
        );
        break;
      default:
        break;
    }
  }

  #createAdapter(roleId: string): RuntimeAdapter {
    if (this.#options.adapterFactory !== undefined) {
      return this.#options.adapterFactory(roleId);
    }
    const options = {
      runtimeId: `${this.#runtimeId}:${roleId}`,
      roleId,
      providerId: this.#options.providerId,
      modelId: this.#options.modelId,
      tools: [],
      credentialProvider: { resolveApiKey: async () => this.#apiKey },
    };
    return new PiRuntimeAdapter(
      this.#options.cwd === undefined ? options : { ...options, cwd: this.#options.cwd },
    );
  }

  #isActiveTurnEvent(roleId: string, event: RuntimeEvent): boolean {
    return (
      this.#activeRoleId === roleId &&
      (event.correlationId === undefined ||
        event.correlationId === null ||
        this.#activeTurnCorrelationId === undefined ||
        event.correlationId === this.#activeTurnCorrelationId)
    );
  }

  #enqueueOperation<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.#operationTail.then(operation);
    this.#operationTail = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  #enqueueInternal(operation: () => Promise<void>): void {
    void this.#enqueueOperation(operation).catch(() => {
      this.#diagnose("internal_operation_failed", "The local host could not continue the meeting flow");
    });
  }

  async #stopAllRoles(): Promise<void> {
    const roles = [...this.#roles.values()];
    this.#roles.clear();
    this.#expectedTurnIds.clear();
    await Promise.all(
      roles.map(async (role) => {
        role.unsubscribe();
        try {
          await role.adapter.stop();
        } catch {
          this.#diagnose("role_stop_failed", "A role runtime did not stop cleanly");
        }
      }),
    );
  }

  #emit(
    kind: MeetingEventKind,
    actorId: string | null,
    targetId: string | null,
    causationId: string | null,
    payload: JsonObject,
  ): MeetingEvent {
    const event: MeetingEvent = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId: this.meetingId,
      eventId: randomUUID(),
      sequence: ++this.#sequence,
      runtimeGeneration: this.runtimeGeneration,
      kind,
      occurredAt: this.#now().toISOString(),
      payload,
    };
    if (actorId !== null) {
      event.actorId = actorId;
    }
    if (targetId !== null) {
      event.targetId = targetId;
    }
    if (causationId !== null) {
      event.causationId = causationId;
    }
    for (const listener of this.#eventListeners) {
      try {
        listener(event);
      } catch {
        // Presentation and transport listeners cannot corrupt authoritative state.
      }
    }
    return event;
  }

  #accepted(command: MeetingCommand): CommandReceipt {
    return this.#receipt(command, "accepted", null, null, this.#sequence);
  }

  #invalidTransition(command: MeetingCommand): CommandReceipt {
    return this.#receipt(
      command,
      "rejected",
      "invalid_transition",
      `Command ${command.kind} is not valid while meeting is ${this.#phase}`,
    );
  }

  #fromRuntimeResult(
    command: MeetingCommand,
    result: RuntimeCommandResult,
  ): CommandReceipt {
    return result.accepted
      ? this.#accepted(command)
      : this.#receipt(
          command,
          "rejected",
          this.#safeRuntimeErrorCode(result.errorCode),
          "The role runtime rejected the command",
        );
  }

  #receipt(
    command: MeetingCommand,
    status: CommandReceipt["status"],
    errorCode: string | null,
    message: string | null,
    sequence?: number,
  ): CommandReceipt {
    const receipt: CommandReceipt = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId: this.meetingId,
      commandId: command.commandId,
      status,
      acknowledgedAt: this.#now().toISOString(),
    };
    if (errorCode !== null) {
      receipt.errorCode = errorCode;
    }
    if (message !== null) {
      receipt.message = message;
    }
    if (sequence !== undefined) {
      receipt.sequence = sequence;
    }
    return receipt;
  }

  #remember(
    command: MeetingCommand,
    fingerprint: string,
    receipt: CommandReceipt,
  ): CommandReceipt {
    if (this.#receipts.size >= 2_048) {
      const oldest = this.#receipts.keys().next().value as string | undefined;
      if (oldest !== undefined) {
        this.#receipts.delete(oldest);
      }
    }
    this.#receipts.set(command.commandId, { fingerprint, receipt });
    return receipt;
  }

  #readString(payload: JsonObject, key: string): string | undefined {
    const value = payload[key];
    return typeof value === "string" ? value : undefined;
  }

  #safeRuntimeErrorCode(errorCode: string | null | undefined): string {
    return errorCode !== undefined &&
      errorCode !== null &&
      /^[a-z0-9][a-z0-9_.-]{0,63}$/u.test(errorCode)
      ? errorCode
      : "runtime_rejected";
  }

  #diagnose(errorCode: string, message: string): void {
    for (const listener of this.#diagnosticListeners) {
      try {
        listener(errorCode, message);
      } catch {
        // Diagnostics are observational and must not alter host state.
      }
    }
  }
}
