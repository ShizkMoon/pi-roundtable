import { randomUUID } from "node:crypto";

import {
  PROTOCOL_VERSION,
  type JsonObject,
  type MeetingEvent,
  type MeetingEventKind,
} from "@pi-roundtable/protocol";

export type MeetingEventListener = (event: MeetingEvent) => void;

interface NormalizedEventWriteRequestBase {
  kind: MeetingEventKind;
  actorId: string | null;
  targetId: string | null;
  causationId: string | null;
  payload: JsonObject;
  allowDuringStop?: boolean;
}

export type NormalizedEventWriteRequest = NormalizedEventWriteRequestBase & (
  | { visibility?: "public"; audience?: never }
  | { visibility: "private"; audience: string[] }
);

export interface NormalizedEventWriter {
  readonly sequence: number;
  reset(initialSequence: number): void;
  subscribe(listener: MeetingEventListener): () => void;
  write(request: NormalizedEventWriteRequest): MeetingEvent | undefined;
}

export interface NormalizedEventWriterOptions {
  meetingId: string;
  runtimeGeneration: number;
  now?: () => Date;
  eventIdFactory?: () => string;
  shouldWrite?: (allowDuringStop: boolean) => boolean;
}

export type NormalizedEventWriterFactory = (
  options: NormalizedEventWriterOptions,
) => NormalizedEventWriter;

/**
 * Synchronous in-memory event authority for one Host generation. It assigns
 * the meeting-wide sequence and normalized protocol envelope, then fans out the
 * same event object in listener insertion order. Persistence remains a client
 * or relay concern, not part of this writer.
 */
export class SynchronousNormalizedEventWriter implements NormalizedEventWriter {
  readonly #options: NormalizedEventWriterOptions;
  readonly #listeners = new Set<MeetingEventListener>();
  #sequence = 0;

  constructor(options: NormalizedEventWriterOptions) {
    if (options.meetingId.length === 0) {
      throw new Error("meetingId is required");
    }
    if (!Number.isSafeInteger(options.runtimeGeneration) || options.runtimeGeneration < 1) {
      throw new RangeError("runtimeGeneration must be a positive safe integer");
    }
    this.#options = options;
  }

  get sequence(): number {
    return this.#sequence;
  }

  reset(initialSequence: number): void {
    if (!Number.isSafeInteger(initialSequence) || initialSequence < 0) {
      throw new RangeError("initialSequence must be a non-negative safe integer");
    }
    this.#sequence = initialSequence;
  }

  subscribe(listener: MeetingEventListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  write(request: NormalizedEventWriteRequest): MeetingEvent | undefined {
    const allowDuringStop = request.allowDuringStop ?? false;
    if (this.#options.shouldWrite?.(allowDuringStop) === false) {
      return undefined;
    }
    if (request.visibility === "private") {
      if (
        request.audience.length === 0 ||
        request.audience.some((principalId) => principalId.length === 0) ||
        new Set(request.audience).size !== request.audience.length
      ) {
        throw new Error("Private normalized events require a non-empty unique audience");
      }
    } else if (request.audience !== undefined) {
      throw new Error("Public normalized events cannot carry an audience");
    }
    const event: MeetingEvent = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId: this.#options.meetingId,
      eventId: (this.#options.eventIdFactory ?? randomUUID)(),
      sequence: ++this.#sequence,
      runtimeGeneration: this.#options.runtimeGeneration,
      kind: request.kind,
      occurredAt: (this.#options.now ?? (() => new Date()))().toISOString(),
      visibility: request.visibility ?? "public",
      payload: request.payload,
    };
    if (request.actorId !== null) {
      event.actorId = request.actorId;
    }
    if (request.targetId !== null) {
      event.targetId = request.targetId;
    }
    if (request.causationId !== null) {
      event.causationId = request.causationId;
    }
    if (request.audience !== undefined) {
      event.audience = request.audience;
    }
    for (const listener of this.#listeners) {
      try {
        listener(event);
      } catch {
        // Presentation and transport listeners cannot corrupt authoritative state.
      }
    }
    return event;
  }
}
