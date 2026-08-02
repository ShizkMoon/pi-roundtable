-- Canonical migration mirrored by POSTGRES_MIGRATION_001 in src/postgres-meeting-store.ts.
-- The server persists only normalized protocol events and encrypted key envelopes;
-- it never stores raw Pi session records or executes models.
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
