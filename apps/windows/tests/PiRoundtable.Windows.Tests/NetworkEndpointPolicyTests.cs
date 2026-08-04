using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class NetworkEndpointPolicyTests
{
    [TestMethod]
    [DataRow("https://api.example.com/v1")]
    [DataRow("http://localhost:4317")]
    [DataRow("http://127.0.2.4:4317/v1")]
    [DataRow("http://127.255.255.255")]
    [DataRow("http://[::1]:4317")]
    public void Accepts_https_and_canonical_loopback_http(string value)
    {
        Assert.IsTrue(NetworkEndpointPolicy.TryNormalize(value, out var normalized));
        Assert.IsNotNull(normalized);
    }

    [TestMethod]
    [DataRow("https://api.example.com/v1?token=secret")]
    [DataRow("https://api.example.com/v1?")]
    [DataRow("https://api.example.com/v1#fragment")]
    [DataRow("https://api.example.com/v1#")]
    [DataRow("https://api.example.com/v1 x")]
    [DataRow("http://example.com/v1")]
    [DataRow("http://0.0.0.0:4317")]
    [DataRow("http://[::]:4317")]
    [DataRow("http://[::ffff:127.0.0.1]:4317")]
    [DataRow("https://user:password@example.com/v1")]
    public void Rejects_noncanonical_or_credential_bearing_endpoints(string value)
    {
        Assert.IsFalse(NetworkEndpointPolicy.TryNormalize(value, out var normalized));
        Assert.IsNull(normalized);
    }

    [TestMethod]
    public void Rejects_endpoint_longer_than_the_public_schema_limit()
    {
        Assert.IsFalse(NetworkEndpointPolicy.TryNormalize(
            $"https://api.example.com/{new string('x', 2049)}",
            out _));
    }
}
