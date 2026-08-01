using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class ProviderModelDiscoveryService
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxPages = 10;
    private const int MaxModels = 5000;
    private readonly HttpClient _httpClient;

    public ProviderModelDiscoveryService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<IReadOnlyList<ProviderModelCandidate>> DiscoverAsync(
        ProviderProfileConfiguration provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("获取模型列表需要 API Key。");
        }

        var family = provider.ApiFamily.Trim().ToLowerInvariant();
        return family switch
        {
            "anthropic_messages" => await DiscoverAnthropicAsync(provider, apiKey, cancellationToken),
            "google_generate_content" => await DiscoverGoogleAsync(provider, apiKey, cancellationToken),
            _ => await DiscoverOpenAiCompatibleAsync(provider, apiKey, cancellationToken),
        };
    }

    private async Task<IReadOnlyList<ProviderModelCandidate>> DiscoverOpenAiCompatibleAsync(
        ProviderProfileConfiguration provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildModelsEndpoint(provider.Endpoint, "https://api.openai.com/v1");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var results = new List<ProviderModelCandidate>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!TryGetString(item, "id", out var id))
                {
                    continue;
                }
                results.Add(new ProviderModelCandidate
                {
                    ModelId = id,
                    DisplayName = id,
                    Capabilities = ["text", "reasoning", "tool_calling"],
                });
            }
        }
        return NormalizeResults(results);
    }

    private async Task<IReadOnlyList<ProviderModelCandidate>> DiscoverAnthropicAsync(
        ProviderProfileConfiguration provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var baseEndpoint = BuildModelsEndpoint(provider.Endpoint, "https://api.anthropic.com/v1");
        var results = new List<ProviderModelCandidate>();
        string? afterId = null;
        for (var page = 0; page < MaxPages && results.Count < MaxModels; page++)
        {
            var endpoint = string.IsNullOrEmpty(afterId)
                ? baseEndpoint
                : AppendQuery(baseEndpoint, "after_id", afterId);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            using var document = await SendForJsonAsync(request, cancellationToken);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!TryGetString(item, "id", out var id))
                    {
                        continue;
                    }
                    var displayName = TryGetString(item, "display_name", out var name) ? name : id;
                    results.Add(new ProviderModelCandidate
                    {
                        ModelId = id,
                        DisplayName = displayName,
                        ContextWindow = TryGetPositiveInt(item, "max_input_tokens"),
                        Capabilities = ["text", "reasoning", "tool_calling"],
                    });
                }
            }
            var hasMore = document.RootElement.TryGetProperty("has_more", out var hasMoreElement) &&
                          hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore || !TryGetString(document.RootElement, "last_id", out afterId))
            {
                break;
            }
        }
        return NormalizeResults(results);
    }

    private async Task<IReadOnlyList<ProviderModelCandidate>> DiscoverGoogleAsync(
        ProviderProfileConfiguration provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var baseEndpoint = BuildModelsEndpoint(provider.Endpoint, "https://generativelanguage.googleapis.com/v1beta");
        var results = new List<ProviderModelCandidate>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages && results.Count < MaxModels; page++)
        {
            var endpoint = string.IsNullOrEmpty(pageToken)
                ? baseEndpoint
                : AppendQuery(baseEndpoint, "pageToken", pageToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("x-goog-api-key", apiKey);
            using var document = await SendForJsonAsync(request, cancellationToken);
            if (document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                {
                    if (!SupportsGenerateContent(item) || !TryGetString(item, "name", out var resourceName))
                    {
                        continue;
                    }
                    var id = resourceName.StartsWith("models/", StringComparison.Ordinal)
                        ? resourceName["models/".Length..]
                        : resourceName;
                    var displayName = TryGetString(item, "displayName", out var name) ? name : id;
                    results.Add(new ProviderModelCandidate
                    {
                        ModelId = id,
                        DisplayName = displayName,
                        ContextWindow = TryGetPositiveInt(item, "inputTokenLimit"),
                        Capabilities = ["text", "reasoning", "tool_calling"],
                    });
                }
            }
            if (!TryGetString(document.RootElement, "nextPageToken", out pageToken))
            {
                break;
            }
        }
        return NormalizeResults(results);
    }

    private async Task<JsonDocument> SendForJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidOperationException("模型列表端点返回了重定向；为避免凭据被转发，客户端已拒绝跟随。");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"模型列表请求失败：HTTP {(int)response.StatusCode}。");
        }
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidOperationException("模型列表响应超过 4 MiB 安全上限。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (buffer.Length + count > MaxResponseBytes)
            {
                throw new InvalidOperationException("模型列表响应超过 4 MiB 安全上限。");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static Uri BuildModelsEndpoint(string? configuredEndpoint, string defaultBase)
    {
        var baseUri = NetworkEndpointPolicy.RequireBaseUri(configuredEndpoint, defaultBase);
        if (baseUri.AbsolutePath.TrimEnd('/').EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }
        return new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/models", UriKind.Absolute);
    }

    private static Uri AppendQuery(Uri endpoint, string name, string value)
    {
        var builder = new UriBuilder(endpoint);
        var prefix = string.IsNullOrEmpty(builder.Query) ? string.Empty : $"{builder.Query.TrimStart('&', '?')}&";
        builder.Query = $"{prefix}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        return builder.Uri;
    }

    private static IReadOnlyList<ProviderModelCandidate> NormalizeResults(
        IEnumerable<ProviderModelCandidate> results) => results
        .GroupBy(item => item.ModelId, StringComparer.Ordinal)
        .Select(group => group.First())
        .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Take(MaxModels)
        .ToArray();

    private static bool SupportsGenerateContent(JsonElement item)
    {
        if (!item.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
        {
            return true;
        }
        return methods.EnumerateArray().Any(method =>
            method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent");
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static int? TryGetPositiveInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt32(out var value) && value > 0
            ? value
            : null;
    }
}
