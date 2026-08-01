import assert from "node:assert/strict";
import test from "node:test";

import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

import {
  McpClientManager,
  validateRemoteMcpEndpoint,
} from "../mcp-client-manager.js";

test("accepts secure remote MCP endpoints and loopback HTTP only", () => {
  assert.equal(validateRemoteMcpEndpoint("https://mcp.example.com/api").origin, "https://mcp.example.com");
  assert.equal(validateRemoteMcpEndpoint("http://127.0.0.1:4317/mcp").port, "4317");
  assert.throws(() => validateRemoteMcpEndpoint("http://mcp.example.com/api"), /HTTPS or loopback/);
  assert.throws(() => validateRemoteMcpEndpoint("https://user:secret@mcp.example.com/api"), /credentials/);
  assert.throws(() => validateRemoteMcpEndpoint("https://mcp.example.com/api?token=secret"), /query/);
});

test("discovers an approved MCP tool and proxies a bounded text result", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "fake-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{
      name: "echo",
      description: "Echo a message",
      inputSchema: {
        type: "object",
        properties: { message: { type: "string" } },
        required: ["message"],
      },
    }],
  }));
  server.setRequestHandler(CallToolRequestSchema, (request) => ({
    content: [{ type: "text", text: String(request.params.arguments?.message ?? "") }],
  }));
  await server.connect(serverTransport);
  const manager = new McpClientManager([{
    serverId: "mcp.fake",
    displayName: "Fake MCP",
    transport: "stdio",
    toolAllowlist: ["echo"],
  }], () => clientTransport);
  try {
    const tools = await manager.connect();
    assert.equal(tools.length, 1);
    assert.match(tools[0]!.name, /^mcp_/);
    const result = await tools[0]!.execute(
      "tool-call",
      { message: "hello" },
      undefined,
      undefined,
      {} as never,
    );
    assert.equal(result.content[0]?.type, "text");
    assert.match(result.content[0]?.type === "text" ? result.content[0].text : "", /hello/);
    assert.match(result.content[0]?.type === "text" ? result.content[0].text : "", /Untrusted MCP/);
  } finally {
    await manager.close();
    await server.close();
  }
});

test("does not expose tools outside a non-empty MCP allowlist", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "fake-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{
      name: "dangerous",
      inputSchema: { type: "object", properties: {} },
    }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => ({
    content: [{ type: "text", text: "should not run" }],
  }));
  await server.connect(serverTransport);
  const manager = new McpClientManager([{
    serverId: "mcp.fake",
    displayName: "Fake MCP",
    transport: "stdio",
    toolAllowlist: ["safe_only"],
  }], () => clientTransport);
  try {
    assert.deepEqual(await manager.connect(), []);
  } finally {
    await manager.close();
    await server.close();
  }
});
