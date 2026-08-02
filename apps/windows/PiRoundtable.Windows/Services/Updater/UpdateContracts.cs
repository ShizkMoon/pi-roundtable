using System.Text.Json.Serialization;

namespace PiRoundtable.Windows.Services.Updater;

internal enum UpdateAvailability
{
    UpToDate,
    Available,
}
internal sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    Version CurrentVersion,
    Version AvailableVersion,
    VerifiedUpdateManifest Manifest);

internal sealed record VerifiedUpdateManifest(
    UpdateManifestDocument Document,
    Version Version,
    DateTimeOffset PublishedAt,
    Uri AssetUri,
    byte[] ExpectedSha256);

internal sealed record StagedUpdatePackage(
    VerifiedUpdateManifest Manifest,
    string PackagePath);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UpdateManifestDocument
{
    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; set; }

    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; set; } = string.Empty;

    [JsonPropertyName("asset")]
    public UpdateAssetDocument Asset { get; set; } = new();

    [JsonPropertyName("signature")]
    public UpdateSignatureDocument Signature { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UpdateAssetDocument
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("authenticodeRequired")]
    public bool AuthenticodeRequired { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UpdateSignatureDocument
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = string.Empty;

    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
