import { createHmac, timingSafeEqual } from "node:crypto";
import type { IncomingHttpHeaders } from "node:http";

export interface DeviceTokenPayload {
  version: 1;
  userId: string;
  deviceId: string;
  meetingIds: string[];
  audienceIds: string[];
  runtimeIds: string[];
  expiresAt: string;
}

export interface AuthenticatedPrincipal {
  userId: string;
  deviceId: string;
  meetingIds: ReadonlySet<string>;
  audienceIds: ReadonlySet<string>;
  runtimeIds: ReadonlySet<string>;
  observablePrincipalIds: ReadonlySet<string>;
  expiresAt: string;
  keyId: string;
}

export class AuthenticationError extends Error {
  constructor(readonly code: "missing_token" | "invalid_token" | "expired_token" | "forbidden", message: string) {
    super(message);
    this.name = "AuthenticationError";
  }
}

const ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const KEY_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
const PAYLOAD_KEYS = new Set(["version", "userId", "deviceId", "meetingIds", "audienceIds", "runtimeIds", "expiresAt"]);

function decodeBase64Url(value: string): Buffer {
  if (!/^[A-Za-z0-9_-]+$/.test(value)) {
    throw new AuthenticationError("invalid_token", "token contains invalid base64url data");
  }
  return Buffer.from(value, "base64url");
}

function validateIdList(value: unknown, name: string): string[] {
  if (!Array.isArray(value) || value.length > 256 ||
      value.some((entry) => typeof entry !== "string" || !ID_PATTERN.test(entry)) ||
      new Set(value).size !== value.length) {
    throw new AuthenticationError("invalid_token", `${name} must be a unique ID list`);
  }
  return value as string[];
}

function parsePayload(encoded: string): DeviceTokenPayload {
  let value: unknown;
  try {
    value = JSON.parse(decodeBase64Url(encoded).toString("utf8"));
  } catch (error) {
    if (error instanceof AuthenticationError) throw error;
    throw new AuthenticationError("invalid_token", "token payload is not valid JSON");
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new AuthenticationError("invalid_token", "token payload must be an object");
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record);
  if (keys.length !== PAYLOAD_KEYS.size || keys.some((key) => !PAYLOAD_KEYS.has(key)) ||
      record.version !== 1 || typeof record.userId !== "string" || !ID_PATTERN.test(record.userId) ||
      typeof record.deviceId !== "string" || !ID_PATTERN.test(record.deviceId) ||
      typeof record.expiresAt !== "string" || !Number.isFinite(Date.parse(record.expiresAt))) {
    throw new AuthenticationError("invalid_token", "token payload fields are invalid");
  }
  return {
    version: 1,
    userId: record.userId,
    deviceId: record.deviceId,
    meetingIds: validateIdList(record.meetingIds, "meetingIds"),
    audienceIds: validateIdList(record.audienceIds, "audienceIds"),
    runtimeIds: validateIdList(record.runtimeIds, "runtimeIds"),
    expiresAt: record.expiresAt,
  };
}

export class DeviceTokenAuthenticator {
  readonly #keys: ReadonlyMap<string, Buffer>;
  readonly #now: () => Date;

  constructor(keys: ReadonlyMap<string, Buffer>, now: () => Date = () => new Date()) {
    if (keys.size === 0) {
      throw new Error("at least one device-token verification key is required");
    }
    for (const [keyId, key] of keys) {
      if (!KEY_ID_PATTERN.test(keyId) || key.length < 32) {
        throw new Error("device-token key IDs must be safe and keys must contain at least 32 bytes");
      }
    }
    this.#keys = keys;
    this.#now = now;
  }

  authenticate(headers: IncomingHttpHeaders): AuthenticatedPrincipal {
    const authorization = headers.authorization;
    if (typeof authorization !== "string" || !authorization.startsWith("Bearer ")) {
      throw new AuthenticationError("missing_token", "a bearer device token is required");
    }
    const token = authorization.slice("Bearer ".length);
    const segments = token.split(".");
    if (segments.length !== 3) {
      throw new AuthenticationError("invalid_token", "device token must contain key, payload, and signature segments");
    }
    const [keyId = "", encodedPayload = "", encodedSignature = ""] = segments;
    const key = this.#keys.get(keyId);
    if (key === undefined || !KEY_ID_PATTERN.test(keyId)) {
      throw new AuthenticationError("invalid_token", "device token key is not trusted");
    }
    const signature = decodeBase64Url(encodedSignature);
    const expected = createHmac("sha256", key).update(`${keyId}.${encodedPayload}`).digest();
    if (signature.length !== expected.length || !timingSafeEqual(signature, expected)) {
      throw new AuthenticationError("invalid_token", "device token signature is invalid");
    }
    const payload = parsePayload(encodedPayload);
    if (Date.parse(payload.expiresAt) <= this.#now().getTime()) {
      throw new AuthenticationError("expired_token", "device token has expired");
    }
    const audienceIds = new Set(payload.audienceIds);
    return {
      userId: payload.userId,
      deviceId: payload.deviceId,
      meetingIds: new Set(payload.meetingIds),
      audienceIds,
      runtimeIds: new Set(payload.runtimeIds),
      observablePrincipalIds: new Set([payload.userId, payload.deviceId, ...audienceIds]),
      expiresAt: payload.expiresAt,
      keyId,
    };
  }

  static fromEnvironment(raw = process.env.PI_ROUNDTABLE_AUTH_KEYS_JSON): DeviceTokenAuthenticator {
    if (raw === undefined) {
      throw new Error("PI_ROUNDTABLE_AUTH_KEYS_JSON is required");
    }
    const parsed: unknown = JSON.parse(raw);
    if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new Error("PI_ROUNDTABLE_AUTH_KEYS_JSON must be a JSON object");
    }
    const keys = new Map<string, Buffer>();
    for (const [keyId, encoded] of Object.entries(parsed as Record<string, unknown>)) {
      if (typeof encoded !== "string") throw new Error("authentication keys must be base64 strings");
      keys.set(keyId, Buffer.from(encoded, "base64"));
    }
    return new DeviceTokenAuthenticator(keys);
  }
}

export function createDeviceToken(keyId: string, key: Buffer, payload: DeviceTokenPayload): string {
  if (!KEY_ID_PATTERN.test(keyId) || key.length < 32) throw new Error("invalid signing key");
  const encoded = Buffer.from(JSON.stringify(payload), "utf8").toString("base64url");
  const signature = createHmac("sha256", key).update(`${keyId}.${encoded}`).digest("base64url");
  return `${keyId}.${encoded}.${signature}`;
}
