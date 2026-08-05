import assert from "node:assert/strict";
import test from "node:test";

import {
  ProviderNativeWebSearchFactory,
  createWebSearchTool,
  type WebSearchProvider,
} from "../web-search.js";

test("controlled web search returns bounded structured citations without diagnostic content", async () => {
  const requests: string[] = [];
  const provider: WebSearchProvider = {
    providerId: "fixture.search",
    search: (request) => {
      requests.push(request.query);
      return Promise.resolve({
        summary: "A bounded fixture result.",
        citations: [{ citationId: "source-1", url: "https://example.test/source", title: "Source" }],
        usage: { inputTokens: 4, outputTokens: 8, searchCalls: 1 },
      });
    },
  };
  const tool = createWebSearchTool({ provider, maximumCalls: 1 });
  const result = await tool.execute(
    "call-1",
    { query: "private fixture query", maximumResults: 3 },
    undefined,
    undefined,
    {} as never,
  );

  assert.deepEqual(requests, ["private fixture query"]);
  assert.equal(JSON.stringify(result.details).includes("private fixture query"), false);
  assert.equal(JSON.stringify(result.details).includes("example.test"), false);
  assert.match((result.content[0] as { text: string }).text, /https:\/\/example\.test\/source/);
  await assert.rejects(
    tool.execute("call-2", { query: "second" }, undefined, undefined, {} as never),
    /web_search_budget_exhausted/,
  );
});

test("provider native factory fails closed outside reviewed OpenAI Responses routes", () => {
  const factory = new ProviderNativeWebSearchFactory(async () => new Response());
  assert.equal(factory.create({
    providerId: "openai-compatible",
    apiFamily: "openai_responses",
    endpoint: "https://compatible.example/v1/",
    modelId: "model",
    resolveApiKey: () => "secret",
  }), undefined);
  assert.equal(factory.create({
    providerId: "openai",
    apiFamily: "openai_responses",
    endpoint: "https://api.openai.com/v1/",
    modelId: "model",
    resolveApiKey: () => "secret",
  })?.providerId, "openai.responses.web_search");
});
