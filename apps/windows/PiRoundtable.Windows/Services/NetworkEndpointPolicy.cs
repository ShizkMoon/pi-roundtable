using System.Net;
using System.Net.Sockets;

namespace PiRoundtable.Windows.Services;

internal static class NetworkEndpointPolicy
{
    public static Uri RequireBaseUri(string? configuredEndpoint, string defaultBase)
    {
        var raw = string.IsNullOrWhiteSpace(configuredEndpoint) ? defaultBase : configuredEndpoint;
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
        if (value.Length > 2048 ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            value.Contains('?') ||
            value.Contains('#') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsCanonicalLoopback(uri))))
        {
            return false;
        }
        endpoint = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    private static bool IsCanonicalLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var host = uri.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => address.GetAddressBytes()[0] == 127,
            AddressFamily.InterNetworkV6 => address.Equals(IPAddress.IPv6Loopback),
            _ => false,
        };
    }
}
