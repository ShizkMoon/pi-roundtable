import { randomUUID } from "node:crypto";
import { existsSync, realpathSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { basename, isAbsolute, relative, resolve, sep } from "node:path";

import {
  PROTOCOL_VERSION,
  validateRoundtableSession,
  type CommandReceipt,
  type JsonObject,
  type MeetingCommand,
  type MeetingEvent,
  type MeetingEventKind,
  type RoleScope,
  type ParticipantManifest,
  type RoundtableSession,
  type WorkspaceProfile,
} from "@pi-roundtable/protocol";

import { PiRuntimeAdapter } from "./pi-runtime-adapter.js";
import {
  validateRemoteMcpEndpoint,
  type ResolvedMcpServerRuntimeConfiguration,
} from "./mcp-client-manager.js";
import type {
  RuntimeAdapter,
  RuntimeCommandResult,
  RuntimeEvent,
} from "./runtime-adapter.js";

export interface LocalRoundtableHostOptions {
  meetingId: string;
  runtimeId?: string;
  runtimeGeneration?: number;
  cwd?: string;
  catalogSkillRoot?: string;
  catalogMcpRoot?: string;
  now?: () => Date;
  adapterFactory?: (roleId: string, configuration?: ResolvedRoleRuntimeConfiguration) => RuntimeAdapter;
}

export interface ResolvedRoleRuntimeConfiguration {
  displayName: string;
  providerId: string;
  modelId: string;
  apiKey: string;
  systemPrompt: string;
  skillPaths: string[];
  mcpServers: ResolvedMcpServerRuntimeConfiguration[];
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

interface ExpectedTurn {
  commandId: string;
  visibility: "public" | "private";
}

interface PendingPublicTurn {
  roleId: string;
  commandId: string;
}

interface PublicHostMessage {
  message: string;
  mentions: string[];
}

interface RememberedReceipt {
  fingerprint: string;
  receipt: CommandReceipt;
}

export type MeetingEventListener = (event: MeetingEvent) => void;
export type HostDiagnosticListener = (errorCode: string, message: string) => void;

export class LocalRoundtableHost {
  readonly #options: LocalRoundtableHostOptions;
  readonly #runtimeId: string;
  readonly #runtimeGeneration: number;
  readonly #now: () => Date;
  readonly #roles = new Map<string, HostedRole>();
  readonly #receipts = new Map<string, RememberedReceipt>();
  readonly #eventListeners = new Set<MeetingEventListener>();
  readonly #diagnosticListeners = new Set<HostDiagnosticListener>();
  readonly #expectedTurns = new Map<string, ExpectedTurn>();
  readonly #pendingPublicTurns: PendingPublicTurn[] = [];
  readonly #publicMessages: PublicHostMessage[] = [];
  readonly #rolePublicCursors = new Map<string, number>();
  #phase: "created" | "live" | "closed" = "created";
  #sequence = 0;
  #leaseActive = false;
  #activeRoleId: string | undefined;
  #activeTurnCorrelationId: string | undefined;
  #activeTurnVisibility: "public" | "private" = "public";
  #pendingHandoff: PendingHandoff | undefined;
  #deferredTerminalEvents:
    | { roleId: string; events: RuntimeEvent[] }
    | undefined;
  #operationTail: Promise<void> = Promise.resolve();
  #workspace: WorkspaceProfile | undefined;
  #session: RoundtableSession | undefined;
  #credentials = new Map<string, string>();
  #runtimeConfigurationInitialized = false;
  #stopped = false;

  constructor(options: LocalRoundtableHostOptions) {
    if (options.meetingId.length === 0) {
      throw new Error("meetingId is required");
    }
    this.#options = options;
    this.#runtimeId = options.runtimeId ?? randomUUID();
    const runtimeGeneration = options.runtimeGeneration ?? 1;
    if (!Number.isSafeInteger(runtimeGeneration) || runtimeGeneration < 1) {
      throw new RangeError("runtimeGeneration must be a positive safe integer");
    }
    this.#runtimeGeneration = runtimeGeneration;
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

  initializeRuntimeConfiguration(
    workspace: WorkspaceProfile,
    session: RoundtableSession,
    credentials: Readonly<Record<string, string>>,
  ): void {
    if (this.#leaseActive || this.#stopped || this.#runtimeConfigurationInitialized) {
      throw new Error("Runtime configuration is already initialized");
    }
    if (session.sessionId !== this.meetingId || session.workspaceId !== workspace.workspaceId) {
      throw new Error("Runtime session does not match the meeting or workspace");
    }
    this.#workspace = structuredClone(workspace);
    this.#session = structuredClone(session);
    this.#credentials = new Map(Object.entries(credentials));
    this.#runtimeConfigurationInitialized = true;
  }

  start(): void {
    if (this.#leaseActive || this.#stopped) {
      throw new Error("Local Roundtable Host cannot be started again");
    }
    if (this.#options.adapterFactory === undefined && !this.#runtimeConfigurationInitialized) {
      throw new Error("Runtime configuration is not initialized");
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
    this.#expectedTurns.clear();
    this.#pendingPublicTurns.length = 0;
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
    this.#credentials.clear();
    this.#workspace = undefined;
    this.#session = undefined;
    this.#runtimeConfigurationInitialized = false;
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
        this.#expectedTurns.clear();
        this.#pendingPublicTurns.length = 0;
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

      case "speech.broadcast":
        return this.#broadcast(command);

      case "speech.direct":
        return this.#direct(command);

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
    let configuration: ResolvedRoleRuntimeConfiguration | undefined;
    if (
      this.#workspace !== undefined ||
      this.#session !== undefined ||
      this.#options.adapterFactory === undefined ||
      this.#readObject(command.payload, "participantManifest") !== undefined
    ) {
      try {
        configuration = this.#resolveRoleRuntimeConfiguration(command.payload, roleId, scope);
      } catch {
        return this.#receipt(
          command,
          "rejected",
          "invalid_role_manifest",
          "The participant manifest could not be resolved against the runtime workspace",
        );
      }
    }
    const displayName = configuration?.displayName ?? this.#readString(command.payload, "displayName") ?? roleId;
    const adapter = this.#createAdapter(roleId, configuration);
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
    this.#rolePublicCursors.set(roleId, 0);
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
    this.#expectedTurns.delete(roleId);
    this.#rolePublicCursors.delete(roleId);
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
    this.#expectedTurns.set(roleId, { commandId: command.commandId, visibility: "public" });
    const result = await role.adapter.execute({
      kind: "turn.prompt",
      commandId: command.commandId,
      roleId,
      message: this.#withUnseenPublicContext(roleId, message),
      delivery: "immediate",
    });
    if (!result.accepted && this.#expectedTurns.get(roleId)?.commandId === command.commandId) {
      this.#expectedTurns.delete(roleId);
    }
    return this.#fromRuntimeResult(command, result);
  }

  async #broadcast(command: MeetingCommand): Promise<CommandReceipt> {
    if (this.#phase !== "live") {
      return this.#invalidTransition(command);
    }
    if (command.actorId !== "user.direct_host") {
      return this.#receipt(command, "rejected", "invalid_actor", "Broadcasts require the direct meeting host");
    }
    if (this.#activeRoleId !== undefined || this.#pendingPublicTurns.length > 0) {
      return this.#receipt(command, "rejected", "floor_busy", "The public floor is busy");
    }
    const message = this.#readString(command.payload, "message")?.trim();
    if (!message) {
      return this.#receipt(command, "rejected", "invalid_prompt", "Broadcast message is required");
    }
    const requestedMentions = this.#readStringArray(command.payload, "mentions");
    if (requestedMentions === undefined) {
      return this.#receipt(command, "rejected", "invalid_mentions", "Mentions must be an array of role identifiers");
    }
    const mentions = [...new Set(requestedMentions)];
    if (mentions.some((roleId) => !this.#roles.has(roleId))) {
      return this.#receipt(command, "rejected", "unknown_role", "A mentioned role does not exist");
    }
    const targets = mentions.length > 0 ? mentions : [...this.#roles.keys()];
    this.#publicMessages.push({ message, mentions });
    this.#emit(
      "message.published",
      "user.direct_host",
      null,
      command.commandId,
      { message, mentions },
    );
    this.#pendingPublicTurns.push(
      ...targets.map((roleId, index) => ({ roleId, commandId: `${command.commandId}:${index + 1}` })),
    );
    await this.#startNextPublicTurn();
    return this.#accepted(command);
  }

  async #direct(command: MeetingCommand): Promise<CommandReceipt> {
    if (this.#phase !== "live") {
      return this.#invalidTransition(command);
    }
    if (command.actorId !== "user.direct_host") {
      return this.#receipt(command, "rejected", "invalid_actor", "Direct messages require the direct meeting host");
    }
    const roleId = command.targetId;
    const role = roleId === undefined || roleId === null ? undefined : this.#roles.get(roleId);
    const message = this.#readString(command.payload, "message")?.trim();
    if (roleId === undefined || roleId === null || role === undefined) {
      return this.#receipt(command, "rejected", "unknown_role", "Private target role does not exist");
    }
    if (!message) {
      return this.#receipt(command, "rejected", "invalid_prompt", "Private message is required");
    }
    if (this.#activeRoleId !== undefined || this.#pendingPublicTurns.length > 0) {
      return this.#receipt(command, "rejected", "floor_busy", "A role is already responding");
    }
    const audience = ["user.direct_host", roleId];
    this.#emit(
      "message.direct_sent",
      "user.direct_host",
      roleId,
      command.commandId,
      { message },
      "private",
      audience,
    );
    this.#expectedTurns.set(roleId, { commandId: command.commandId, visibility: "private" });
    const result = await role.adapter.execute({
      kind: "turn.prompt",
      commandId: command.commandId,
      roleId,
      message: this.#withUnseenPublicContext(
        roleId,
        `[Private message from the meeting host. Do not reveal it to other roles unless the host explicitly republishes it.]\n${message}`,
      ),
      delivery: "immediate",
    });
    if (!result.accepted && this.#expectedTurns.get(roleId)?.commandId === command.commandId) {
      this.#expectedTurns.delete(roleId);
      this.#diagnose(this.#safeRuntimeErrorCode(result.errorCode), "The target role could not answer the private message");
    }
    return this.#accepted(command);
  }

  async #startNextPublicTurn(): Promise<void> {
    if (this.#activeRoleId !== undefined || this.#phase !== "live") {
      return;
    }
    while (this.#pendingPublicTurns.length > 0) {
      const next = this.#pendingPublicTurns.shift();
      if (next === undefined) {
        return;
      }
      const role = this.#roles.get(next.roleId);
      if (role === undefined) {
        continue;
      }
      this.#expectedTurns.set(next.roleId, { commandId: next.commandId, visibility: "public" });
      const result = await role.adapter.execute({
        kind: "turn.prompt",
        commandId: next.commandId,
        roleId: next.roleId,
        message: this.#withUnseenPublicContext(
          next.roleId,
          "Respond to the latest public roundtable message. Keep private conversations private.",
        ),
        delivery: "immediate",
      });
      if (result.accepted) {
        return;
      }
      this.#expectedTurns.delete(next.roleId);
      this.#diagnose(this.#safeRuntimeErrorCode(result.errorCode), "A mentioned role could not take the public floor");
    }
  }

  #withUnseenPublicContext(roleId: string, instruction: string): string {
    const cursor = this.#rolePublicCursors.get(roleId) ?? 0;
    const unseen = this.#publicMessages.slice(cursor);
    this.#rolePublicCursors.set(roleId, this.#publicMessages.length);
    if (unseen.length === 0) {
      return instruction;
    }
    const context = unseen.map((entry) => {
      const mentionLabel = entry.mentions.length === 0
        ? "addressed to the full roundtable"
        : `mentions: ${entry.mentions.join(", ")}`;
      return `[Public host message; ${mentionLabel}]\n${entry.message}`;
    }).join("\n\n");
    return `${context}\n\n${instruction}`;
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
    this.#pendingPublicTurns.length = 0;
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
      this.#expectedTurns.delete(roleId);
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
    this.#expectedTurns.set(handoff.interruptorId, {
      commandId: handoff.commandId,
      visibility: "public",
    });
    const result = await interruptor.adapter.execute({
      kind: "turn.prompt",
      commandId: handoff.commandId,
      roleId: handoff.interruptorId,
      message: this.#withUnseenPublicContext(handoff.interruptorId, handoff.message),
      delivery: "immediate",
    });
    if (!result.accepted) {
      if (this.#expectedTurns.get(handoff.interruptorId)?.commandId === handoff.commandId) {
        this.#expectedTurns.delete(handoff.interruptorId);
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
        const expectedTurn = this.#expectedTurns.get(roleId);
        const expectedCorrelationId = expectedTurn?.commandId;
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
        this.#expectedTurns.delete(roleId);
        this.#activeRoleId = roleId;
        this.#activeTurnCorrelationId = event.correlationId ?? expectedCorrelationId;
        this.#activeTurnVisibility = expectedTurn?.visibility ?? "public";
        this.#emitActiveTurnEvent("speech.started", roleId, event, {});
        break;
      }
      case "turn.delta":
        if (this.#isActiveTurnEvent(roleId, event)) {
          this.#emitActiveTurnEvent("speech.delta", roleId, event, event.payload);
        }
        break;
      case "turn.completed":
      case "turn.cancelled": {
        if (!this.#isActiveTurnEvent(roleId, event)) {
          return;
        }
        const kind = event.kind === "turn.completed" ? "speech.completed" : "speech.cancelled";
        const handoff = this.#pendingHandoff;
        this.#emitActiveTurnEvent(
          kind,
          event.kind === "turn.cancelled" && handoff?.targetId === roleId
            ? handoff.interruptorId
            : roleId,
          event,
          {},
          event.kind === "turn.cancelled" ? roleId : null,
        );
        const completedVisibility = this.#activeTurnVisibility;
        this.#activeRoleId = undefined;
        this.#activeTurnCorrelationId = undefined;
        this.#activeTurnVisibility = "public";
        this.#enqueueInternal(() => this.#continueHandoff(roleId));
        if (completedVisibility === "public") {
          this.#enqueueInternal(() => this.#startNextPublicTurn());
        }
        break;
      }
      case "tool.started":
        this.#emitActiveTurnEvent("tool.started", roleId, event, event.payload);
        break;
      case "tool.completed":
        this.#emitActiveTurnEvent("tool.completed", roleId, event, event.payload);
        break;
      case "tool.failed":
        this.#emitActiveTurnEvent("tool.failed", roleId, event, event.payload);
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

  #emitActiveTurnEvent(
    kind: MeetingEventKind,
    actorId: string,
    event: RuntimeEvent,
    payload: JsonObject,
    publicTargetId: string | null = null,
  ): void {
    const isPrivate = this.#activeTurnVisibility === "private";
    this.#emit(
      kind,
      actorId,
      isPrivate ? "user.direct_host" : publicTargetId,
      event.correlationId ?? null,
      payload,
      isPrivate ? "private" : "public",
      isPrivate ? ["user.direct_host", actorId] : undefined,
    );
  }

  #createAdapter(
    roleId: string,
    configuration: ResolvedRoleRuntimeConfiguration | undefined,
  ): RuntimeAdapter {
    if (this.#options.adapterFactory !== undefined) {
      return this.#options.adapterFactory(roleId, configuration);
    }
    if (configuration === undefined) {
      throw new Error("Resolved role runtime configuration is required");
    }
    const options = {
      runtimeId: `${this.#runtimeId}:${roleId}`,
      roleId,
      providerId: configuration.providerId,
      modelId: configuration.modelId,
      tools: [],
      systemPrompt: configuration.systemPrompt,
      skillPaths: configuration.skillPaths,
      mcpServers: configuration.mcpServers,
      credentialProvider: {
        resolveApiKey: async (providerId: string) =>
          providerId === configuration.providerId ? configuration.apiKey : undefined,
      },
    };
    return new PiRuntimeAdapter(
      this.#options.cwd === undefined ? options : { ...options, cwd: this.#options.cwd },
    );
  }

  #resolveRoleRuntimeConfiguration(
    payload: JsonObject,
    roleId: string,
    scope: RoleScope,
  ): ResolvedRoleRuntimeConfiguration {
    const workspace = this.#workspace;
    let session = this.#session;
    if (workspace === undefined || session === undefined) {
      throw new Error("Workspace and session are required");
    }
    let participant = session.participants.find(
      (candidate) => candidate.participantId === roleId,
    );
    if (participant === undefined && scope === "temporary") {
      if (this.#phase !== "live") {
        throw new Error("Dynamic temporary roles can only be invited during a live meeting");
      }
      const manifest = this.#readObject(payload, "participantManifest");
      if (manifest === undefined) {
        throw new Error("Temporary participant manifest is required");
      }
      const candidate = manifest as unknown as ParticipantManifest;
      if (candidate.scope !== "temporary" || candidate.invitation === undefined) {
        throw new Error("Dynamic participant must contain a temporary-role invitation");
      }
      const nextSession: RoundtableSession = {
        ...session,
        updatedAt: this.#now().toISOString(),
        participants: [...session.participants, candidate],
      };
      if (validateRoundtableSession(nextSession, workspace).length > 0) {
        throw new Error("Temporary participant manifest failed validation");
      }
      const inviter = candidate.invitation;
      if (inviter.inviterType === "role") {
        if (this.#roles.get(inviter.inviterId)?.scope !== "long_term") {
          throw new Error("Temporary roles require an active long-term role inviter");
        }
      } else if (inviter.inviterId !== "user.direct_host") {
        throw new Error("User invitations must come from the direct meeting host");
      }
      this.#session = structuredClone(nextSession);
      session = nextSession;
      participant = candidate;
    }
    if (
      participant === undefined ||
      participant.participantId !== roleId ||
      participant.scope !== scope
    ) {
      throw new Error("Participant identity or scope does not match the role command");
    }
    const displayName = participant.displayName;
    const systemPrompt = participant.systemPromptSnapshot;
    const modelProfileId = participant.modelRouteSnapshot.primaryModelProfileId;
    const skillIds = participant.capabilitiesSnapshot.skillIds;
    if (scope === "long_term") {
      if (
        participant.scope !== "long_term" ||
        !workspace.roles.some((role) => role.roleProfileId === participant.roleProfileId) ||
        participant.retentionPolicy !== "retain_profile"
      ) {
        throw new Error("Long-term role manifest is incomplete");
      }
    } else {
      if (
        participant.scope !== "temporary" ||
        participant.invitation.status !== "accepted" ||
        !["delete_after_session", "review_at_close", "promote_candidate"].includes(
          participant.retentionPolicy,
        )
      ) {
        throw new Error("Temporary role invitation is incomplete");
      }
    }

    const model = workspace.models.find(
      (candidate) => candidate.modelProfileId === modelProfileId && candidate.enabled,
    );
    const provider = model === undefined
      ? undefined
      : workspace.providers.find(
          (candidate) =>
            candidate.providerProfileId === model.providerProfileId && candidate.enabled,
        );
    if (model === undefined || provider === undefined) {
      throw new Error("Participant model route cannot be resolved");
    }
    const apiKey = this.#credentials.get(provider.credentialRef);
    if (apiKey === undefined || apiKey.length === 0) {
      throw new Error("Provider credential is unavailable");
    }
    const skillPaths = skillIds.map((skillId) => {
      const skill = workspace.skills.find(
        (candidate) => candidate.skillId === skillId && candidate.enabled,
      );
      if (skill === undefined) {
        throw new Error("Participant skill grant cannot be resolved");
      }
      if (skill.source.kind === "git") {
        if (
          skill.importStatus !== "installed" ||
          skill.installDirectory === undefined ||
          skill.source.contentDigest === undefined
        ) {
          throw new Error("Git Skill source is not a verified local installation");
        }
        return this.#resolveApprovedSkillPath(skill.installDirectory);
      }
      return this.#resolveApprovedSkillPath(skill.source.locator);
    });
    const mcpGrants = participant.capabilitiesSnapshot.mcpGrants;
    if (mcpGrants.length > 16) {
      throw new Error("Participant MCP server grant limit exceeded");
    }
    const mcpServers = mcpGrants.map((grant): ResolvedMcpServerRuntimeConfiguration => {
      if (grant.approvalMode !== "never") {
        throw new Error("MCP grants requiring interactive approval cannot run headlessly");
      }
      const server = workspace.mcpServers.find(
        (candidate) => candidate.mcpServerId === grant.mcpServerId && candidate.enabled,
      );
      if (server === undefined) {
        throw new Error("Participant MCP grant cannot be resolved");
      }
      if (!["registered", "installed"].includes(server.importStatus ?? "registered")) {
        throw new Error("Participant MCP server has not completed explicit catalog approval");
      }
      if (
        server.transport === "stdio" &&
        (server.command === undefined || !this.#isAllowedImportedMcpCommand(server.command))
      ) {
        throw new Error("MCP stdio command is outside the approved launcher allowlist");
      }
      if (server.transport !== "stdio") {
        if (server.endpoint === undefined) {
          throw new Error("Remote MCP server is missing an endpoint");
        }
        validateRemoteMcpEndpoint(server.endpoint);
      }
      let resolvedWorkingDirectory = server.workingDirectory;
      if (server.source?.kind === "git") {
        if (
          server.importStatus !== "installed" ||
          server.installDirectory === undefined ||
          server.contentDigest === undefined ||
          server.source.contentDigest !== server.contentDigest
        ) {
          throw new Error("Git MCP source is not a verified local installation");
        }
        resolvedWorkingDirectory = this.#resolveApprovedMcpWorkingDirectory(
          server.installDirectory,
          server.workingDirectory,
        );
      }
      return {
        serverId: server.mcpServerId,
        displayName: server.displayName,
        transport: server.transport,
        ...(server.command === undefined ? {} : { command: server.command }),
        ...(server.arguments === undefined ? {} : { arguments: server.arguments }),
        ...(resolvedWorkingDirectory === undefined
          ? {}
          : { workingDirectory: resolvedWorkingDirectory }),
        ...(server.endpoint === undefined ? {} : { endpoint: server.endpoint }),
        ...this.#optionalCredentialField(
          "environment",
          this.#resolveCredentialReferences(server.environmentCredentialRefs),
        ),
        ...this.#optionalCredentialField(
          "headers",
          this.#resolveCredentialReferences(server.headerCredentialRefs),
        ),
        toolAllowlist: [...grant.toolAllowlist],
      };
    });
    return {
      displayName,
      providerId: provider.runtimeProviderId,
      modelId: model.modelId,
      apiKey,
      systemPrompt,
      skillPaths,
      mcpServers,
    };
  }

  #resolveApprovedSkillPath(locator: string): string {
    const lexicalCwd = resolve(this.#options.cwd ?? process.cwd());
    const lexicalCandidate = resolve(lexicalCwd, locator);
    const approvedRoots = [
      lexicalCwd,
      resolve(homedir(), ".codex", "skills"),
      resolve(homedir(), ".agents", "skills"),
      resolve(homedir(), ".pi", "agent", "skills"),
      ...(this.#options.catalogSkillRoot === undefined
        ? []
        : [resolve(this.#options.catalogSkillRoot)]),
      ...(process.env.LOCALAPPDATA === undefined
        ? []
        : [resolve(process.env.LOCALAPPDATA, "PiRoundtable", "catalog", "skills")]),
    ]
      .filter((root) => existsSync(root))
      .map((root) => realpathSync(root));
    const candidate = realpathSync(lexicalCandidate);
    const isApproved = approvedRoots.some((root) => {
      const pathFromRoot = relative(root, candidate);
      return pathFromRoot === "" ||
        (!pathFromRoot.startsWith(`..${sep}`) && pathFromRoot !== ".." && !isAbsolute(pathFromRoot));
    });
    const leaf = basename(candidate).toLowerCase();
    const candidateType = statSync(candidate);
    const isSkillManifest = leaf === "skill.md" && candidateType.isFile();
    const isSkillDirectory = candidateType.isDirectory();
    if (!isApproved || (!isSkillManifest && !isSkillDirectory)) {
      throw new Error("Skill locator is outside approved roots or is not a Skill directory/manifest");
    }
    return candidate;
  }

  #resolveApprovedMcpWorkingDirectory(
    installDirectory: string,
    workingDirectory: string | undefined,
  ): string {
    const approvedRoots = [
      ...(this.#options.catalogMcpRoot === undefined
        ? []
        : [resolve(this.#options.catalogMcpRoot)]),
      ...(process.env.LOCALAPPDATA === undefined
        ? []
        : [resolve(process.env.LOCALAPPDATA, "PiRoundtable", "catalog", "mcp")]),
    ]
      .filter((root) => existsSync(root))
      .map((root) => realpathSync(root));
    const installation = realpathSync(resolve(installDirectory));
    const approved = approvedRoots.some((root) => {
      const pathFromRoot = relative(root, installation);
      return pathFromRoot === "" ||
        (!pathFromRoot.startsWith(".." + sep) && pathFromRoot !== ".." && !isAbsolute(pathFromRoot));
    });
    const working = realpathSync(resolve(workingDirectory ?? installDirectory));
    const pathFromInstallation = relative(installation, working);
    const contained = pathFromInstallation === "" ||
      (!pathFromInstallation.startsWith(".." + sep) &&
        pathFromInstallation !== ".." &&
        !isAbsolute(pathFromInstallation));
    if (!approved || !contained || !statSync(working).isDirectory()) {
      throw new Error("Git MCP working directory is outside its approved installation");
    }
    return working;
  }

  #resolveCredentialReferences(
    references: Record<string, string> | undefined,
  ): Record<string, string> | undefined {
    if (references === undefined) {
      return undefined;
    }
    return Object.fromEntries(Object.entries(references).map(([name, reference]) => {
      const credential = this.#credentials.get(reference);
      if (credential === undefined || credential.length === 0) {
        throw new Error("MCP credential is unavailable");
      }
      return [name, credential];
    }));
  }

  #optionalCredentialField<K extends "environment" | "headers">(
    name: K,
    value: Record<string, string> | undefined,
  ): { [P in K]?: Record<string, string> } {
    return value === undefined ? {} : { [name]: value } as { [P in K]: Record<string, string> };
  }

  #isAllowedImportedMcpCommand(command: string): boolean {
    return [
      "node", "node.exe", "python", "python.exe", "python3",
      "uv", "uv.exe", "uvx", "uvx.exe",
      "npx", "npx.cmd", "npm", "npm.cmd", "pnpm", "pnpm.cmd",
      "bun", "bun.exe", "deno", "deno.exe",
      "dotnet", "dotnet.exe", "cargo", "cargo.exe",
    ].includes(command.toLowerCase());
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
    this.#expectedTurns.clear();
    this.#pendingPublicTurns.length = 0;
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
    visibility: "public" | "private" = "public",
    audience?: string[],
  ): MeetingEvent {
    const event: MeetingEvent = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId: this.meetingId,
      eventId: randomUUID(),
      sequence: ++this.#sequence,
      runtimeGeneration: this.runtimeGeneration,
      kind,
      occurredAt: this.#now().toISOString(),
      visibility,
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
    if (audience !== undefined) {
      event.audience = audience;
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

  #readObject(payload: JsonObject, key: string): JsonObject | undefined {
    const value = payload[key];
    return typeof value === "object" && value !== null && !Array.isArray(value)
      ? value
      : undefined;
  }

  #readStringArray(payload: JsonObject, key: string): string[] | undefined {
    const value = payload[key];
    if (value === undefined) {
      return [];
    }
    if (!Array.isArray(value) || value.some((item) => typeof item !== "string" || item.length === 0)) {
      return undefined;
    }
    return value as string[];
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
