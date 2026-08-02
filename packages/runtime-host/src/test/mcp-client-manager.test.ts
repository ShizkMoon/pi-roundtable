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
    approvalMode: "never",
    executionMode: "direct",
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
    approvalMode: "never",
    executionMode: "direct",
  }], () => clientTransport);
  try {
    assert.deepEqual(await manager.connect(), []);
  } finally {
    await manager.close();
    await server.close();
  }
});

test("requires approval and remembers an approved first use without exposing arguments", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "approval-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  let calls = 0;
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{
      name: "write_note",
      title: "Write note",
      inputSchema: { type: "object", properties: { text: { type: "string" } } },
    }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => {
    calls += 1;
    return { content: [{ type: "text", text: "done" }] };
  });
  await server.connect(serverTransport);
  const approvalRequests: Array<{ toolName: string; serverId: string }> = [];
  const manager = new McpClientManager([{
    serverId: "mcp.approval",
    displayName: "Approval MCP",
    transport: "stdio",
    toolAllowlist: ["write_note"],
    approvalMode: "on_first_use",
    executionMode: "direct",
  }], () => clientTransport, async (request) => {
    approvalRequests.push({ toolName: request.toolName, serverId: request.serverId });
    return true;
  });
  try {
    const [tool] = await manager.connect();
    assert.ok(tool);
    await tool.execute("call-1", { text: "private one" }, undefined, undefined, {} as never);
    await tool.execute("call-2", { text: "private two" }, undefined, undefined, {} as never);
    assert.equal(calls, 2);
    assert.deepEqual(approvalRequests, [{ toolName: "write_note", serverId: "mcp.approval" }]);
    assert.equal(JSON.stringify(approvalRequests).includes("private"), false);
  } finally {
    await manager.close();
    await server.close();
  }
});

test("a denied approval prevents the MCP side effect", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "denied-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  let calls = 0;
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{ name: "dangerous", inputSchema: { type: "object", properties: {} } }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => {
    calls += 1;
    return { content: [{ type: "text", text: "unexpected" }] };
  });
  await server.connect(serverTransport);
  const manager = new McpClientManager([{
    serverId: "mcp.denied",
    displayName: "Denied MCP",
    transport: "stdio",
    toolAllowlist: ["dangerous"],
    approvalMode: "always",
    executionMode: "direct",
  }], () => clientTransport, async () => false);
  try {
    const [tool] = await manager.connect();
    assert.ok(tool);
    await assert.rejects(
      tool.execute("call-denied", {}, undefined, undefined, {} as never),
      /not approved/,
    );
    assert.equal(calls, 0);
  } finally {
    await manager.close();
    await server.close();
  }
});
