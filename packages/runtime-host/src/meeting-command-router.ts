import { AsyncLocalStorage } from "node:async_hooks";
import { createHash } from "node:crypto";

import {
  MEETING_COMMAND_KINDS,
  PROTOCOL_VERSION,
  type CommandReceipt,
  type MeetingCommand,
  type MeetingCommandKind,
} from "@pi-roundtable/protocol";

const DEFAULT_MAX_REMEMBERED_RECEIPTS = 2_048;
const MAX_COMMAND_FINGERPRINT_CHARS = 1_048_576;

export interface MeetingCommandRouterState {
  meetingId: string;
  runtimeGeneration: number;
  sequence: number;
  leaseActive: boolean;
  stopRequested: boolean;
  stopped: boolean;
}

export type MeetingCommandHandler = (
  command: MeetingCommand,
) => CommandReceipt | Promise<CommandReceipt>;

export interface MeetingCommandRouterOptions {
  readState: () => MeetingCommandRouterState;
  handlers: Readonly<Record<MeetingCommandKind, MeetingCommandHandler>>;
  now?: () => Date;
  maxRememberedReceipts?: number;
}

interface RememberedReceipt {
  fingerprint: string;
  receipt: CommandReceipt;
}

/**
 * Serializes every authoritative Host operation and routes public commands
 * through one fail-closed envelope and idempotency boundary. Meeting state,
 * event sequencing, role sessions, and Pi integration remain in the Host and
 * are reached only through the injected command handlers.
 */
export class MeetingCommandRouter {
  readonly #readState: () => MeetingCommandRouterState;
  readonly #handlers: Readonly<Record<MeetingCommandKind, MeetingCommandHandler>>;
  readonly #now: () => Date;
  readonly #maxRememberedReceipts: number;
  readonly #receipts = new Map<string, RememberedReceipt>();
  readonly #serializedContext = new AsyncLocalStorage<true>();
  #operationTail: Promise<void> = Promise.resolve();

  constructor(options: MeetingCommandRouterOptions) {
    const maxRememberedReceipts = options.maxRememberedReceipts ??
      DEFAULT_MAX_REMEMBERED_RECEIPTS;
    if (
      !Number.isSafeInteger(maxRememberedReceipts) ||
      maxRememberedReceipts < 1 ||
      maxRememberedReceipts > 65_536
    ) {
      throw new RangeError("maxRememberedReceipts must be between 1 and 65536");
    }
    for (const kind of MEETING_COMMAND_KINDS) {
      if (typeof options.handlers[kind] !== "function") {
        throw new Error(`Meeting command handler is missing for ${kind}`);
      }
    }
    this.#readState = options.readState;
    this.#handlers = Object.freeze({ ...options.handlers });
    this.#now = options.now ?? (() => new Date());
    this.#maxRememberedReceipts = maxRememberedReceipts;
  }

  execute(command: MeetingCommand): Promise<CommandReceipt> {
    if (this.#serializedContext.getStore() === true) {
      return Promise.resolve(this.createReceipt(
        command,
        "rejected",
        "reentrant_command",
        "A command cannot enqueue another command from the active serialized operation",
      ));
    }
    return this.serializeOperation(() => this.executeWithinSerializedOperation(command));
  }

  /**
   * Adds lifecycle or continuation work to the same queue used by commands.
   * This prevents stop, restore, and callback continuations from racing a
   * command.
   */
  serializeOperation<T>(operation: () => T | Promise<T>): Promise<T> {
    const result = this.#operationTail.then(() => this.#serializedContext.run(true, operation));
    this.#operationTail = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  /**
   * Routes a command when the caller already owns serializeOperation(). This
   * is used by internal observer commands and must not enqueue recursively.
   */
  async executeWithinSerializedOperation(command: MeetingCommand): Promise<CommandReceipt> {
    if (this.#serializedContext.getStore() !== true) {
      return this.execute(command);
    }
    let fingerprint: string;
    try {
      fingerprint = fingerprintMeetingCommand(command);
    } catch {
      return this.createReceipt(
        command,
        "rejected",
        "invalid_command_fingerprint",
        "Command cannot be fingerprinted deterministically",
      );
    }
    const remembered = this.#receipts.get(command.commandId);
    if (remembered !== undefined) {
      if (remembered.fingerprint !== fingerprint) {
        return this.createReceipt(
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
      const handled = await this.#handlers[command.kind](command);
      if (!isCommandReceipt(handled, command, this.#readState().meetingId)) {
        throw new Error("Command handler returned an invalid receipt");
      }
      receipt = handled;
    } catch {
      receipt = this.createReceipt(
        command,
        "rejected",
        "host_execution_failed",
        "Local Runtime Host could not execute the command",
      );
    }
    if (this.#readState().stopRequested && receipt.status === "accepted") {
      receipt = this.createReceipt(
        command,
        "rejected",
        "runtime_stopped",
        "Runtime is stopped",
      );
    }
    return this.#remember(command, fingerprint, receipt);
  }

  createReceipt(
    command: MeetingCommand,
    status: CommandReceipt["status"],
    errorCode: string | null,
    message: string | null,
    sequence?: number,
  ): CommandReceipt {
    const receipt: CommandReceipt = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId: this.#readState().meetingId,
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

  #validateEnvelope(command: MeetingCommand): CommandReceipt | undefined {
    const state = this.#readState();
    if (command.protocolVersion !== PROTOCOL_VERSION) {
      return this.createReceipt(
        command,
        "rejected",
        "unsupported_protocol",
        `Expected protocol version ${PROTOCOL_VERSION}`,
      );
    }
    if (command.meetingId !== state.meetingId) {
      return this.createReceipt(
        command,
        "rejected",
        "meeting_mismatch",
        "Meeting ID mismatch",
      );
    }
    if (command.runtimeGeneration !== state.runtimeGeneration) {
      return this.createReceipt(
        command,
        "rejected",
        "runtime_generation_mismatch",
        "Command does not carry the active runtime generation",
      );
    }
    if (
      command.expectedSequence !== undefined &&
      command.expectedSequence !== null &&
      command.expectedSequence !== state.sequence
    ) {
      return this.createReceipt(
        command,
        "rejected",
        "sequence_mismatch",
        `Expected sequence ${state.sequence}`,
      );
    }
    if (!state.leaseActive || state.stopRequested || state.stopped) {
      return this.createReceipt(
        command,
        "rejected",
        "runtime_stopped",
        "Runtime is stopped",
      );
    }
    return undefined;
  }

  #remember(
    command: MeetingCommand,
    fingerprint: string,
    receipt: CommandReceipt,
  ): CommandReceipt {
    if (this.#receipts.size >= this.#maxRememberedReceipts) {
      const oldest = this.#receipts.keys().next().value as string | undefined;
      if (oldest !== undefined) {
        this.#receipts.delete(oldest);
      }
    }
    const rememberedReceipt = { ...receipt };
    this.#receipts.set(command.commandId, { fingerprint, receipt: rememberedReceipt });
    return { ...rememberedReceipt };
  }
}

function fingerprintMeetingCommand(command: MeetingCommand): string {
  const hash = createHash("sha256");
  visitStableJson(command, (text) => hash.update(text, "utf8"));
  return hash.digest("hex");
}

function visitStableJson(value: unknown, consume: (text: string) => void): void {
  type Frame =
    | { kind: "value"; value: unknown }
    | { kind: "array"; value: unknown[]; index: number }
    | {
        kind: "object";
        value: Record<string, unknown>;
        keys: string[];
        index: number;
      };
  const stack: Frame[] = [{ kind: "value", value }];
  const activeObjects = new WeakSet<object>();
  let outputLength = 0;
  const append = (text: string): void => {
    outputLength += text.length;
    if (outputLength > MAX_COMMAND_FINGERPRINT_CHARS) {
      throw new RangeError("Command fingerprint exceeds its limit");
    }
    consume(text);
  };

  while (stack.length > 0) {
    const frame = stack.pop()!;
    if (frame.kind === "array") {
      if (frame.index >= frame.value.length) {
        append("]");
        activeObjects.delete(frame.value);
        continue;
      }
      if (frame.index > 0) {
        append(",");
      }
      stack.push({ ...frame, index: frame.index + 1 });
      stack.push({ kind: "value", value: frame.value[frame.index] });
      continue;
    }
    if (frame.kind === "object") {
      if (frame.index >= frame.keys.length) {
        append("}");
        activeObjects.delete(frame.value);
        continue;
      }
      if (frame.index > 0) {
        append(",");
      }
      const key = frame.keys[frame.index]!;
      append(`${JSON.stringify(key)}:`);
      stack.push({ ...frame, index: frame.index + 1 });
      stack.push({ kind: "value", value: frame.value[key] });
      continue;
    }
    const entry = frame.value;
    if (entry === null || typeof entry !== "object") {
      if (
        (typeof entry === "number" && !Number.isFinite(entry)) ||
        entry === undefined ||
        typeof entry === "function" ||
        typeof entry === "symbol" ||
        typeof entry === "bigint"
      ) {
        throw new TypeError("Command fingerprint contains a non-JSON value");
      }
      append(JSON.stringify(entry) ?? "null");
      continue;
    }
    if (activeObjects.has(entry)) {
      throw new TypeError("Command fingerprint contains a circular value");
    }
    activeObjects.add(entry);
    if (Array.isArray(entry)) {
      append("[");
      stack.push({ kind: "array", value: entry, index: 0 });
      continue;
    }
    const prototype = Object.getPrototypeOf(entry);
    if (prototype !== Object.prototype && prototype !== null) {
      throw new TypeError("Command fingerprint contains a non-JSON object");
    }
    append("{");
    const object = entry as Record<string, unknown>;
    const keys = Object.keys(object)
      .filter((key) => object[key] !== undefined)
      .sort();
    stack.push({ kind: "object", value: object, keys, index: 0 });
  }
}

const COMMAND_RECEIPT_KEYS = new Set<PropertyKey>([
  "protocolVersion",
  "meetingId",
  "commandId",
  "status",
  "acknowledgedAt",
  "sequence",
  "errorCode",
  "message",
]);

function isCommandReceipt(
  value: unknown,
  command: MeetingCommand,
  meetingId: string,
): value is CommandReceipt {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  const prototype = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) {
    return false;
  }
  if (Reflect.ownKeys(value).some((key) => !COMMAND_RECEIPT_KEYS.has(key))) {
    return false;
  }
  const receipt = value as Partial<CommandReceipt>;
  return receipt.protocolVersion === PROTOCOL_VERSION &&
    receipt.meetingId === meetingId &&
    receipt.commandId === command.commandId &&
    (receipt.status === "accepted" || receipt.status === "rejected") &&
    typeof receipt.acknowledgedAt === "string" &&
    Number.isFinite(Date.parse(receipt.acknowledgedAt)) &&
    isOptionalNullablePositiveInteger(receipt, "sequence") &&
    isOptionalNullableString(receipt, "errorCode") &&
    isOptionalNullableString(receipt, "message");
}

function isOptionalNullablePositiveInteger(
  receipt: Partial<CommandReceipt>,
  key: "sequence",
): boolean {
  if (!Object.hasOwn(receipt, key)) {
    return true;
  }
  const value = receipt[key];
  return value === null || (Number.isSafeInteger(value) && (value as number) >= 1);
}

function isOptionalNullableString(
  receipt: Partial<CommandReceipt>,
  key: "errorCode" | "message",
): boolean {
  if (!Object.hasOwn(receipt, key)) {
    return true;
  }
  const value = receipt[key];
  return value === null || typeof value === "string";
}
