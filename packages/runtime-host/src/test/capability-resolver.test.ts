import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import type { CapabilityPolicy, WorkspaceProfile } from "@pi-roundtable/protocol";

import { WorkspaceCapabilityResolver } from "../capability-resolver.js";

test("resolves only approved Skill and MCP grants and leaves generic tools non-executable", () => {
  const runtimeDirectory = mkdtempSync(join(tmpdir(), "pi-roundtable-capability-"));
  const skillManifest = join(runtimeDirectory, "skills", "review", "SKILL.md");
  const mcpRoot = join(runtimeDirectory, "catalog", "mcp");
  const mcpInstallation = join(mcpRoot, "mcp.review");
  mkdirSync(join(runtimeDirectory, "skills", "review"), { recursive: true });
  mkdirSync(mcpInstallation, { recursive: true });
  writeFileSync(skillManifest, "# Review\n");
  writeFileSync(join(mcpInstallation, "server.js"), "export {};\n");
  const workspace = createWorkspace(skillManifest, mcpInstallation);
  const policy: CapabilityPolicy = {
    skillIds: ["skill.review"],
    mcpGrants: [{
      mcpServerId: "mcp.review",
      toolAllowlist: ["inspect"],
      approvalMode: "on_first_use",
      executionMode: "subagent_preferred",
    }],
    toolGrants: [{
      toolId: "host.unimplemented",
      approvalMode: "never",
      executionMode: "direct",
    }],
  };

  try {
    const resolved = new WorkspaceCapabilityResolver({
      cwd: runtimeDirectory,
      catalogMcpRoot: mcpRoot,
    }).resolve({
      workspace,
      policy,
      resolveCredential: (reference) =>
        reference === "secret://mcp-token" ? "mcp-secret" : undefined,
    });

    assert.deepEqual(resolved.skillPaths, [skillManifest]);
    assert.equal(resolved.mcpServers.length, 1);
    assert.deepEqual(resolved.mcpServers[0], {
      serverId: "mcp.review",
      displayName: "Review MCP",
      transport: "stdio",
      command: "node",
      arguments: ["server.js"],
      workingDirectory: mcpInstallation,
      environment: { REVIEW_TOKEN: "mcp-secret" },
      toolAllowlist: ["inspect"],
      approvalMode: "on_first_use",
      executionMode: "subagent_preferred",
    });
    assert.deepEqual(Object.keys(resolved).sort(), ["mcpServers", "skillPaths"]);
  } finally {
    rmSync(runtimeDirectory, { recursive: true, force: true });
  }
});

test("rejects an MCP launcher outside the explicit allowlist", () => {
  const workspace = createWorkspace("unused", "unused");
  workspace.skills = [];
  delete workspace.mcpServers[0]!.source;
  workspace.mcpServers[0]!.command = "powershell.exe";
  assert.throws(
    () => new WorkspaceCapabilityResolver().resolve({
      workspace,
      policy: {
        skillIds: [],
        mcpGrants: [{
          mcpServerId: "mcp.review",
          toolAllowlist: ["inspect"],
          approvalMode: "never",
          executionMode: "direct",
        }],
        toolGrants: [],
      },
      resolveCredential: () => undefined,
    }),
    /outside the approved launcher allowlist/,
  );
});

function createWorkspace(skillManifest: string, mcpInstallation: string): WorkspaceProfile {
  return {
    configurationVersion: 1,
    workspaceId: "workspace.capabilities",
    displayName: "Capability fixture",
    updatedAt: "2026-08-04T00:00:00.000Z",
    providers: [],
    models: [],
    skills: [{
      skillId: "skill.review",
      displayName: "Review",
      description: "Review documents",
      source: { kind: "local", locator: skillManifest },
      enabled: true,
    }],
    mcpServers: [{
      mcpServerId: "mcp.review",
      displayName: "Review MCP",
      source: {
        kind: "git",
        locator: "https://github.com/example/review-mcp",
        contentDigest: "sha256:review",
      },
      importStatus: "installed",
      installDirectory: mcpInstallation,
      contentDigest: "sha256:review",
      transport: "stdio",
      command: "node",
      arguments: ["server.js"],
      workingDirectory: mcpInstallation,
      environmentCredentialRefs: { REVIEW_TOKEN: "secret://mcp-token" },
      enabled: true,
    }],
    roles: [],
  };
}
