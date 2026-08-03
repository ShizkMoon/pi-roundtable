using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

namespace PiRoundtable.Distribution;

/// <summary>Monotonic replay floor persisted by the catalog-owning boundary.</summary>
public readonly record struct CatalogRollbackFloor(ulong Epoch, ulong Sequence);

/// <summary>
/// One trusted catalog signing key and its independently configured lifecycle.
/// Revocation is policy state and is therefore effective even for signatures
/// made before <see cref="RevokedAt"/>.
/// </summary>
public sealed record CatalogTrustedKey(
    string KeyId,
    string PublicKeyPem,
    DateTimeOffset NotBefore,
    DateTimeOffset? NotAfter = null,
    DateTimeOffset? RevokedAt = null);

/// <summary>
/// Trusted expectations supplied outside the untrusted document. This type
/// carries no network, filesystem, registry, or process behavior.
/// </summary>
public sealed class SignedCatalogPolicy
{
    private readonly IReadOnlyDictionary<string, CatalogTrustedKey> _trustedKeys;

    public SignedCatalogPolicy(
        string catalogId,
        string catalogKind,
        string channel,
        string architecture,
        Uri origin,
        IEnumerable<CatalogTrustedKey> trustedKeys,
        CatalogRollbackFloor rollbackFloor = default,
        int maximumCatalogBytes = 1024 * 1024,
        int maximumAssets = 256,
        long maximumAssetBytes = 4L * 1024 * 1024 * 1024,
        long maximumTotalAssetBytes = 16L * 1024 * 1024 * 1024,
        TimeSpan? maximumLifetime = null,
        TimeSpan? allowedClockSkew = null)
    {
        CatalogId = RequirePolicyText(catalogId, nameof(catalogId));
        CatalogKind = RequirePolicyText(catalogKind, nameof(catalogKind));
        Channel = RequirePolicyText(channel, nameof(channel));
        Architecture = RequirePolicyText(architecture, nameof(architecture));
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            origin.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(origin.Host) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            !origin.AbsolutePath.EndsWith("/", StringComparison.Ordinal) ||
            origin.AbsolutePath.Contains('\\'))
        {
            throw new ArgumentException(
                "Catalog origin must be an absolute HTTPS URI without credentials, query, or fragment.",
                nameof(origin));
        }

        ArgumentNullException.ThrowIfNull(trustedKeys);
        var keyMap = new Dictionary<string, CatalogTrustedKey>(StringComparer.Ordinal);
        foreach (var key in trustedKeys)
        {
            ArgumentNullException.ThrowIfNull(key);
            var keyId = RequirePolicyText(key.KeyId, nameof(trustedKeys));
            ArgumentException.ThrowIfNullOrWhiteSpace(key.PublicKeyPem);
            if (key.NotAfter is not null && key.NotAfter <= key.NotBefore)
            {
                throw new ArgumentException("Trusted key validity interval is empty.", nameof(trustedKeys));
            }
            var keySnapshot = new CatalogTrustedKey(
                keyId,
                key.PublicKeyPem,
                key.NotBefore,
                key.NotAfter,
                key.RevokedAt);
            if (!keyMap.TryAdd(keyId, keySnapshot))
            {
                throw new ArgumentException("Trusted key identifiers must be unique.", nameof(trustedKeys));
            }
        }
        if (keyMap.Count == 0)
        {
            throw new ArgumentException("At least one trusted key is required.", nameof(trustedKeys));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCatalogBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAssets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAssetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalAssetBytes);
        if (maximumTotalAssetBytes < maximumAssetBytes)
        {
            throw new ArgumentException(
                "Total asset limit cannot be smaller than the per-asset limit.",
                nameof(maximumTotalAssetBytes));
        }

        var lifetime = maximumLifetime ?? TimeSpan.FromDays(31);
        var clockSkew = allowedClockSkew ?? TimeSpan.FromMinutes(10);
        if (lifetime <= TimeSpan.Zero || clockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLifetime),
                "Catalog lifetime must be positive and clock skew cannot be negative.");
        }

        Origin = origin;
        _trustedKeys = new ReadOnlyDictionary<string, CatalogTrustedKey>(keyMap);
        RollbackFloor = rollbackFloor;
        MaximumCatalogBytes = maximumCatalogBytes;
        MaximumAssets = maximumAssets;
        MaximumAssetBytes = maximumAssetBytes;
        MaximumTotalAssetBytes = maximumTotalAssetBytes;
        MaximumLifetime = lifetime;
        AllowedClockSkew = clockSkew;
    }

    public string CatalogId { get; }
    public string CatalogKind { get; }
    public string Channel { get; }
    public string Architecture { get; }
    public Uri Origin { get; }
    public IReadOnlyDictionary<string, CatalogTrustedKey> TrustedKeys => _trustedKeys;
    public CatalogRollbackFloor RollbackFloor { get; }
    public int MaximumCatalogBytes { get; }
    public int MaximumAssets { get; }
    public long MaximumAssetBytes { get; }
    public long MaximumTotalAssetBytes { get; }
    public TimeSpan MaximumLifetime { get; }
    public TimeSpan AllowedClockSkew { get; }

    private static string RequirePolicyText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Policy identifiers must be bounded canonical text.", parameterName);
        }
        return value;
    }
}

/// <summary>A normalized asset whose descriptor was covered by the catalog signature.</summary>
public sealed class VerifiedCatalogAsset
{
    internal VerifiedCatalogAsset(
        string assetId,
        string version,
        Uri assetUri,
        string fileName,
        long size,
        string sha256,
        string mediaType,
        bool authenticodeRequired)
    {
        AssetId = assetId;
        Version = version;
        AssetUri = assetUri;
        FileName = fileName;
        Size = size;
        Sha256 = sha256;
        MediaType = mediaType;
        AuthenticodeRequired = authenticodeRequired;
    }

    public string AssetId { get; }
    public string Version { get; }
    public Uri AssetUri { get; }
    public string FileName { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public string MediaType { get; }
    public bool AuthenticodeRequired { get; }

    /// <summary>Builds the immutable byte-verification target for this asset.</summary>
    public ArtifactVerificationSpec CreateVerificationSpec() =>
        ArtifactVerificationSpec.FromSha256Hex(Size, Sha256);
}

/// <summary>
/// Immutable normalized output of signed-catalog verification. The owning
/// component may persist <see cref="NextRollbackFloor"/> only after accepting
/// the catalog; the verifier itself never writes anti-rollback state.
/// </summary>
public sealed class VerifiedSignedCatalog
{
    internal VerifiedSignedCatalog(
        string catalogId,
        string catalogKind,
        string channel,
        string architecture,
        Uri origin,
        ulong epoch,
        ulong sequence,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string keyId,
        IReadOnlyList<VerifiedCatalogAsset> assets,
        CatalogRollbackFloor nextRollbackFloor)
    {
        CatalogId = catalogId;
        CatalogKind = catalogKind;
        Channel = channel;
        Architecture = architecture;
        Origin = origin;
        Epoch = epoch;
        Sequence = sequence;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        KeyId = keyId;
        Assets = Array.AsReadOnly(assets.ToArray());
        NextRollbackFloor = nextRollbackFloor;
    }

    public string CatalogId { get; }
    public string CatalogKind { get; }
    public string Channel { get; }
    public string Architecture { get; }
    public Uri Origin { get; }
    public ulong Epoch { get; }
    public ulong Sequence { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string KeyId { get; }
    public IReadOnlyList<VerifiedCatalogAsset> Assets { get; }
    public CatalogRollbackFloor NextRollbackFloor { get; }
}

/// <summary>Version 1 signed-catalog JSON contract.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SignedCatalogDocument
{
    [JsonPropertyName("catalogVersion")]
    public int CatalogVersion { get; set; }

    [JsonPropertyName("catalogId")]
    public string CatalogId { get; set; } = string.Empty;

    [JsonPropertyName("catalogKind")]
    public string CatalogKind { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = string.Empty;

    [JsonPropertyName("epoch")]
    public ulong Epoch { get; set; }

    [JsonPropertyName("sequence")]
    public ulong Sequence { get; set; }

    [JsonPropertyName("issuedAt")]
    public string IssuedAt { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public string ExpiresAt { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<SignedCatalogAssetDocument>? Assets { get; set; }

    [JsonPropertyName("signature")]
    public SignedCatalogSignatureDocument? Signature { get; set; }
}

/// <summary>One artifact descriptor covered by a version 1 catalog signature.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SignedCatalogAssetDocument
{
    [JsonPropertyName("assetId")]
    public string AssetId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("authenticodeRequired")]
    public bool AuthenticodeRequired { get; set; }
}

/// <summary>Signature metadata bound into the signed canonical bytes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SignedCatalogSignatureDocument
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = string.Empty;

    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
