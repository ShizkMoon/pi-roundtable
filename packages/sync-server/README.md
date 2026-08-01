# Sync server

The scaffold implements the protocol's lease fencing and replay semantics with an in-memory store. It exposes:

- `GET /healthz`
- `POST /v1/meetings/:meetingId/leases`
- `DELETE /v1/meetings/:meetingId/leases/:ownerRuntimeId`
- `POST /v1/meetings/:meetingId/events`
- `GET /v1/meetings/:meetingId/events?after=<sequence>`
- `GET /v1/meetings/:meetingId/stream?after=<sequence>` (SSE)

This server has no authentication and no durable persistence. It binds to loopback by default and is for development only. Production work must add authenticated principals, authorization, a durable transaction boundary for lease/event writes, rate limits, TLS, and retention policy.
