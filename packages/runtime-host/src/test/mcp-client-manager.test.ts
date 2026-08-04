import assert from "node:assert/strict";
import test from "node:test";

import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  type JSONRPCMessage,
} from "@modelcontextprotocol/sdk/types.js";

import {
  McpClientManager,
  validateRemoteMcpEndpoint,
} from "../mcp-client-manager.js";

class FailingStartTransport implements Transport {
  starts = 0;
  closes = 0;

  async start(): Promise<void> {
    ++this.starts;
    throw new Error("controlled MCP transport start failure");
  }

  async send(): Promise<void> {}

  async close(): Promise<void> {
    ++this.closes;
  }
}

class HangingStartTransport implements Transport {
  starts = 0;
  closes = 0;
  rejectClose = false;

  async start(): Promise<void> {
    ++this.starts;
    await new Promise<void>(() => undefined);
  }

  async send(): Promise<void> {}

  async close(): Promise<void> {
    ++this.closes;
    if (this.rejectClose) {
      throw new Error("controlled MCP transport close failure");
    }
  }
}

class RejectingInitializeTransport implements Transport {
  closes = 0;
  onmessage: NonNullable<Transport["onmessage"]> = () => undefined;

  async start(): Promise<void> {}

  async send(message: JSONRPCMessage): Promise<void> {
    if ("method" in message && message.method === "initialize" && "id" in message) {
      queueMicrotask(() => this.onmessage?.({
        jsonrpc: "2.0",
        id: message.id,
        error: { code: -32603, message: "controlled MCP initialize failure" },
      } as never));
    }
  }

  async close(): Promise<void> {
    ++this.closes;
    await new Promise<void>((resolve) => setImmediate(resolve));
    throw new Error("controlled MCP transport close failure");
  }
}

test("accepts secure remote MCP endpoints and loopback HTTP only", () => {
  assert.equal(validateRemoteMcpEndpoint("https://mcp.example.com/api").origin, "https://mcp.example.com");
  assert.equal(validateRemoteMcpEndpoint("http://127.0.0.1:4317/mcp").port, "4317");
  assert.throws(() => validateRemoteMcpEndpoint("http://mcp.example.com/api"), /HTTPS or loopback/);
  assert.throws(() => validateRemoteMcpEndpoint("https://user:secret@mcp.example.com/api"), /credentials/);
  assert.throws(() => validateRemoteMcpEndpoint("https://mcp.example.com/api?token=secret"), /query/);
});

test("closes a transport that fails before MCP initialization and keeps connect single-flight", async () => {
  const transport = new FailingStartTransport();
  let factoryCalls = 0;
  const manager = new McpClientManager([{
    serverId: "mcp.failing",
    displayName: "Failing MCP",
    transport: "stdio",
    toolAllowlist: [],
    approvalMode: "never",
    executionMode: "direct",
  }], () => {
    ++factoryCalls;
    return transport;
  });

  const first = manager.connect();
  const second = manager.connect();
  assert.equal(first, second);
  await assert.rejects(first, /controlled MCP transport start failure/);
  assert.equal(factoryCalls, 1);
  assert.equal(transport.starts, 1);
  assert.equal(transport.closes, 1);
  await assert.rejects(manager.connect(), /closed/);
  await manager.close();
  assert.equal(transport.closes, 1);
});

test("close promptly cancels a hanging MCP connection and closes its transport exactly once", async () => {
  const transport = new HangingStartTransport();
  transport.rejectClose = true;
  const manager = new McpClientManager([{
    serverId: "mcp.hanging",
    displayName: "Hanging MCP",
    transport: "stdio",
    toolAllowlist: [],
    approvalMode: "never",
    executionMode: "direct",
  }], () => transport);

  const connecting = manager.connect();
  await new Promise<void>((resolve) => setImmediate(resolve));
  await manager.close();
  await assert.rejects(connecting, /closed during connection/);
  assert.equal(transport.starts, 1);
  assert.equal(transport.closes, 1);
});

test("coalesces the SDK and manager close paths after MCP initialization fails", async () => {
  const transport = new RejectingInitializeTransport();
  const manager = new McpClientManager([{
    serverId: "mcp.initialize-failure",
    displayName: "Initialize failure MCP",
    transport: "stdio",
    toolAllowlist: [],
    approvalMode: "never",
    executionMode: "direct",
  }], () => transport);

  await assert.rejects(manager.connect(), /controlled MCP initialize failure/);
  await manager.close();
  assert.equal(transport.closes, 1);
});

test("close cancels tool discovery even when the server never answers listTools", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  let markListStarted!: () => void;
  const listStarted = new Promise<void>((resolve) => {
    markListStarted = resolve;
  });
  const server = new Server(
    { name: "hanging-list-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  server.setRequestHandler(ListToolsRequestSchema, async () => {
    markListStarted();
    return new Promise<never>(() => undefined);
  });
  await server.connect(serverTransport);
  const manager = new McpClientManager([{
    serverId: "mcp.hanging-list",
    displayName: "Hanging list MCP",
    transport: "stdio",
    toolAllowlist: [],
    approvalMode: "never",
    executionMode: "direct",
  }], () => clientTransport);

  const connecting = manager.connect();
  await listStarted;
  await manager.close();
  await assert.rejects(connecting, /closed|abort/i);
  await server.close();
});

test("closes a transport returned by a factory that reentrantly closes the manager", async () => {
  const transport = new HangingStartTransport();
  let manager!: McpClientManager;
  manager = new McpClientManager([{
    serverId: "mcp.reentrant-factory-close",
    displayName: "Reentrant factory close MCP",
    transport: "stdio",
    toolAllowlist: [],
    approvalMode: "never",
    executionMode: "direct",
  }], () => {
    void manager.close();
    return transport;
  });

  await assert.rejects(manager.connect(), /closed during connection/);
  await manager.close();
  assert.equal(transport.starts, 0);
  assert.equal(transport.closes, 1);
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

test("fences tool closures and later connects after an unexpected transport exit", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const closeTransport = clientTransport.close.bind(clientTransport);
  let transportCloses = 0;
  const countedClientTransport = new Proxy(clientTransport, {
    get(target, property) {
      if (property === "close") {
        return async () => {
          ++transportCloses;
          await closeTransport();
        };
      }
      const value: unknown = Reflect.get(target, property, target);
      return (property === "start" || property === "send") && typeof value === "function"
        ? value.bind(target)
        : value;
    },
    set(target, property, value) {
      return Reflect.set(target, property, value, target);
    },
  });
  const server = new Server(
    { name: "closing-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{ name: "echo", inputSchema: { type: "object", properties: {} } }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => ({
    content: [{ type: "text", text: "unexpected" }],
  }));
  await server.connect(serverTransport);
  let approvalRequests = 0;
  const manager = new McpClientManager([{
    serverId: "mcp.closing",
    displayName: "Closing MCP",
    transport: "stdio",
    toolAllowlist: ["echo"],
    approvalMode: "always",
    executionMode: "direct",
  }], () => countedClientTransport, async () => {
    ++approvalRequests;
    return true;
  });
  const [tool] = await manager.connect();
  assert.ok(tool !== undefined);

  clientTransport.onclose?.();
  await new Promise<void>((resolve) => setImmediate(resolve));
  await assert.rejects(manager.connect(), /closed/);
  await assert.rejects(
    tool.execute("after-close", {}, undefined, undefined, {} as never),
    /closed/,
  );
  assert.equal(approvalRequests, 0);
  await manager.close();
  assert.equal(transportCloses, 1);
  await server.close();
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

test("an empty MCP allowlist exposes no tools and cannot be widened by approval", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "empty-allowlist-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  let approvalRequests = 0;
  let calls = 0;
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{ name: "write_anything", inputSchema: { type: "object", properties: {} } }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => {
    calls += 1;
    return { content: [{ type: "text", text: "unexpected" }] };
  });
  await server.connect(serverTransport);
  const manager = new McpClientManager([{
    serverId: "mcp.empty",
    displayName: "Empty allowlist MCP",
    transport: "stdio",
    toolAllowlist: [],
    approvalMode: "always",
    executionMode: "direct",
  }], () => clientTransport, async () => {
    approvalRequests += 1;
    return true;
  });
  try {
    assert.deepEqual(await manager.connect(), []);
    assert.equal(approvalRequests, 0);
    assert.equal(calls, 0);
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

test("always approval is requested for every call and denial does not poison a later approval", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "retry-approval-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  let calls = 0;
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{ name: "write_once", inputSchema: { type: "object", properties: {} } }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => {
    calls += 1;
    return { content: [{ type: "text", text: "done" }] };
  });
  await server.connect(serverTransport);
  const decisions = [false, true, true];
  let approvals = 0;
  const manager = new McpClientManager([{
    serverId: "mcp.retry-approval",
    displayName: "Retry approval MCP",
    transport: "stdio",
    toolAllowlist: ["write_once"],
    approvalMode: "always",
    executionMode: "direct",
  }], () => clientTransport, async () => decisions[approvals++] ?? false);
  try {
    const [tool] = await manager.connect();
    assert.ok(tool);
    await assert.rejects(
      tool.execute("call-denied", {}, undefined, undefined, {} as never),
      /not approved/,
    );
    assert.equal(calls, 0);
    await tool.execute("call-approved-1", {}, undefined, undefined, {} as never);
    await tool.execute("call-approved-2", {}, undefined, undefined, {} as never);
    assert.equal(approvals, 3);
    assert.equal(calls, 2);
  } finally {
    await manager.close();
    await server.close();
  }
});

test("caller and manager cancellation fence approval before any MCP side effect", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new Server(
    { name: "cancelled-approval-mcp", version: "1.0.0" },
    { capabilities: { tools: {} } },
  );
  let calls = 0;
  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: [{ name: "write_once", inputSchema: { type: "object", properties: {} } }],
  }));
  server.setRequestHandler(CallToolRequestSchema, () => {
    ++calls;
    return { content: [{ type: "text", text: "unexpected" }] };
  });
  await server.connect(serverTransport);
  let approvalRequests = 0;
  let markApprovalStarted!: () => void;
  const approvalStarted = new Promise<void>((resolve) => {
    markApprovalStarted = resolve;
  });
  let resolveApproval!: (approved: boolean) => void;
  const approvalDecision = new Promise<boolean>((resolve) => {
    resolveApproval = resolve;
  });
  const manager = new McpClientManager([{
    serverId: "mcp.cancelled-approval",
    displayName: "Cancelled approval MCP",
    transport: "stdio",
    toolAllowlist: ["write_once"],
    approvalMode: "always",
    executionMode: "direct",
  }], () => clientTransport, async (_request, signal) => {
    ++approvalRequests;
    assert.equal(signal.aborted, false);
    markApprovalStarted();
    return approvalDecision;
  });
  const [tool] = await manager.connect();
  assert.ok(tool);

  const alreadyCancelled = new AbortController();
  alreadyCancelled.abort();
  await assert.rejects(
    tool.execute("pre-cancelled", {}, alreadyCancelled.signal, undefined, {} as never),
    /cancelled/,
  );
  assert.equal(approvalRequests, 0);

  const executing = tool.execute("cancel-during-approval", {}, undefined, undefined, {} as never);
  await approvalStarted;
  await manager.close();
  await assert.rejects(executing, /approval was cancelled/);
  resolveApproval(true);
  assert.equal(approvalRequests, 1);
  assert.equal(calls, 0);
  await server.close();
});
