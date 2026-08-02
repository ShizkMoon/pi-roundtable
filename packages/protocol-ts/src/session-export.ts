export const SESSION_EXPORT_PACKAGE_VERSION = 1 as const;

export interface SessionExportMessage {
  messageId: string;
  kind: "host" | "role" | "system";
  speakerId: string;
  speakerName: string;
  visibility: "public" | "private";
  audienceRoleIds: string[];
  text: string;
  state: "submitted" | "streaming" | "completed" | "cancelled";
  occurredAt: string;
}

export interface SessionExportPackage {
  packageVersion: typeof SESSION_EXPORT_PACKAGE_VERSION;
  protocolVersion: 1;
  sourceSessionId: string;
  title: string;
  exportedAt: string;
  includesPrivateMessages: boolean;
  messages: SessionExportMessage[];
}

export interface SessionExportValidationIssue {
  path: string;
  code: "invalid_shape" | "invalid_version" | "invalid_id" | "invalid_timestamp" | "invalid_order" | "invalid_visibility" | "duplicate_id";
  message: string;
}

const ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const ROOT_KEYS = new Set(["packageVersion", "protocolVersion", "sourceSessionId", "title", "exportedAt", "includesPrivateMessages", "messages"]);
const MESSAGE_KEYS = new Set(["messageId", "kind", "speakerId", "speakerName", "visibility", "audienceRoleIds", "text", "state", "occurredAt"]);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, allowed: ReadonlySet<string>): boolean {
  const keys = Object.keys(value);
  return keys.length === allowed.size && keys.every((key) => allowed.has(key));
}

export function validateSessionExportPackage(value: unknown): SessionExportValidationIssue[] {
  const issues: SessionExportValidationIssue[] = [];
  if (!isRecord(value) || !hasExactKeys(value, ROOT_KEYS)) {
    return [{ path: "", code: "invalid_shape", message: "Session export root fields must match the public contract exactly." }];
  }
  if (value.packageVersion !== 1 || value.protocolVersion !== 1) {
    issues.push({ path: "packageVersion", code: "invalid_version", message: "Only package/protocol version 1 is supported." });
  }
  if (typeof value.sourceSessionId !== "string" || !ID_PATTERN.test(value.sourceSessionId)) {
    issues.push({ path: "sourceSessionId", code: "invalid_id", message: "Source session ID is invalid." });
  }
  if (typeof value.title !== "string" || value.title.length < 1 || value.title.length > 256 ||
      typeof value.exportedAt !== "string" || !Number.isFinite(Date.parse(value.exportedAt)) ||
      typeof value.includesPrivateMessages !== "boolean" || !Array.isArray(value.messages) || value.messages.length > 10_000) {
    issues.push({ path: "", code: "invalid_shape", message: "Session export metadata or message count is invalid." });
    return issues;
  }

  const messageIds = new Set<string>();
  let previousTime = Number.NEGATIVE_INFINITY;
  for (const [index, candidate] of value.messages.entries()) {
    const path = `messages[${index}]`;
    if (!isRecord(candidate) || !hasExactKeys(candidate, MESSAGE_KEYS)) {
      issues.push({ path, code: "invalid_shape", message: "Message fields must match the public contract exactly." });
      continue;
    }
    const id = candidate.messageId;
    const speakerId = candidate.speakerId;
    if (typeof id !== "string" || !ID_PATTERN.test(id) || typeof speakerId !== "string" || !ID_PATTERN.test(speakerId)) {
      issues.push({ path, code: "invalid_id", message: "Message or speaker ID is invalid." });
    } else if (messageIds.has(id)) {
      issues.push({ path: `${path}.messageId`, code: "duplicate_id", message: `Duplicate message ID '${id}'.` });
    } else {
      messageIds.add(id);
    }
    const occurredAt = typeof candidate.occurredAt === "string" ? Date.parse(candidate.occurredAt) : Number.NaN;
    if (!Number.isFinite(occurredAt)) {
      issues.push({ path: `${path}.occurredAt`, code: "invalid_timestamp", message: "Message timestamp is invalid." });
    } else if (occurredAt < previousTime) {
      issues.push({ path: `${path}.occurredAt`, code: "invalid_order", message: "Messages must be ordered by time." });
    } else {
      previousTime = occurredAt;
    }
    const audience = candidate.audienceRoleIds;
    const audienceValid = Array.isArray(audience) && audience.every((entry) => typeof entry === "string" && ID_PATTERN.test(entry)) && new Set(audience).size === audience.length;
    const visibilityValid = candidate.visibility === "public"
      ? audienceValid && audience.length === 0
      : candidate.visibility === "private" && audienceValid && audience.length > 0 && value.includesPrivateMessages;
    if (!visibilityValid) {
      issues.push({ path, code: "invalid_visibility", message: "Visibility, audience, and export scope are inconsistent." });
    }
    if ((candidate.kind !== "host" && candidate.kind !== "role" && candidate.kind !== "system") ||
        (candidate.state !== "submitted" && candidate.state !== "streaming" && candidate.state !== "completed" && candidate.state !== "cancelled") ||
        typeof candidate.speakerName !== "string" || candidate.speakerName.length < 1 || candidate.speakerName.length > 128 ||
        typeof candidate.text !== "string" || candidate.text.length > 1_048_576) {
      issues.push({ path, code: "invalid_shape", message: "Message enum or text fields are invalid." });
    }
  }
  return issues;
}
