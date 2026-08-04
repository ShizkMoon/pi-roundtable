using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PiRoundtable.Windows.Services.Updater;

internal sealed record UpdateManifestPolicy(
    string ProductId,
    string Channel,
    string Architecture,
    IReadOnlyDictionary<string, string> TrustedPublicKeys,
    long MaximumAssetBytes = 2L * 1024 * 1024 * 1024,
    bool AllowLoopbackHttp = false,
    Uri? ReleaseAssetBaseUri = null,
    Version? MinimumAuthenticodeVersion = null);

internal sealed partial class UpdateManifestVerifier(UpdateManifestPolicy policy)
{
    public const int MaximumManifestBytes = 128 * 1024;
    public const string SignatureAlgorithm = "ECDSA_P256_SHA256";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public VerifiedUpdateManifest ParseAndVerify(ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        if (json.IsEmpty || json.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("更新清单为空或超过大小限制。");
        }

        RejectDuplicateProperties(json);
        var document = JsonSerializer.Deserialize<UpdateManifestDocument>(json, SerializerOptions)
            ?? throw new InvalidDataException("更新清单无法解析。");
        if (document.Asset is null || document.Signature is null)
        {
            throw new InvalidDataException("更新清单缺少工件或签名对象。");
        }

        ValidateCanonicalField(document.ProductId, nameof(document.ProductId));
        ValidateCanonicalField(document.Channel, nameof(document.Channel));
        ValidateCanonicalField(document.Architecture, nameof(document.Architecture));
        ValidateCanonicalField(document.Version, nameof(document.Version));
        ValidateCanonicalField(document.PublishedAt, nameof(document.PublishedAt));
        ValidateCanonicalField(document.Asset.Url, nameof(document.Asset.Url));
        ValidateCanonicalField(document.Asset.FileName, nameof(document.Asset.FileName));
        ValidateCanonicalField(document.Asset.Sha256, nameof(document.Asset.Sha256));
        ValidateCanonicalField(document.Signature.Algorithm, nameof(document.Signature.Algorithm));
        ValidateCanonicalField(document.Signature.KeyId, nameof(document.Signature.KeyId));

        if (document.ManifestVersion != 1 ||
            !string.Equals(document.ProductId, policy.ProductId, StringComparison.Ordinal) ||
            !string.Equals(document.Channel, policy.Channel, StringComparison.Ordinal) ||
            !string.Equals(document.Architecture, policy.Architecture, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新清单与当前产品、通道或架构不匹配。");
        }
        if (!string.Equals(document.Signature.Algorithm, SignatureAlgorithm, StringComparison.Ordinal) ||
            !policy.TrustedPublicKeys.TryGetValue(document.Signature.KeyId, out var publicKeyPem))
        {
            throw new CryptographicException("更新清单使用了不受信任的签名算法或密钥。");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(document.Signature.Value);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("更新清单签名不是有效的 Base64。", exception);
        }
        if (signature.Length != 64)
        {
            throw new CryptographicException("更新清单签名长度无效。");
        }

        using (var signer = ECDsa.Create())
        {
            signer.ImportFromPem(publicKeyPem);
            var canonical = UpdateManifestCanonicalizer.Canonicalize(document);
            if (!signer.VerifyData(
                    canonical,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new CryptographicException("更新清单签名验证失败。");
            }
        }

        if (!ThreePartVersionRegex().IsMatch(document.Version) ||
            !Version.TryParse(document.Version, out var version) ||
            version.Major > 255 ||
            version.Minor > 255 ||
            version.Build > 65535)
        {
            throw new InvalidDataException("更新版本必须是无前导零且符合 Windows Installer 范围的三段数字版本号。");
        }
        if (!DateTimeOffset.TryParseExact(
                document.PublishedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var publishedAt) ||
            publishedAt > now.AddMinutes(10))
        {
            throw new InvalidDataException("更新发布时间无效或来自未来。");
        }
        if (policy.MinimumAuthenticodeVersion is { } minimumAuthenticodeVersion &&
            version >= minimumAuthenticodeVersion &&
            !document.Asset.AuthenticodeRequired)
        {
            throw new InvalidDataException("该版本的更新包必须要求 Windows Authenticode 验证。");
        }
        if (document.Asset.Size <= 0 || document.Asset.Size > policy.MaximumAssetBytes)
        {
            throw new InvalidDataException("更新包大小无效或超过限制。");
        }
        if (!Sha256Regex().IsMatch(document.Asset.Sha256))
        {
            throw new InvalidDataException("更新包 SHA-256 格式无效。");
        }
        if (!IsSafeMsiFileName(document.Asset.FileName))
        {
            throw new InvalidDataException("更新包文件名无效。");
        }
        var expectedFileName = $"PiRoundtable-{document.Version}-win-{policy.Architecture}.msi";
        if (!string.Equals(document.Asset.FileName, expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新包文件名与版本或架构不匹配。");
        }
        if (!Uri.TryCreate(document.Asset.Url, UriKind.Absolute, out var assetUri) ||
            !IsAllowedAssetUri(assetUri, document.Version, expectedFileName))
        {
            throw new InvalidDataException("更新包地址不属于受信任的版本化发布目录。");
        }

        return new VerifiedUpdateManifest(
            document,
            version,
            publishedAt,
            assetUri,
            Convert.FromHexString(document.Asset.Sha256));
    }

    internal static bool IsAllowedNetworkUri(Uri uri, bool allowLoopbackHttp, bool allowQuery)
    {
        return uri.IsAbsoluteUri &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            (allowQuery || string.IsNullOrEmpty(uri.Query)) &&
            (uri.Scheme == Uri.UriSchemeHttps ||
             (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
    }

    private bool IsAllowedAssetUri(Uri uri, string version, string expectedFileName)
    {
        if (policy.AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return IsAllowedNetworkUri(uri, allowLoopbackHttp: true, allowQuery: false);
        }
        if (!IsAllowedNetworkUri(uri, allowLoopbackHttp: false, allowQuery: false) ||
            policy.ReleaseAssetBaseUri is not { } baseUri ||
            !IsAllowedNetworkUri(baseUri, allowLoopbackHttp: false, allowQuery: false) ||
            !baseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var expectedUri = new Uri(baseUri, $"v{version}/{expectedFileName}");
        return string.Equals(uri.AbsoluteUri, expectedUri.AbsoluteUri, StringComparison.Ordinal);
    }

    private static bool IsSafeMsiFileName(string value)
    {
        return value.Length is > 4 and <= 120 &&
            string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
            !value.Any(character => Path.GetInvalidFileNameChars().Contains(character)) &&
            value.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateCanonicalField(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"更新清单字段 {fieldName} 无效。");
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        using var parsed = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        Visit(parsed.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException($"更新清单包含重复字段：{property.Name}。");
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreePartVersionRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}

internal static class UpdateManifestCanonicalizer
{
    public static byte[] Canonicalize(UpdateManifestDocument document)
    {
        var text = string.Join('\n',
        [
            $"manifestVersion={document.ManifestVersion}",
            $"productId={document.ProductId}",
            $"channel={document.Channel}",
            $"architecture={document.Architecture}",
            $"version={document.Version}",
            $"publishedAt={document.PublishedAt}",
            $"asset.url={document.Asset.Url}",
            $"asset.fileName={document.Asset.FileName}",
            $"asset.size={document.Asset.Size.ToString(CultureInfo.InvariantCulture)}",
            $"asset.sha256={document.Asset.Sha256.ToUpperInvariant()}",
            $"asset.authenticodeRequired={document.Asset.AuthenticodeRequired.ToString().ToLowerInvariant()}",
            $"signature.algorithm={document.Signature.Algorithm}",
            $"signature.keyId={document.Signature.KeyId}",
        ]) + "\n";
        return Encoding.UTF8.GetBytes(text);
    }
}
