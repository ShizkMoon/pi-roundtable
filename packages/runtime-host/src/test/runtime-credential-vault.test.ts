import assert from "node:assert/strict";
import test from "node:test";

import { RuntimeCredentialVault } from "../runtime-credential-vault.js";
import { ZeroizableUtf8Secret } from "../zeroizable-utf8-secret.js";

test("zeroizable secret overwrites its owned UTF-8 buffer and blocks reveal", () => {
  const value = "private-密钥-🔐";
  const secret = new ZeroizableUtf8Secret(value);
  assert.equal(secret.byteLength, Buffer.byteLength(value, "utf8"));
  assert.equal(secret.reveal(), value);
  assert.equal(secret.isZeroized, false);

  secret.close();
  secret.close();
  assert.equal(secret.isZeroized, true);
  assert.throws(() => secret.reveal(), /closed/);
});

test("vault clears every owned buffer and never resolves after close", () => {
  const values = {
    "memory://provider.a": "provider-secret",
    "memory://mcp.a": "mcp-密钥",
  };
  const vault = new RuntimeCredentialVault(values);
  const expectedBytes = Object.values(values).reduce(
    (total, value) => total + Buffer.byteLength(value, "utf8"),
    0,
  );

  assert.equal(vault.resolve("memory://provider.a"), "provider-secret");
  assert.equal(vault.ownedSecretCount, 2);
  assert.equal(vault.ownedByteLength, expectedBytes);
  assert.equal(vault.zeroizedSecretCount, 0);

  vault.close();
  vault.close();
  assert.equal(vault.closed, true);
  assert.equal(vault.resolve("memory://provider.a"), undefined);
  assert.equal(vault.zeroizedSecretCount, vault.ownedSecretCount);
  assert.equal(vault.zeroizedByteLength, expectedBytes);
});
