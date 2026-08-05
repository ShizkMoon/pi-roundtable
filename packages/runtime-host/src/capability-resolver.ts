import { existsSync, realpathSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { basename, isAbsolute, relative, resolve, sep } from "node:path";

import type {
  ApprovalMode,
  CapabilityPolicy,
  DelegationPolicy,
  ExecutionMode,
  WorkspaceProfile,
} from "@pi-roundtable/protocol";
import { WEB_SEARCH_TOOL_ID } from "./web-search.js";

import {
  validateRemoteMcpEndpoint,
  type ResolvedMcpServerRuntimeConfiguration,
} from "./mcp-client-manager.js";

export type CredentialReferenceResolver = (reference: string) => string | undefined;

export interface RoleCapabilityResolutionRequest {
  workspace: WorkspaceProfile;
  policy: CapabilityPolicy;
  resolveCredential: CredentialReferenceResolver;
  networkAccess: DelegationPolicy["networkAccess"];
}

export interface ResolvedRoleCapabilities {
  skillPaths: string[];
  mcpServers: ResolvedMcpServerRuntimeConfiguration[];
  webSearch?: {
    approvalMode: ApprovalMode;
    executionMode: ExecutionMode;
  };
}

/** Resolves frozen grants without granting authority from prompt text. */
export interface CapabilityResolver {
  resolve(request: RoleCapabilityResolutionRequest): ResolvedRoleCapabilities;
}

export interface WorkspaceCapabilityResolverOptions {
  cwd?: string;
  catalogSkillRoot?: string;
  catalogMcpRoot?: string;
}

/**
 * Maps approved workspace Skill and MCP catalog entries to local runtime
 * resources. Generic tool grants remain non-executable until a bounded host
 * tool registry is introduced in a later release.
 */
export class WorkspaceCapabilityResolver implements CapabilityResolver {
  readonly #options: WorkspaceCapabilityResolverOptions;

  constructor(options: WorkspaceCapabilityResolverOptions = {}) {
    this.#options = options;
  }

  resolve({
    workspace,
    policy,
    resolveCredential,
    networkAccess,
  }: RoleCapabilityResolutionRequest): ResolvedRoleCapabilities {
    const skillPaths = policy.skillIds.map((skillId) => {
      const skill = workspace.skills.find(
        (candidate) => candidate.skillId === skillId && candidate.enabled,
      );
      if (skill === undefined) {
        throw new Error("Participant skill grant cannot be resolved");
      }
      if (skill.source.kind === "git") {
        if (
          skill.importStatus !== "installed" ||
          skill.installDirectory === undefined ||
          skill.source.contentDigest === undefined
        ) {
          throw new Error("Git Skill source is not a verified local installation");
        }
        return this.#resolveApprovedSkillPath(skill.installDirectory);
      }
      return this.#resolveApprovedSkillPath(skill.source.locator);
    });

    if (policy.mcpGrants.length > 16) {
      throw new Error("Participant MCP server grant limit exceeded");
    }
    const mcpServers = policy.mcpGrants.map(
      (grant): ResolvedMcpServerRuntimeConfiguration => {
        const server = workspace.mcpServers.find(
          (candidate) => candidate.mcpServerId === grant.mcpServerId && candidate.enabled,
        );
        if (server === undefined) {
          throw new Error("Participant MCP grant cannot be resolved");
        }
        if (!["registered", "installed"].includes(server.importStatus ?? "registered")) {
          throw new Error("Participant MCP server has not completed explicit catalog approval");
        }
        if (
          server.transport === "stdio" &&
          (server.command === undefined || !isAllowedImportedMcpCommand(server.command))
        ) {
          throw new Error("MCP stdio command is outside the approved launcher allowlist");
        }
        if (server.transport !== "stdio") {
          if (server.endpoint === undefined) {
            throw new Error("Remote MCP server is missing an endpoint");
          }
          validateRemoteMcpEndpoint(server.endpoint);
        }
        let resolvedWorkingDirectory = server.workingDirectory;
        if (server.source?.kind === "git") {
          if (
            server.importStatus !== "installed" ||
            server.installDirectory === undefined ||
            server.contentDigest === undefined ||
            server.source.contentDigest !== server.contentDigest
          ) {
            throw new Error("Git MCP source is not a verified local installation");
          }
          resolvedWorkingDirectory = this.#resolveApprovedMcpWorkingDirectory(
            server.installDirectory,
            server.workingDirectory,
          );
        }
        return {
          serverId: server.mcpServerId,
          displayName: server.displayName,
          transport: server.transport,
          ...(server.command === undefined ? {} : { command: server.command }),
          ...(server.arguments === undefined ? {} : { arguments: server.arguments }),
          ...(resolvedWorkingDirectory === undefined
            ? {}
            : { workingDirectory: resolvedWorkingDirectory }),
          ...(server.endpoint === undefined ? {} : { endpoint: server.endpoint }),
          ...optionalCredentialField(
            "environment",
            resolveCredentialReferences(server.environmentCredentialRefs, resolveCredential),
          ),
          ...optionalCredentialField(
            "headers",
            resolveCredentialReferences(server.headerCredentialRefs, resolveCredential),
          ),
          toolAllowlist: [...grant.toolAllowlist],
          approvalMode: grant.approvalMode,
          executionMode: grant.executionMode,
        };
      },
    );

    const webSearchGrants = policy.toolGrants.filter(
      (grant) => grant.toolId === WEB_SEARCH_TOOL_ID,
    );
    if (webSearchGrants.length > 1) {
      throw new Error("Participant web search grant is duplicated");
    }
    const webSearch = webSearchGrants[0];
    if (webSearch !== undefined) {
      if (networkAccess === "forbidden") {
        throw new Error("Participant web search grant conflicts with forbidden network access");
      }
      if (webSearch.executionMode === "direct" && networkAccess !== "direct_allowed") {
        throw new Error("Direct web search requires direct_allowed network policy");
      }
      if (webSearch.executionMode !== "direct" && networkAccess === "direct_allowed") {
        throw new Error("SubAgent web search grant requires a SubAgent network policy");
      }
      if (webSearch.executionMode !== "direct" && webSearch.approvalMode !== "never") {
        throw new Error("SubAgent web search requires a pre-approved never-prompt grant");
      }
    }
    return {
      skillPaths,
      mcpServers,
      ...(webSearch === undefined ? {} : { webSearch: { ...webSearch } }),
    };
  }

  #resolveApprovedSkillPath(locator: string): string {
    const lexicalCwd = resolve(this.#options.cwd ?? process.cwd());
    const lexicalCandidate = resolve(lexicalCwd, locator);
    const approvedRoots = [
      lexicalCwd,
      resolve(homedir(), ".codex", "skills"),
      resolve(homedir(), ".agents", "skills"),
      resolve(homedir(), ".pi", "agent", "skills"),
      ...(this.#options.catalogSkillRoot === undefined
        ? []
        : [resolve(this.#options.catalogSkillRoot)]),
      ...(process.env.LOCALAPPDATA === undefined
        ? []
        : [resolve(process.env.LOCALAPPDATA, "PiRoundtable", "catalog", "skills")]),
    ]
      .filter((root) => existsSync(root))
      .map((root) => realpathSync(root));
    const candidate = realpathSync(lexicalCandidate);
    const isApproved = approvedRoots.some((root) => isContainedPath(root, candidate));
    const leaf = basename(candidate).toLowerCase();
    const candidateType = statSync(candidate);
    const isSkillManifest = leaf === "skill.md" && candidateType.isFile();
    const isSkillDirectory = candidateType.isDirectory();
    if (!isApproved || (!isSkillManifest && !isSkillDirectory)) {
      throw new Error("Skill locator is outside approved roots or is not a Skill directory/manifest");
    }
    return candidate;
  }

  #resolveApprovedMcpWorkingDirectory(
    installDirectory: string,
    workingDirectory: string | undefined,
  ): string {
    const approvedRoots = [
      ...(this.#options.catalogMcpRoot === undefined
        ? []
        : [resolve(this.#options.catalogMcpRoot)]),
      ...(process.env.LOCALAPPDATA === undefined
        ? []
        : [resolve(process.env.LOCALAPPDATA, "PiRoundtable", "catalog", "mcp")]),
    ]
      .filter((root) => existsSync(root))
      .map((root) => realpathSync(root));
    const installation = realpathSync(resolve(installDirectory));
    const approved = approvedRoots.some((root) => isContainedPath(root, installation));
    const working = realpathSync(resolve(workingDirectory ?? installDirectory));
    const contained = isContainedPath(installation, working);
    if (!approved || !contained || !statSync(working).isDirectory()) {
      throw new Error("Git MCP working directory is outside its approved installation");
    }
    return working;
  }
}

function isContainedPath(root: string, candidate: string): boolean {
  const pathFromRoot = relative(root, candidate);
  return pathFromRoot === "" ||
    (!pathFromRoot.startsWith(`..${sep}`) && pathFromRoot !== ".." && !isAbsolute(pathFromRoot));
}

function resolveCredentialReferences(
  references: Record<string, string> | undefined,
  resolveCredential: CredentialReferenceResolver,
): Record<string, string> | undefined {
  if (references === undefined) {
    return undefined;
  }
  return Object.fromEntries(Object.entries(references).map(([name, reference]) => {
    const credential = resolveCredential(reference);
    if (credential === undefined || credential.length === 0) {
      throw new Error("MCP credential is unavailable");
    }
    return [name, credential];
  }));
}

function optionalCredentialField<K extends "environment" | "headers">(
  name: K,
  value: Record<string, string> | undefined,
): { [P in K]?: Record<string, string> } {
  return value === undefined ? {} : { [name]: value } as { [P in K]: Record<string, string> };
}

function isAllowedImportedMcpCommand(command: string): boolean {
  return [
    "node", "node.exe", "python", "python.exe", "python3",
    "uv", "uv.exe", "uvx", "uvx.exe",
    "npx", "npx.cmd", "npm", "npm.cmd", "pnpm", "pnpm.cmd",
    "bun", "bun.exe", "deno", "deno.exe",
    "dotnet", "dotnet.exe", "cargo", "cargo.exe",
  ].includes(command.toLowerCase());
}
