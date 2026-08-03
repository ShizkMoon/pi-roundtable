import assert from "node:assert/strict";
import test from "node:test";

import { RoleCredentialLease } from "../role-credential-lease.js";

test("scopes resolved credentials to one role and generation until close", () => {
  const lease = new RoleCredentialLease({
    roleId: "role.a",
    runtimeGeneration: 8,
    providerId: "provider.a",
    apiKey: "private-test-key",
    mcpServers: [{
      serverId: "mcp.a",
      displayName: "MCP A",
      transport: "stdio",
      command: "node",
      environment: { TOKEN: "private-mcp-token" },
      toolAllowlist: ["read"],
      approvalMode: "on_first_use",
      executionMode: "direct",
    }],
  });

  assert.equal(lease.roleId, "role.a");
  assert.equal(lease.runtimeGeneration, 8);
  assert.equal(lease.resolveApiKey("provider.a"), "private-test-key");
  assert.equal(lease.resolveApiKey("provider.b"), undefined);
  const first = lease.materializeMcpServers();
  first[0]!.environment!.TOKEN = "caller mutation";
  assert.equal(lease.materializeMcpServers()[0]?.environment?.TOKEN, "private-mcp-token");

  lease.close();
  lease.close();
  assert.equal(lease.closed, true);
  assert.equal(lease.resolveApiKey("provider.a"), undefined);
  assert.deepEqual(lease.materializeMcpServers(), []);
});
