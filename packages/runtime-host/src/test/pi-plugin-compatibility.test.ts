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

test("hashes credential records without leaking secrets and isolates resolved configuration", () => {
  const first = {
    serverId: "mcp.secure",
    displayName: "Secure MCP",
    transport: "stdio" as const,
    command: "node",
    environment: { B: "second-secret", A: "first-secret" },
    headers: { Authorization: "Bearer header-secret" },
    toolAllowlist: ["read"],
    approvalMode: "never" as const,
    executionMode: "direct" as const,
  };
  const equivalent = {
    ...structuredClone(first),
    environment: { A: "first-secret", B: "second-secret" },
  };
  const resolved = resolvePiPluginSet([], [first, equivalent]);
  first.environment.A = "caller-mutation";
  assert.equal(resolved.mcpServers[0]?.environment?.A, "first-secret");

  let message = "";
  assert.throws(
    () => resolvePiPluginSet([], [equivalent, {
      ...structuredClone(equivalent),
      environment: { A: "different-secret", B: "second-secret" },
    }]),
    (error: unknown) => {
      message = error instanceof Error ? error.message : String(error);
      return /Conflicting approved MCP configurations/.test(message);
    },
  );
  assert.equal(message.includes("first-secret"), false);
  assert.equal(message.includes("different-secret"), false);
});

test("length-prefixes malformed credential records without delimiter collisions", () => {
  const base = {
    serverId: "mcp.nul",
    displayName: "NUL MCP",
    transport: "stdio" as const,
    command: "node",
    toolAllowlist: [],
    approvalMode: "never" as const,
    executionMode: "direct" as const,
  };
  assert.throws(
    () => resolvePiPluginSet([], [
      { ...base, environment: { a: "\0b\0" } },
      { ...base, environment: { a: "", b: "" } },
    ]),
    /Conflicting approved MCP configurations/,
  );
});

test("rejects empty plugin identities", () => {
  assert.throws(() => resolvePiPluginSet(["  "], []), /non-empty filesystem paths/);
});
