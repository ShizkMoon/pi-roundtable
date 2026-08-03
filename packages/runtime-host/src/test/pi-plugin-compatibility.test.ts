import assert from "node:assert/strict";
import test from "node:test";

import {
  PI_PLUGIN_CAPABILITIES,
  resolvePiPluginSet,
} from "../pi-plugin-compatibility.js";

test("advertises skills and MCP without claiming unsafe in-process extension support", () => {
  assert.deepEqual(
    PI_PLUGIN_CAPABILITIES.map(({ kind, mode, executable }) => ({ kind, mode, executable })),
    [
      { kind: "skill", mode: "native_resource", executable: true },
      { kind: "mcp", mode: "mcp_bridge", executable: true },
      { kind: "extension", mode: "unsupported_in_process", executable: false },
    ],
  );
});

test("deduplicates approved plugin resources without changing their order", () => {
  const mcp = {
    serverId: "mcp.files",
    displayName: "Files",
    transport: "stdio" as const,
    command: "node",
    toolAllowlist: ["read_document"],
    approvalMode: "on_first_use" as const,
    executionMode: "direct" as const,
  };
  const resolved = resolvePiPluginSet(
    ["C:/skills/research", "C:/skills/research", "C:/skills/review"],
    [mcp, { ...mcp }],
  );
  assert.deepEqual(resolved.skillPaths, ["C:/skills/research", "C:/skills/review"]);
  assert.deepEqual(resolved.mcpServers, [mcp]);
});

test("rejects duplicate MCP identities with conflicting authority", () => {
  const mcp = {
    serverId: "mcp.files",
    displayName: "Files",
    transport: "stdio" as const,
    command: "node",
    toolAllowlist: ["read_document"],
    approvalMode: "on_first_use" as const,
    executionMode: "direct" as const,
  };
  assert.throws(
    () => resolvePiPluginSet([], [mcp, { ...mcp, approvalMode: "always" }]),
    /Conflicting approved MCP configurations/,
  );
});

test("rejects empty plugin identities", () => {
  assert.throws(() => resolvePiPluginSet(["  "], []), /non-empty filesystem paths/);
});
