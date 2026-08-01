# Wire protocol

The JSON Schemas in `schema/` describe the client/server contract. Protocol version `1` uses additive evolution within the major version. Breaking field or semantic changes require a new major version and parallel server support during migration.

The server assigns `eventId`, `sequence`, and `occurredAt`. Runtime Hosts supply `meetingId`, `runtimeGeneration`, event kind, actor/target IDs, and a normalized payload. Clients resume from the largest fully applied `sequence`.
