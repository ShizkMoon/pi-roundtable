import assert from "node:assert/strict";
import test from "node:test";

import {
  createProviderTransport,
  type ProviderTransportOptions,
} from "../provider-transport.js";

type TestDispatcher = ReturnType<NonNullable<ProviderTransportOptions["dispatcherFactory"]>>;

test("binds an environment proxy dispatcher to every provider request", async () => {
  let capturedInit: { dispatcher?: unknown } | undefined;
  let closeCount = 0;
  const dispatcher = {
    close: async () => {
      closeCount += 1;
    },
  } as unknown as TestDispatcher;
  const transport = createProviderTransport({
    environment: {
      HTTPS_PROXY: "http://127.0.0.1:7897",
      NO_PROXY: "localhost,127.0.0.1",
    },
    dispatcherFactory: (options) => {
      assert.equal(options?.httpsProxy, "http://127.0.0.1:7897");
      assert.equal(options?.noProxy, "localhost,127.0.0.1");
      return dispatcher;
    },
    baseFetch: async (_input, init) => {
      capturedInit = init as { dispatcher?: unknown } | undefined;
      return new Response("ok");
    },
  });

  await transport.fetch("https://api.deepseek.com/chat/completions");
  assert.equal(capturedInit?.dispatcher, dispatcher);
  await transport.close();
  await transport.close();
  assert.equal(closeCount, 1);
});

test("keeps a caller-owned provider fetch independent from environment proxies", async () => {
  const providerFetch: typeof globalThis.fetch = async () => new Response("ok");
  const transport = createProviderTransport({
    providerFetch,
    environment: { HTTPS_PROXY: "http://127.0.0.1:7897" },
    dispatcherFactory: () => {
      throw new Error("A caller-owned transport must not allocate a proxy dispatcher");
    },
  });

  assert.equal(transport.fetch, providerFetch);
  await transport.close();
});

test("uses the base fetch unchanged when no proxy is configured", async () => {
  const baseFetch: typeof globalThis.fetch = async () => new Response("ok");
  const transport = createProviderTransport({ baseFetch, environment: {} });

  assert.equal(transport.fetch, baseFetch);
  await transport.close();
});
