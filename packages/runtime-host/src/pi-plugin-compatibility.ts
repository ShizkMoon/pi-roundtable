import type { ResolvedMcpServerRuntimeConfiguration } from "./mcp-client-manager.js";

export const PI_PLUGIN_COMPATIBILITY_VERSION = 1;

export type PiPluginCompatibilityMode =
  | "native_resource"
  | "mcp_bridge"
  | "unsupported_in_process";

export interface PiPluginCompatibilityCapability {
  kind: "skill" | "mcp" | "extension";
  mode: PiPluginCompatibilityMode;
  executable: boolean;
  rationale: string;
}

export interface ResolvedPiPluginSet {
  compatibilityVersion: number;
  skillPaths: readonly string[];
  mcpServers: readonly ResolvedMcpServerRuntimeConfiguration[];
}

/**
 * Stable compatibility statement presented by the host rather than inferred
 * from whichever Pi SDK happens to be installed.
 */
export const PI_PLUGIN_CAPABILITIES: readonly PiPluginCompatibilityCapability[] = [
  {
    kind: "skill",
    mode: "native_resource",
    executable: true,
    rationale: "Approved Pi Skill content is loaded as a bounded role resource.",
  },
  {
    kind: "mcp",
    mode: "mcp_bridge",
    executable: true,
    rationale: "Tool plugins execute behind the host MCP allowlist and approval boundary.",
  },
  {
    kind: "extension",
    mode: "unsupported_in_process",
    executable: false,
    rationale: "Raw Pi extensions inherit host OS permissions and cannot run in the meeting process.",
  },
] as const;

/**
 * Resolve the only two executable plugin forms accepted by a meeting. Native
 * extension paths are intentionally absent from the input type so message text
 * or workspace settings cannot smuggle executable modules into Pi.
 */
export function resolvePiPluginSet(
  skillPaths: readonly string[],
  mcpServers: readonly ResolvedMcpServerRuntimeConfiguration[],
): ResolvedPiPluginSet {
  const uniqueSkillPaths = uniqueBy(skillPaths, (path) => {
    if (path.trim().length === 0 || path.includes("\0")) {
      throw new Error("Approved Pi Skill paths must be non-empty filesystem paths");
    }
    return path;
  });
  const uniqueMcpServers = resolveUniqueMcpServers(mcpServers);
  return {
    compatibilityVersion: PI_PLUGIN_COMPATIBILITY_VERSION,
    skillPaths: uniqueSkillPaths,
    mcpServers: uniqueMcpServers,
  };
}

function resolveUniqueMcpServers(
  servers: readonly ResolvedMcpServerRuntimeConfiguration[],
): ResolvedMcpServerRuntimeConfiguration[] {
  const resolved = new Map<string, ResolvedMcpServerRuntimeConfiguration>();
  for (const server of servers) {
    if (server.serverId.trim().length === 0) {
      throw new Error("Approved MCP servers must have a non-empty serverId");
    }
    const existing = resolved.get(server.serverId);
    if (existing === undefined) {
      resolved.set(server.serverId, server);
      continue;
    }
    if (mcpConfigurationFingerprint(existing) !== mcpConfigurationFingerprint(server)) {
      throw new Error(`Conflicting approved MCP configurations for ${server.serverId}`);
    }
  }
  return [...resolved.values()];
}

/**
 * The manifest resolver already validates endpoints, executables, and path
 * boundaries. This comparison is deliberately local: it prevents two grants
 * with one identity but different authority from being silently collapsed.
 */
function mcpConfigurationFingerprint(
  server: ResolvedMcpServerRuntimeConfiguration,
): string {
  const orderedRecord = (value: Record<string, string> | undefined) =>
    value === undefined
      ? undefined
      : Object.fromEntries(Object.entries(value).sort(([left], [right]) => left.localeCompare(right)));
  return JSON.stringify({
    serverId: server.serverId,
    displayName: server.displayName,
    transport: server.transport,
    command: server.command,
    arguments: server.arguments,
    workingDirectory: server.workingDirectory,
    endpoint: server.endpoint,
    environment: orderedRecord(server.environment),
    headers: orderedRecord(server.headers),
    toolAllowlist: [...new Set(server.toolAllowlist)].sort(),
    approvalMode: server.approvalMode,
    executionMode: server.executionMode,
  });
}

function uniqueBy<T>(values: readonly T[], key: (value: T) => string): T[] {
  const seen = new Set<string>();
  const result: T[] = [];
  for (const value of values) {
    const identity = key(value);
    if (seen.has(identity)) {
      continue;
    }
    seen.add(identity);
    result.push(value);
  }
  return result;
}
