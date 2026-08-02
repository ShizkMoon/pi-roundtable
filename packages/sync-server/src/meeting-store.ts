import { randomUUID } from "node:crypto";

import {
  PROTOCOL_VERSION,
  type JsonObject,
  type MeetingEvent,
  type MeetingEventKind,
} from "@pi-roundtable/protocol";

export type StoreErrorCode =
  | "invalid_argument"
  | "lease_conflict"
  | "lease_expired"
  | "lease_not_found"
  | "lease_owner_mismatch"
  | "stale_runtime_generation"
  | "generation_mismatch";

export class MeetingStoreError extends Error {
  constructor(
    readonly code: StoreErrorCode,
    message: string,
  ) {
    super(message);
    this.name = "MeetingStoreError";
  }
}

export interface MeetingLease {
  meetingId: string;
  ownerRuntimeId: string;
  runtimeGeneration: number;
  expiresAt: string;
}

export interface AcquireLeaseInput {
  meetingId: string;
  ownerRuntimeId: string;
  ttlMs: number;
  expectedGeneration?: number;
}

export interface AcquireLeaseResult {
  lease: MeetingLease;
  renewed: boolean;
  event: MeetingEvent | null;
}

export interface AppendMeetingEventInput {
  meetingId: string;
  ownerRuntimeId: string;
  runtimeGeneration: number;
  kind: MeetingEventKind;
  actorId?: string | null;
  targetId?: string | null;
  causationId?: string | null;
  visibility?: "public" | "private";
  audience?: string[];
  payload?: JsonObject;
}

export type EventListener = (event: MeetingEvent) => void;

interface MeetingRecord {
  runtimeGeneration: number;
  lastSequence: number;
  lease: MeetingLease | undefined;
  events: MeetingEvent[];
  listeners: Set<EventListener>;
}

export interface MeetingStoreOptions {
  now?: () => Date;
  nextId?: () => string;
}

export type StoreResult<T> = T | Promise<T>;

export interface MeetingStore {
  readonly persistence: "memory" | "postgres";
  acquireLease(input: AcquireLeaseInput): StoreResult<AcquireLeaseResult>;
  releaseLease(meetingId: string, ownerRuntimeId: string): StoreResult<MeetingEvent>;
  append(input: AppendMeetingEventInput): StoreResult<MeetingEvent>;
  eventsAfter(meetingId: string, sequence: number): StoreResult<MeetingEvent[]>;
  subscribe(meetingId: string, listener: EventListener): () => void;
  currentLease(meetingId: string): StoreResult<MeetingLease | null>;
  close?(): StoreResult<void>;
}

export class InMemoryMeetingStore implements MeetingStore {
  readonly persistence = "memory" as const;
  readonly #records = new Map<string, MeetingRecord>();
  readonly #now: () => Date;
  readonly #nextId: () => string;

  constructor(options: MeetingStoreOptions = {}) {
    this.#now = options.now ?? (() => new Date());
    this.#nextId = options.nextId ?? randomUUID;
  }

  acquireLease(input: AcquireLeaseInput): AcquireLeaseResult {
    this.#validateId(input.meetingId, "meetingId");
    this.#validateId(input.ownerRuntimeId, "ownerRuntimeId");
    if (!Number.isSafeInteger(input.ttlMs) || input.ttlMs < 1_000 || input.ttlMs > 300_000) {
      throw new MeetingStoreError("invalid_argument", "ttlMs must be between 1000 and 300000");
    }

    const record = this.#record(input.meetingId);
    if (
      input.expectedGeneration !== undefined &&
      input.expectedGeneration !== record.runtimeGeneration
    ) {
      throw new MeetingStoreError(
        "generation_mismatch",
        `expected generation ${input.expectedGeneration}, current ${record.runtimeGeneration}`,
      );
    }

    const now = this.#now();
    const activeLease =
      record.lease !== undefined && Date.parse(record.lease.expiresAt) > now.getTime()
        ? record.lease
        : undefined;

    if (activeLease !== undefined && activeLease.ownerRuntimeId !== input.ownerRuntimeId) {
      throw new MeetingStoreError(
        "lease_conflict",
        `meeting is owned by ${activeLease.ownerRuntimeId} until ${activeLease.expiresAt}`,
      );
    }

    const expiresAt = new Date(now.getTime() + input.ttlMs).toISOString();
    if (activeLease !== undefined) {
      const renewed: MeetingLease = { ...activeLease, expiresAt };
      record.lease = renewed;
      return { lease: renewed, renewed: true, event: null };
    }

    const runtimeGeneration = record.runtimeGeneration + 1;
    const lease: MeetingLease = {
      meetingId: input.meetingId,
      ownerRuntimeId: input.ownerRuntimeId,
      runtimeGeneration,
      expiresAt,
    };
    record.runtimeGeneration = runtimeGeneration;
    record.lease = lease;
    const event = this.#appendInternal(record, {
      meetingId: input.meetingId,
      runtimeGeneration,
      kind: "runtime.lease_acquired",
      actorId: input.ownerRuntimeId,
      payload: { ownerRuntimeId: input.ownerRuntimeId, expiresAt },
    });
    return { lease, renewed: false, event };
  }

  releaseLease(meetingId: string, ownerRuntimeId: string): MeetingEvent {
    const record = this.#records.get(meetingId);
    if (record?.lease === undefined) {
      throw new MeetingStoreError("lease_not_found", "meeting has no active lease");
    }
    if (record.lease.ownerRuntimeId !== ownerRuntimeId) {
      throw new MeetingStoreError("lease_owner_mismatch", "runtime does not own this meeting");
    }
    if (Date.parse(record.lease.expiresAt) <= this.#now().getTime()) {
      record.lease = undefined;
      throw new MeetingStoreError("lease_expired", "meeting lease has expired");
    }

    const event = this.#appendInternal(record, {
      meetingId,
      runtimeGeneration: record.runtimeGeneration,
      kind: "runtime.lease_released",
      actorId: ownerRuntimeId,
      payload: {},
    });
    record.lease = undefined;
    return event;
  }

  append(input: AppendMeetingEventInput): MeetingEvent {
    const record = this.#records.get(input.meetingId);
    if (record === undefined) {
      throw new MeetingStoreError("lease_not_found", "meeting has no runtime generation");
    }
    if (input.runtimeGeneration !== record.runtimeGeneration) {
      throw new MeetingStoreError(
        input.runtimeGeneration < record.runtimeGeneration
          ? "stale_runtime_generation"
          : "generation_mismatch",
        `event generation ${input.runtimeGeneration}, current ${record.runtimeGeneration}`,
      );
    }
    if (record.lease === undefined) {
      throw new MeetingStoreError("lease_not_found", "meeting has no active lease");
    }
    if (Date.parse(record.lease.expiresAt) <= this.#now().getTime()) {
      record.lease = undefined;
      throw new MeetingStoreError("lease_expired", "meeting lease has expired");
    }
    if (record.lease.ownerRuntimeId !== input.ownerRuntimeId) {
      throw new MeetingStoreError("lease_owner_mismatch", "runtime does not own this meeting");
    }

    return this.#appendInternal(record, input);
  }

  eventsAfter(meetingId: string, sequence: number): MeetingEvent[] {
    if (!Number.isSafeInteger(sequence) || sequence < 0) {
      throw new MeetingStoreError("invalid_argument", "sequence must be a non-negative integer");
    }
    return (this.#records.get(meetingId)?.events ?? []).filter(
      (event) => event.sequence > sequence,
    );
  }

  subscribe(meetingId: string, listener: EventListener): () => void {
    const record = this.#record(meetingId);
    record.listeners.add(listener);
    return () => record.listeners.delete(listener);
  }

  currentLease(meetingId: string): MeetingLease | null {
    const lease = this.#records.get(meetingId)?.lease;
    if (lease === undefined || Date.parse(lease.expiresAt) <= this.#now().getTime()) {
      return null;
    }
    return { ...lease };
  }

  #record(meetingId: string): MeetingRecord {
    let record = this.#records.get(meetingId);
    if (record === undefined) {
      record = {
        runtimeGeneration: 0,
        lastSequence: 0,
        lease: undefined,
        events: [],
        listeners: new Set(),
      };
      this.#records.set(meetingId, record);
    }
    return record;
  }

  #appendInternal(
    record: MeetingRecord,
    input: Omit<AppendMeetingEventInput, "ownerRuntimeId">,
  ): MeetingEvent {
    const visibility = input.visibility ?? "public";
    if (visibility === "private") {
      if (input.audience === undefined || input.audience.length === 0) {
        throw new MeetingStoreError("invalid_argument", "private events require a non-empty audience");
      }
      if (new Set(input.audience).size !== input.audience.length) {
        throw new MeetingStoreError("invalid_argument", "private event audience entries must be unique");
      }
      for (const principalId of input.audience) {
        this.#validateId(principalId, "audience principal");
      }
    } else if (input.audience !== undefined) {
      throw new MeetingStoreError("invalid_argument", "public events must not carry a private audience");
    }
    const event: MeetingEvent = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId: input.meetingId,
      eventId: this.#nextId(),
      sequence: record.lastSequence + 1,
      runtimeGeneration: input.runtimeGeneration,
      kind: input.kind,
      occurredAt: this.#now().toISOString(),
      visibility,
      payload: input.payload ?? {},
      ...(input.actorId !== undefined ? { actorId: input.actorId } : {}),
      ...(input.targetId !== undefined ? { targetId: input.targetId } : {}),
      ...(input.causationId !== undefined ? { causationId: input.causationId } : {}),
      ...(input.audience !== undefined ? { audience: input.audience } : {}),
    };

    record.lastSequence = event.sequence;
    record.events.push(event);
    for (const listener of record.listeners) {
      listener(event);
    }
    return event;
  }

  #validateId(value: string, name: string): void {
    if (value.length === 0 || value.length > 128) {
      throw new MeetingStoreError("invalid_argument", `${name} must contain 1-128 characters`);
    }
  }
}
