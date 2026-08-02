import {
  EnvHttpProxyAgent,
  fetch as undiciFetch,
  type Dispatcher,
} from "undici";

type DispatcherFactory = (options: ConstructorParameters<typeof EnvHttpProxyAgent>[0]) => Dispatcher;

export interface ProviderTransportOptions {
  /** A caller-owned transport, primarily for deterministic tests and embedded hosts. */
  providerFetch?: typeof globalThis.fetch;
  /** Test seam; production always uses Node's fetch with an explicit dispatcher. */
  baseFetch?: typeof globalThis.fetch;
  environment?: Readonly<NodeJS.ProcessEnv>;
  dispatcherFactory?: DispatcherFactory;
}

export interface ProviderTransport {
  fetch: typeof globalThis.fetch;
  close(): Promise<void>;
}

/**
 * Creates the provider HTTP boundary owned by one Pi session.
 *
 * Node's process-global Undici dispatcher is mutable: importing another Undici
 * distribution can replace the proxy-aware dispatcher installed by Node 24.
 * Passing an EnvHttpProxyAgent on every request keeps meeting connectivity
 * deterministic even when Pi or MCP dependencies mutate that global state.
 */
export function createProviderTransport(
  options: ProviderTransportOptions = {},
): ProviderTransport {
  if (options.providerFetch !== undefined) {
    return { fetch: options.providerFetch, close: async () => undefined };
  }

  const environment = options.environment ?? process.env;
  const httpProxy = readEnvironmentValue(environment, "HTTP_PROXY", "http_proxy");
  const httpsProxy = readEnvironmentValue(environment, "HTTPS_PROXY", "https_proxy");
  if (httpProxy === undefined && httpsProxy === undefined) {
    return {
      fetch: options.baseFetch ?? globalThis.fetch.bind(globalThis),
      close: async () => undefined,
    };
  }

  const noProxy = readEnvironmentValue(environment, "NO_PROXY", "no_proxy");
  const dispatcher = (options.dispatcherFactory ?? ((agentOptions) =>
    new EnvHttpProxyAgent(agentOptions)))({
      ...(httpProxy === undefined ? {} : { httpProxy }),
      ...(httpsProxy === undefined ? {} : { httpsProxy }),
      ...(noProxy === undefined ? {} : { noProxy }),
    });
  // The dispatcher and fetch implementation must come from the same Undici
  // distribution. Node's built-in fetch and npm Undici have intentionally
  // different dispatcher handler contracts in Node 24.
  const baseFetch = options.baseFetch ?? fetchWithBundledUndici;
  const fetchWithSessionDispatcher: typeof globalThis.fetch = (input, init) => {
    const requestInit = { ...init, dispatcher } as RequestInit & { dispatcher: Dispatcher };
    return baseFetch(input, requestInit);
  };
  let closePromise: Promise<void> | undefined;
  return {
    fetch: fetchWithSessionDispatcher,
    close: () => {
      closePromise ??= dispatcher.close();
      return closePromise;
    },
  };
}

function fetchWithBundledUndici(
  input: Parameters<typeof globalThis.fetch>[0],
  init?: Parameters<typeof globalThis.fetch>[1],
): ReturnType<typeof globalThis.fetch> {
  // Both implementations expose the Fetch standard at runtime. Their type
  // packages duplicate Request/Response declarations, so this narrow adapter
  // is the only place where the equivalent shapes are bridged.
  return undiciFetch(
    input as Parameters<typeof undiciFetch>[0],
    init as Parameters<typeof undiciFetch>[1],
  ) as unknown as ReturnType<typeof globalThis.fetch>;
}

function readEnvironmentValue(
  environment: Readonly<NodeJS.ProcessEnv>,
  primaryName: string,
  fallbackName: string,
): string | undefined {
  const value = environment[primaryName] ?? environment[fallbackName];
  const trimmed = value?.trim();
  return trimmed === undefined || trimmed.length === 0 ? undefined : trimmed;
}
