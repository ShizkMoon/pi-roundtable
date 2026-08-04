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
      headers: { Authorization: "Bearer private-http-token" },
      toolAllowlist: ["read"],
      approvalMode: "on_first_use",
      executionMode: "direct",
    }],
  });

  assert.equal(lease.roleId, "role.a");
  assert.equal(lease.runtimeGeneration, 8);
  assert.equal(lease.resolveApiKey("provider.a"), "private-test-key");
  assert.equal(lease.resolveApiKey("provider.b"), undefined);
  assert.equal(lease.ownedSecretCount, 3);
  assert.equal(lease.zeroizedSecretCount, 0);
  const first = lease.materializeMcpServers();
  first[0]!.environment!.TOKEN = "caller mutation";
  first[0]!.headers!.Authorization = "caller mutation";
  assert.equal(lease.materializeMcpServers()[0]?.environment?.TOKEN, "private-mcp-token");
  assert.equal(
    lease.materializeMcpServers()[0]?.headers?.Authorization,
    "Bearer private-http-token",
  );

  lease.close();
  lease.close();
  assert.equal(lease.closed, true);
  assert.equal(lease.zeroizedSecretCount, lease.ownedSecretCount);
  assert.equal(lease.zeroizedByteLength, lease.ownedByteLength);
  assert.equal(lease.resolveApiKey("provider.a"), undefined);
  assert.deepEqual(lease.materializeMcpServers(), []);
});

test("owns exact UTF-8 bytes and zeroizes Unicode credentials idempotently", () => {
  const value = "密钥-🔐";
  const lease = new RoleCredentialLease({
    roleId: "role.unicode",
    runtimeGeneration: 1,
    providerId: "provider.unicode",
    apiKey: value,
    mcpServers: [],
  });

  assert.equal(lease.ownedByteLength, Buffer.byteLength(value, "utf8"));
  lease.close();
  lease.close();
  assert.equal(lease.zeroizedByteLength, Buffer.byteLength(value, "utf8"));
});
