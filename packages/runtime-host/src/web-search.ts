import type { ToolDefinition } from "@earendil-works/pi-coding-agent";
import { Type } from "@earendil-works/pi-ai";

export const WEB_SEARCH_TOOL_ID = "provider.web_search" as const;

export interface WebSearchCitation {
  readonly citationId: string;
  readonly url: string;
  readonly title?: string;
  readonly snippet?: string;
}

export interface WebSearchResult {
  readonly summary: string;
  readonly citations: readonly WebSearchCitation[];
  readonly usage?: {
    readonly inputTokens?: number;
    readonly outputTokens?: number;
    readonly searchCalls?: number;
  };
}

export interface WebSearchRequest {
  readonly query: string;
  readonly maximumResults: number;
}

export interface WebSearchProvider {
  readonly providerId: string;
  search(request: WebSearchRequest, signal: AbortSignal): Promise<WebSearchResult>;
}

export interface WebSearchProviderFactoryRequest {
  readonly providerId: string;
  readonly apiFamily: string;
  readonly endpoint?: string;
  readonly modelId: string;
  readonly resolveApiKey: () => string | undefined;
}

export interface WebSearchProviderFactory {
  create(request: WebSearchProviderFactoryRequest): WebSearchProvider | undefined;
}

export class ProviderNativeWebSearchFactory implements WebSearchProviderFactory {
  constructor(private readonly providerFetch: typeof globalThis.fetch = globalThis.fetch.bind(globalThis)) {}

  create(request: WebSearchProviderFactoryRequest): WebSearchProvider | undefined {
    if (request.providerId.toLowerCase() !== "openai" ||
        request.apiFamily !== "openai_responses" ||
        !isOfficialOpenAiEndpoint(request.endpoint)) {
      return undefined;
    }
    return new OpenAiResponsesWebSearchProvider(request, this.providerFetch);
  }
}

class OpenAiResponsesWebSearchProvider implements WebSearchProvider {
  readonly providerId = "openai.responses.web_search";

  constructor(
    private readonly request: WebSearchProviderFactoryRequest,
    private readonly providerFetch: typeof globalThis.fetch,
  ) {}

  async search(request: WebSearchRequest, signal: AbortSignal): Promise<WebSearchResult> {
    const apiKey = this.request.resolveApiKey();
    if (apiKey === undefined || apiKey.length === 0) {
      throw new Error("web_search_credential_unavailable");
    }
    const endpoint = new URL("responses", normalizeOpenAiEndpoint(this.request.endpoint));
    const response = await this.providerFetch(endpoint, {
      method: "POST",
      headers: {
        authorization: `Bearer ${apiKey}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({
        model: this.request.modelId,
        input: request.query,
        tools: [{ type: "web_search_preview", search_context_size: "medium" }],
        tool_choice: "auto",
        max_output_tokens: 1_200,
      }),
      signal,
    });
    if (!response.ok) {
      throw new Error(`web_search_provider_${response.status}`);
    }
    const value = await readBoundedJson(response, 2 * 1024 * 1024, signal);
    return parseOpenAiResponse(value, request.maximumResults);
  }
}

export interface WebSearchToolOptions {
  readonly provider: WebSearchProvider;
  readonly approve?: (toolCallId: string) => Promise<boolean>;
  readonly maximumCalls?: number;
  readonly timeoutMs?: number;
}

export function createWebSearchTool(options: WebSearchToolOptions): ToolDefinition {
  const maximumCalls = boundedInteger(options.maximumCalls ?? 4, 1, 16, "maximumCalls");
  const timeoutMs = boundedInteger(options.timeoutMs ?? 20_000, 1_000, 120_000, "timeoutMs");
  let calls = 0;
  return {
    name: WEB_SEARCH_TOOL_ID,
    label: "Web search",
    description: [
      "Search the public web through the explicitly granted provider capability.",
      "Returns bounded summaries and structured citations.",
      "Queries and private results are visible only to this role session.",
    ].join(" "),
    promptSnippet: "Search the public web with bounded, cited results",
    promptGuidelines: ["Cite search sources using the returned citation URLs."],
    parameters: Type.Object({
      query: Type.String({ minLength: 1, maxLength: 1_024 }),
      maximumResults: Type.Optional(Type.Integer({ minimum: 1, maximum: 8 })),
    }),
    executionMode: "sequential",
    execute: async (toolCallId, rawParameters, signal) => {
      if (calls >= maximumCalls) {
        throw new Error("web_search_budget_exhausted");
      }
      if (options.approve !== undefined && !await options.approve(toolCallId)) {
        throw new Error("web_search_not_approved");
      }
      calls += 1;
      const parameters = rawParameters as { query: string; maximumResults?: number };
      const query = parameters.query.trim();
      if (query.length === 0) {
        throw new Error("web_search_query_empty");
      }
      const maximumResults = boundedInteger(parameters.maximumResults ?? 5, 1, 8, "maximumResults");
      const controller = new AbortController();
      const abort = (): void => controller.abort(signal?.reason);
      if (signal?.aborted === true) {
        abort();
      } else {
        signal?.addEventListener("abort", abort, { once: true });
      }
      const timeout = setTimeout(() => controller.abort(new Error("web_search_timeout")), timeoutMs);
      timeout.unref?.();
      try {
        const result = validateSearchResult(await options.provider.search({
          query,
          maximumResults,
        }, controller.signal));
        return {
          content: [{
            type: "text",
            text: JSON.stringify({
              summary: result.summary,
              citations: result.citations,
              ...(result.usage === undefined ? {} : { usage: result.usage }),
            }),
          }],
          details: {
            providerId: options.provider.providerId,
            citationCount: result.citations.length,
            // No query, result text, URL, or provider-private response is
            // included in details/diagnostics.
            usage: result.usage,
          },
        };
      } finally {
        clearTimeout(timeout);
        signal?.removeEventListener("abort", abort);
      }
    },
  };
}

function validateSearchResult(result: WebSearchResult): WebSearchResult {
  if (typeof result.summary !== "string" || result.summary.includes("\u0000") ||
      Buffer.byteLength(result.summary, "utf8") > 64 * 1024 || result.citations.length > 8) {
    throw new Error("web_search_result_invalid");
  }
  const citations = result.citations.map((citation, index) => {
    let url: URL;
    try {
      url = new URL(citation.url);
    } catch {
      throw new Error("web_search_citation_invalid");
    }
    if (url.protocol !== "https:" || url.username.length > 0 || url.password.length > 0) {
      throw new Error("web_search_citation_invalid");
    }
    return Object.freeze({
      citationId: boundedText(citation.citationId || `citation-${index + 1}`, 128),
      url: url.toString(),
      ...(citation.title === undefined ? {} : { title: boundedText(citation.title, 512) }),
      ...(citation.snippet === undefined ? {} : { snippet: boundedText(citation.snippet, 2_048) }),
    });
  });
  const usage = result.usage === undefined
    ? undefined
    : Object.freeze({
        ...(result.usage.inputTokens === undefined
          ? {}
          : { inputTokens: boundedInteger(result.usage.inputTokens, 0, 100_000_000, "inputTokens") }),
        ...(result.usage.outputTokens === undefined
          ? {}
          : { outputTokens: boundedInteger(result.usage.outputTokens, 0, 100_000_000, "outputTokens") }),
        ...(result.usage.searchCalls === undefined
          ? {}
          : { searchCalls: boundedInteger(result.usage.searchCalls, 0, 1_000, "searchCalls") }),
      });
  return Object.freeze({
    summary: result.summary,
    citations: Object.freeze(citations),
    ...(usage === undefined ? {} : { usage }),
  });
}

function boundedText(value: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximum || value.includes("\u0000")) {
    throw new Error("web_search_result_invalid");
  }
  return value;
}

function boundedInteger(value: number, minimum: number, maximum: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
    throw new RangeError(`${name} is outside its supported range`);
  }
  return value;
}

function parseOpenAiResponse(value: unknown, maximumResults: number): WebSearchResult {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("web_search_provider_response_invalid");
  }
  const record = value as Record<string, unknown>;
  const summaries: string[] = [];
  const citations: WebSearchCitation[] = [];
  const seenUrls = new Set<string>();
  if (Array.isArray(record.output)) {
    for (const item of record.output) {
      if (typeof item !== "object" || item === null || Array.isArray(item)) {
        continue;
      }
      const content = (item as Record<string, unknown>).content;
      if (!Array.isArray(content)) {
        continue;
      }
      for (const part of content) {
        if (typeof part !== "object" || part === null || Array.isArray(part)) {
          continue;
        }
        const partRecord = part as Record<string, unknown>;
        if (typeof partRecord.text === "string") {
          summaries.push(partRecord.text);
        }
        if (!Array.isArray(partRecord.annotations)) {
          continue;
        }
        for (const annotation of partRecord.annotations) {
          if (citations.length >= maximumResults || typeof annotation !== "object" ||
              annotation === null || Array.isArray(annotation)) {
            continue;
          }
          const citation = annotation as Record<string, unknown>;
          if (citation.type !== "url_citation" || typeof citation.url !== "string" ||
              !seenUrls.add(citation.url)) {
            continue;
          }
          citations.push({
            citationId: `source-${citations.length + 1}`,
            url: citation.url,
            ...(typeof citation.title === "string" ? { title: citation.title } : {}),
          });
        }
      }
    }
  }
  const usage = typeof record.usage === "object" && record.usage !== null && !Array.isArray(record.usage)
    ? record.usage as Record<string, unknown>
    : undefined;
  const summary = summaries.join("\n").trim();
  if (summary.length === 0) {
    throw new Error("web_search_provider_response_invalid");
  }
  return {
    summary,
    citations,
    usage: {
      ...(typeof usage?.input_tokens === "number" ? { inputTokens: usage.input_tokens } : {}),
      ...(typeof usage?.output_tokens === "number" ? { outputTokens: usage.output_tokens } : {}),
      searchCalls: 1,
    },
  };
}

function isOfficialOpenAiEndpoint(endpoint: string | undefined): boolean {
  if (endpoint === undefined) {
    return true;
  }
  try {
    const url = new URL(endpoint);
    return url.protocol === "https:" && url.hostname.toLowerCase() === "api.openai.com" &&
      (url.port === "" || url.port === "443") && url.username.length === 0 && url.password.length === 0 &&
      url.search.length === 0 && url.hash.length === 0 &&
      (url.pathname === "/" || url.pathname === "/v1" || url.pathname === "/v1/");
  } catch {
    return false;
  }
}

function normalizeOpenAiEndpoint(endpoint: string | undefined): URL {
  const base = new URL(endpoint ?? "https://api.openai.com/v1/");
  if (base.pathname === "/") {
    base.pathname = "/v1/";
  }
  if (!base.pathname.endsWith("/")) {
    base.pathname += "/";
  }
  return base;
}

async function readBoundedJson(response: Response, maximumBytes: number, signal: AbortSignal): Promise<unknown> {
  const declaredLength = response.headers.get("content-length");
  if (declaredLength !== null) {
    const parsedLength = Number(declaredLength);
    if (!Number.isSafeInteger(parsedLength) || parsedLength < 0 || parsedLength > maximumBytes) {
      throw new Error("web_search_provider_response_too_large");
    }
  }
  if (response.body === null) {
    throw new Error("web_search_provider_response_invalid");
  }
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      if (signal.aborted) {
        throw signal.reason ?? new Error("web_search_cancelled");
      }
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      length += value.byteLength;
      if (length > maximumBytes) {
        throw new Error("web_search_provider_response_too_large");
      }
      chunks.push(value);
    }
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  try {
    return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) as unknown;
  } catch {
    throw new Error("web_search_provider_response_invalid");
  }
}
