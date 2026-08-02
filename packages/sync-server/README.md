# Sync server

The sync server is a normalized-event relay and durable cursor service. It never executes models and never stores raw Pi session records. It exposes:

- `GET /healthz`
- `POST /v1/meetings/:meetingId/leases`
- `DELETE /v1/meetings/:meetingId/leases/:ownerRuntimeId`
- `POST /v1/meetings/:meetingId/events`
- `GET /v1/meetings/:meetingId/events?after=<sequence>`
- `GET /v1/meetings/:meetingId/stream?after=<sequence>` (SSE)

All `/v1` routes require a signed bearer device token. A token binds one user and device to an explicit set of meeting IDs, observable audience IDs, authorized runtime IDs, and an expiry. The HMAC key ID is carried separately from the signed payload, so `PI_ROUNDTABLE_AUTH_KEYS_JSON` may contain an old and a new verification key during rotation. Tokens are issued out of band; the relay does not expose a password or token-minting endpoint.

Set `DATABASE_URL` to enable the PostgreSQL store. Startup applies the idempotent migration in `migrations/001_durable_meetings.sql`; lease acquisition, generation fencing, sequence allocation, and event insert/update run under one database transaction. Without `DATABASE_URL`, the server intentionally reports `persistence: memory` and is suitable only for bounded local development. PostgreSQL live notifications are currently process-local; reconnecting clients must use their durable `after`/`Last-Event-ID` cursor, and multi-replica LISTEN/NOTIFY remains pending.

Private events are accepted only with a non-empty unique audience. Replay and SSE return them only when the authenticated user, device, or delegated audience identity intersects that audience. Public and private clients share the same meeting-wide high-water sequence.

The migration also scaffolds versioned per-recipient encrypted meeting-key envelopes for future E2EE/key rotation. Actual client-side content encryption, envelope-management routes, rate limits, TLS termination, retention jobs, and multi-replica notifications remain pending and must not be reported as implemented.
