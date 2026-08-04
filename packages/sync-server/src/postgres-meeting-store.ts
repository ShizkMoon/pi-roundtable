import { randomUUID } from "node:crypto";

import {
  PROTOCOL_VERSION,
  isValidMeetingEventIdentifier,
  isValidMeetingEventKind,
  validateMeetingEvent,
  type MeetingEvent,
} from "@pi-roundtable/protocol";
import { Pool, type PoolClient } from "pg";

import {
  MeetingStoreError,
  type AcquireLeaseInput,
  type AcquireLeaseResult,
  type AppendMeetingEventInput,
  type EventListener,
  type MeetingLease,
  type MeetingStore,
} from "./meeting-store.js";

export const POSTGRES_MIGRATION_001 = `
CREATE TABLE IF NOT EXISTS pi_roundtable_meetings (
  meeting_id text PRIMARY KEY,
  runtime_generation integer NOT NULL DEFAULT 0 CHECK (runtime_generation >= 0),
  last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
  lease_owner_runtime_id text,
  lease_expires_at timestamptz,
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS pi_roundtable_events (
  meeting_id text NOT NULL REFERENCES pi_roundtable_meetings(meeting_id) ON DELETE CASCADE,
  sequence bigint NOT NULL CHECK (sequence > 0),
  event_id text NOT NULL UNIQUE,
  runtime_generation integer NOT NULL CHECK (runtime_generation > 0),
  visibility text NOT NULL CHECK (visibility IN ('public', 'private')),
  audience text[],
  occurred_at timestamptz NOT NULL,
  event_json jsonb NOT NULL,
  PRIMARY KEY (meeting_id, sequence),
  CHECK ((visibility = 'public' AND audience IS NULL) OR
         (visibility = 'private' AND cardinality(audience) > 0))
);
CREATE INDEX IF NOT EXISTS pi_roundtable_events_audience_idx
  ON pi_roundtable_events USING gin (audience);
CREATE TABLE IF NOT EXISTS pi_roundtable_key_envelopes (
  meeting_id text NOT NULL REFERENCES pi_roundtable_meetings(meeting_id) ON DELETE CASCADE,
  key_id text NOT NULL,
  envelope_version integer NOT NULL CHECK (envelope_version > 0),
  recipient_principal_id text NOT NULL,
  encrypted_key bytea NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  retired_at timestamptz,
  PRIMARY KEY (meeting_id, key_id, recipient_principal_id)
);
`;

interface LockedMeetingRow {
  runtime_generation: number;
  last_sequence: string;
  lease_owner_runtime_id: string | null;
  lease_expires_at: Date | null;
}

export interface PostgresMeetingStoreOptions {
  now?: () => Date;
  nextId?: () => string;
}

export class PostgresMeetingStore implements MeetingStore {
  readonly persistence = "postgres" as const;
  readonly #pool: Pool;
  readonly #now: () => Date;
  readonly #nextId: () => string;
  readonly #listeners = new Map<string, Set<EventListener>>();

  constructor(pool: Pool, options: PostgresMeetingStoreOptions = {}) {
    this.#pool = pool;
    this.#now = options.now ?? (() => new Date());
    this.#nextId = options.nextId ?? randomUUID;
    this.#pool.on("error", (error) => {
      console.error("PostgreSQL idle client error:", error.message);
    });
  }

  static fromConnectionString(connectionString: string): PostgresMeetingStore {
    if (!connectionString.startsWith("postgres://") && !connectionString.startsWith("postgresql://")) {
      throw new Error("DATABASE_URL must use postgres:// or postgresql://");
    }
    return new PostgresMeetingStore(new Pool({
      connectionString,
      max: 10,
      connectionTimeoutMillis: 5_000,
      idleTimeoutMillis: 30_000,
      maxLifetimeSeconds: 300,
    }));
  }

  async initialize(): Promise<void> {
    await this.#pool.query(POSTGRES_MIGRATION_001);
  }

  async acquireLease(input: AcquireLeaseInput): Promise<AcquireLeaseResult> {
    validateId(input.meetingId, "meetingId");
    validateId(input.ownerRuntimeId, "ownerRuntimeId");
    if (!Number.isSafeInteger(input.ttlMs) || input.ttlMs < 1_000 || input.ttlMs > 300_000) {
      throw new MeetingStoreError("invalid_argument", "ttlMs must be between 1000 and 300000");
    }
    const result = await this.#transaction(async (client) => {
      const record = await this.#lockRecord(client, input.meetingId);
      if (input.expectedGeneration !== undefined && input.expectedGeneration !== record.runtime_generation) {
        throw new MeetingStoreError("generation_mismatch", `expected generation ${input.expectedGeneration}, current ${record.runtime_generation}`);
      }
      const now = this.#now();
      const active = record.lease_owner_runtime_id !== null && record.lease_expires_at !== null && record.lease_expires_at.getTime() > now.getTime();
      if (active && record.lease_owner_runtime_id !== input.ownerRuntimeId) {
        throw new MeetingStoreError("lease_conflict", `meeting is owned by ${record.lease_owner_runtime_id} until ${record.lease_expires_at!.toISOString()}`);
      }
      const expiresAt = new Date(now.getTime() + input.ttlMs).toISOString();
      if (active) {
        await client.query(
          `UPDATE pi_roundtable_meetings SET lease_expires_at = $2, updated_at = $3 WHERE meeting_id = $1`,
          [input.meetingId, expiresAt, now],
        );
        return {
          lease: {
            meetingId: input.meetingId,
            ownerRuntimeId: input.ownerRuntimeId,
            runtimeGeneration: record.runtime_generation,
            expiresAt,
          },
          renewed: true,
          event: null,
        } satisfies AcquireLeaseResult;
      }
      const generation = record.runtime_generation + 1;
      const lease: MeetingLease = { meetingId: input.meetingId, ownerRuntimeId: input.ownerRuntimeId, runtimeGeneration: generation, expiresAt };
      const event = await this.#insertEvent(client, input.meetingId, Number(record.last_sequence), {
        meetingId: input.meetingId,
        runtimeGeneration: generation,
        kind: "runtime.lease_acquired",
        actorId: input.ownerRuntimeId,
        payload: { ownerRuntimeId: input.ownerRuntimeId, expiresAt },
      });
      await client.query(
        `UPDATE pi_roundtable_meetings
         SET runtime_generation = $2, last_sequence = $3, lease_owner_runtime_id = $4,
             lease_expires_at = $5, updated_at = $6 WHERE meeting_id = $1`,
        [input.meetingId, generation, event.sequence, input.ownerRuntimeId, expiresAt, now],
      );
      return { lease, renewed: false, event } satisfies AcquireLeaseResult;
    });
    if (result.event !== null) this.#notify(result.event);
    return result;
  }

  async releaseLease(meetingId: string, ownerRuntimeId: string): Promise<MeetingEvent> {
    const event = await this.#transaction(async (client) => {
      const record = await this.#lockRecord(client, meetingId);
      assertActiveOwner(record, ownerRuntimeId, this.#now());
      const created = await this.#insertEvent(client, meetingId, Number(record.last_sequence), {
        meetingId,
        runtimeGeneration: record.runtime_generation,
        kind: "runtime.lease_released",
        actorId: ownerRuntimeId,
        payload: {},
      });
      await client.query(
        `UPDATE pi_roundtable_meetings SET last_sequence = $2, lease_owner_runtime_id = NULL,
         lease_expires_at = NULL, updated_at = $3 WHERE meeting_id = $1`,
        [meetingId, created.sequence, this.#now()],
      );
      return created;
    });
    this.#notify(event);
    return event;
  }

  async append(input: AppendMeetingEventInput): Promise<MeetingEvent> {
    validateId(input.meetingId, "meetingId");
    validateId(input.ownerRuntimeId, "ownerRuntimeId");
    if (!isValidMeetingEventKind(input.kind) || input.kind.startsWith("runtime.lease_")) {
      throw new MeetingStoreError("invalid_argument", "event kind must be valid and runtime lease events are store-owned");
    }
    const event = await this.#transaction(async (client) => {
      const record = await this.#lockRecord(client, input.meetingId);
      if (input.runtimeGeneration !== record.runtime_generation) {
        throw new MeetingStoreError(
          input.runtimeGeneration < record.runtime_generation ? "stale_runtime_generation" : "generation_mismatch",
          `event generation ${input.runtimeGeneration}, current ${record.runtime_generation}`,
        );
      }
      assertActiveOwner(record, input.ownerRuntimeId, this.#now());
      validateVisibility(input);
      const created = await this.#insertEvent(client, input.meetingId, Number(record.last_sequence), input);
      await client.query(
        `UPDATE pi_roundtable_meetings SET last_sequence = $2, updated_at = $3 WHERE meeting_id = $1`,
        [input.meetingId, created.sequence, this.#now()],
      );
      return created;
    });
    this.#notify(event);
    return event;
  }

  async eventsAfter(meetingId: string, sequence: number): Promise<MeetingEvent[]> {
    if (!Number.isSafeInteger(sequence) || sequence < 0) {
      throw new MeetingStoreError("invalid_argument", "sequence must be a non-negative integer");
    }
    const result = await this.#pool.query<{ event_json: MeetingEvent }>(
      `SELECT event_json FROM pi_roundtable_events WHERE meeting_id = $1 AND sequence > $2 ORDER BY sequence`,
      [meetingId, sequence],
    );
    return result.rows.map((row) => row.event_json);
  }

  subscribe(meetingId: string, listener: EventListener): () => void {
    let listeners = this.#listeners.get(meetingId);
    if (listeners === undefined) {
      listeners = new Set();
      this.#listeners.set(meetingId, listeners);
    }
    listeners.add(listener);
    return () => {
      listeners!.delete(listener);
      if (listeners!.size === 0) this.#listeners.delete(meetingId);
    };
  }

  async currentLease(meetingId: string): Promise<MeetingLease | null> {
    const result = await this.#pool.query<LockedMeetingRow>(
      `SELECT runtime_generation, last_sequence::text, lease_owner_runtime_id, lease_expires_at
       FROM pi_roundtable_meetings WHERE meeting_id = $1`,
      [meetingId],
    );
    const row = result.rows[0];
    if (row?.lease_owner_runtime_id === null || row?.lease_expires_at === null || row === undefined || row.lease_expires_at.getTime() <= this.#now().getTime()) {
      return null;
    }
    return {
      meetingId,
      ownerRuntimeId: row.lease_owner_runtime_id,
      runtimeGeneration: row.runtime_generation,
      expiresAt: row.lease_expires_at.toISOString(),
    };
  }

  async close(): Promise<void> {
    await this.#pool.end();
  }

  async #lockRecord(client: PoolClient, meetingId: string): Promise<LockedMeetingRow> {
    validateId(meetingId, "meetingId");
    await client.query(`INSERT INTO pi_roundtable_meetings (meeting_id) VALUES ($1) ON CONFLICT DO NOTHING`, [meetingId]);
    const result = await client.query<LockedMeetingRow>(
      `SELECT runtime_generation, last_sequence::text, lease_owner_runtime_id, lease_expires_at
       FROM pi_roundtable_meetings WHERE meeting_id = $1 FOR UPDATE`,
      [meetingId],
    );
    return result.rows[0]!;
  }

  async #insertEvent(
    client: PoolClient,
    meetingId: string,
    lastSequence: number,
    input: Omit<AppendMeetingEventInput, "ownerRuntimeId">,
  ): Promise<MeetingEvent> {
    if (!Number.isSafeInteger(lastSequence) || lastSequence < 0) throw new MeetingStoreError("invalid_argument", "stored sequence is outside the safe integer range");
    validateVisibility(input);
    const event: MeetingEvent = {
      protocolVersion: PROTOCOL_VERSION,
      meetingId,
      eventId: this.#nextId(),
      sequence: lastSequence + 1,
      runtimeGeneration: input.runtimeGeneration,
      kind: input.kind,
      occurredAt: this.#now().toISOString(),
      visibility: input.visibility ?? "public",
      payload: input.payload ?? {},
      ...(input.actorId !== undefined ? { actorId: input.actorId } : {}),
      ...(input.targetId !== undefined ? { targetId: input.targetId } : {}),
      ...(input.causationId !== undefined ? { causationId: input.causationId } : {}),
      ...(input.audience !== undefined ? { audience: input.audience } : {}),
    };
    const validationIssues = validateMeetingEvent(event);
    if (validationIssues.length > 0) {
      throw new MeetingStoreError(
        "invalid_argument",
        `event violates protocol v1 at ${validationIssues[0]!.path || "envelope"}`,
      );
    }
    await client.query(
      `INSERT INTO pi_roundtable_events
       (meeting_id, sequence, event_id, runtime_generation, visibility, audience, occurred_at, event_json)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)`,
      [meetingId, event.sequence, event.eventId, event.runtimeGeneration, event.visibility, event.audience ?? null, event.occurredAt, JSON.stringify(event)],
    );
    return event;
  }

  async #transaction<T>(operation: (client: PoolClient) => Promise<T>): Promise<T> {
    const client = await this.#pool.connect();
    try {
      await client.query("BEGIN");
      const result = await operation(client);
      await client.query("COMMIT");
      return result;
    } catch (error) {
      await client.query("ROLLBACK").catch(() => undefined);
      throw error;
    } finally {
      client.release();
    }
  }

  #notify(event: MeetingEvent): void {
    for (const listener of this.#listeners.get(event.meetingId) ?? []) listener(event);
  }
}

function validateId(value: string, name: string): void {
  if (!isValidMeetingEventIdentifier(value)) {
    throw new MeetingStoreError("invalid_argument", `${name} must be a protocol identifier`);
  }
}

function validateVisibility(input: Omit<AppendMeetingEventInput, "ownerRuntimeId">): void {
  const visibility = input.visibility ?? "public";
  if (visibility === "private") {
    if (input.audience === undefined || input.audience.length === 0 || new Set(input.audience).size !== input.audience.length) {
      throw new MeetingStoreError("invalid_argument", "private events require a unique non-empty audience");
    }
    for (const principal of input.audience) validateId(principal, "audience principal");
  } else if (input.audience !== undefined) {
    throw new MeetingStoreError("invalid_argument", "public events must not carry a private audience");
  }
}

function assertActiveOwner(record: LockedMeetingRow, ownerRuntimeId: string, now: Date): void {
  if (record.lease_owner_runtime_id === null || record.lease_expires_at === null) throw new MeetingStoreError("lease_not_found", "meeting has no active lease");
  if (record.lease_expires_at.getTime() <= now.getTime()) throw new MeetingStoreError("lease_expired", "meeting lease has expired");
  if (record.lease_owner_runtime_id !== ownerRuntimeId) throw new MeetingStoreError("lease_owner_mismatch", "runtime does not own this meeting");
}
