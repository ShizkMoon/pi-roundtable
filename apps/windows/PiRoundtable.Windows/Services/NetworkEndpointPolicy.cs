namespace PiRoundtable.Windows.Services;

internal static class NetworkEndpointPolicy
{
    public static Uri RequireBaseUri(string? configuredEndpoint, string defaultBase)
    {
        var raw = string.IsNullOrWhiteSpace(configuredEndpoint) ? defaultBase : configuredEndpoint.Trim();
        if (!TryNormalize(raw, out var normalized) || normalized is null)
        {
            throw new InvalidOperationException("网络端点必须使用 HTTPS 或本机回环 HTTP，且不能包含凭据、查询参数或片段。");
        }
        return new Uri(normalized, UriKind.Absolute);
    }

    public static bool TryNormalize(string value, out string? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return false;
        }
        endpoint = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }
}
