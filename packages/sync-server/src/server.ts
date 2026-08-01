import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { pathToFileURL } from "node:url";

import { PROTOCOL_VERSION, isMeetingEventKind, type JsonObject, type MeetingEvent } from "@pi-roundtable/protocol";

import { InMemoryMeetingStore, MeetingStoreError } from "./meeting-store.js";

const MAX_BODY_BYTES = 1_048_576;

function sendJson(response: ServerResponse, status: number, value: unknown): void {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),
    "cache-control": "no-store",
  });
  response.end(body);
}

function sendStoreError(response: ServerResponse, error: MeetingStoreError): void {
  const conflictCodes = new Set([
    "lease_conflict",
    "lease_owner_mismatch",
    "stale_runtime_generation",
    "generation_mismatch",
  ]);
  const status = conflictCodes.has(error.code) ? 409 : error.code === "invalid_argument" ? 400 : 412;
  sendJson(response, status, { error: error.code, message: error.message });
}

async function readJsonObject(request: IncomingMessage): Promise<Record<string, unknown>> {
  const chunks: Buffer[] = [];
  let length = 0;
  for await (const chunk of request) {
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    length += bytes.length;
    if (length > MAX_BODY_BYTES) {
      throw new Error("request body exceeds 1 MiB");
    }
    chunks.push(bytes);
  }
  const value: unknown = JSON.parse(Buffer.concat(chunks).toString("utf8"));
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("request body must be a JSON object");
  }
  return value as Record<string, unknown>;
}

function decodeSegment(value: string): string {
  const decoded = decodeURIComponent(value);
  if (decoded.length === 0 || decoded.length > 128 || decoded.includes("/")) {
    throw new Error("invalid path identifier");
  }
  return decoded;
}

function requiredString(body: Record<string, unknown>, key: string): string {
  const value = body[key];
  if (typeof value !== "string" || value.length === 0 || value.length > 128) {
    throw new Error(`${key} must be a non-empty string`);
  }
  return value;
}

function optionalNullableString(body: Record<string, unknown>, key: string): string | null {
  const value = body[key];
  if (value === null) {
    return null;
  }
  if (typeof value !== "string" || value.length === 0 || value.length > 128) {
    throw new Error(`${key} must be null or a non-empty string`);
  }
  return value;
}

function jsonObject(value: unknown): JsonObject {
  if (value === undefined) {
    return {};
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("payload must be a JSON object");
  }
  return value as JsonObject;
}

function parseCursor(raw: string | null): number {
  if (raw === null) {
    return 0;
  }
  if (raw.length === 0) {
    throw new Error("after must be a non-negative integer");
  }
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error("after must be a non-negative integer");
  }
  return value;
}

function writeSse(response: ServerResponse, event: MeetingEvent): void {
  response.write(`id: ${event.sequence}\n`);
  response.write("event: meeting-event\n");
  response.write(`data: ${JSON.stringify(event)}\n\n`);
}

export function createSyncServer(store = new InMemoryMeetingStore()): Server {
  return createServer(async (request, response) => {
    try {
      const method = request.method ?? "GET";
      const url = new URL(request.url ?? "/", "http://localhost");

      if (method === "GET" && url.pathname === "/healthz") {
        sendJson(response, 200, {
          status: "ok",
          service: "pi-roundtable-sync",
          protocolVersion: PROTOCOL_VERSION,
          persistence: "memory",
        });
        return;
      }

      const eventsMatch = /^\/v1\/meetings\/([^/]+)\/events$/.exec(url.pathname);
      if (eventsMatch !== null) {
        const meetingId = decodeSegment(eventsMatch[1] ?? "");
        if (method === "GET") {
          const after = parseCursor(url.searchParams.get("after"));
          sendJson(response, 200, { events: store.eventsAfter(meetingId, after) });
          return;
        }
        if (method === "POST") {
          const body = await readJsonObject(request);
          const kind = body.kind;
          if (!isMeetingEventKind(kind) || kind.startsWith("runtime.lease_")) {
            throw new Error("kind must be a non-lease meeting event kind");
          }
          const generation = body.runtimeGeneration;
          if (!Number.isSafeInteger(generation) || (generation as number) < 1) {
            throw new Error("runtimeGeneration must be a positive integer");
          }
          const event = store.append({
            meetingId,
            ownerRuntimeId: requiredString(body, "ownerRuntimeId"),
            runtimeGeneration: generation as number,
            kind,
            payload: jsonObject(body.payload),
            ...(body.actorId !== undefined ? { actorId: optionalNullableString(body, "actorId") } : {}),
            ...(body.targetId !== undefined ? { targetId: optionalNullableString(body, "targetId") } : {}),
            ...(body.causationId !== undefined
              ? { causationId: optionalNullableString(body, "causationId") }
              : {}),
          });
          sendJson(response, 201, event);
          return;
        }
      }

      const streamMatch = /^\/v1\/meetings\/([^/]+)\/stream$/.exec(url.pathname);
      if (method === "GET" && streamMatch !== null) {
        const meetingId = decodeSegment(streamMatch[1] ?? "");
        const afterHeader = request.headers["last-event-id"];
        const afterValue = url.searchParams.get("after") ??
          (typeof afterHeader === "string" ? afterHeader : null);
        const after = parseCursor(afterValue);

        response.writeHead(200, {
          "content-type": "text/event-stream; charset=utf-8",
          "cache-control": "no-cache, no-transform",
          connection: "keep-alive",
          "x-accel-buffering": "no",
        });
        response.write(": connected\n\n");
        for (const event of store.eventsAfter(meetingId, after)) {
          writeSse(response, event);
        }
        const unsubscribe = store.subscribe(meetingId, (event) => writeSse(response, event));
        const keepAlive = setInterval(() => response.write(": keepalive\n\n"), 15_000);
        keepAlive.unref();
        request.once("close", () => {
          clearInterval(keepAlive);
          unsubscribe();
        });
        return;
      }

      const leasesMatch = /^\/v1\/meetings\/([^/]+)\/leases$/.exec(url.pathname);
      if (method === "POST" && leasesMatch !== null) {
        const meetingId = decodeSegment(leasesMatch[1] ?? "");
        const body = await readJsonObject(request);
        const ttlMs = body.ttlMs;
        const expectedGeneration = body.expectedGeneration;
        if (!Number.isSafeInteger(ttlMs)) {
          throw new Error("ttlMs must be an integer");
        }
        if (expectedGeneration !== undefined && !Number.isSafeInteger(expectedGeneration)) {
          throw new Error("expectedGeneration must be an integer when supplied");
        }
        const result = store.acquireLease({
          meetingId,
          ownerRuntimeId: requiredString(body, "ownerRuntimeId"),
          ttlMs: ttlMs as number,
          ...(expectedGeneration !== undefined
            ? { expectedGeneration: expectedGeneration as number }
            : {}),
        });
        sendJson(response, result.renewed ? 200 : 201, result);
        return;
      }

      const releaseMatch = /^\/v1\/meetings\/([^/]+)\/leases\/([^/]+)$/.exec(url.pathname);
      if (method === "DELETE" && releaseMatch !== null) {
        const event = store.releaseLease(
          decodeSegment(releaseMatch[1] ?? ""),
          decodeSegment(releaseMatch[2] ?? ""),
        );
        sendJson(response, 200, event);
        return;
      }

      sendJson(response, 404, { error: "not_found" });
    } catch (error) {
      if (error instanceof MeetingStoreError) {
        sendStoreError(response, error);
      } else {
        sendJson(response, 400, {
          error: "bad_request",
          message: error instanceof Error ? error.message : String(error),
        });
      }
    }
  });
}

export function startDevelopmentServer(): Server {
  const host = process.env.HOST ?? "127.0.0.1";
  const port = Number.parseInt(process.env.PORT ?? "4317", 10);
  const server = createSyncServer();
  server.listen(port, host, () => {
    console.log(`Pi Roundtable sync server listening on http://${host}:${port}`);
    console.log("Development mode: in-memory storage, no authentication");
  });
  return server;
}

const entry = process.argv[1];
if (entry !== undefined && import.meta.url === pathToFileURL(entry).href) {
  startDevelopmentServer();
}
