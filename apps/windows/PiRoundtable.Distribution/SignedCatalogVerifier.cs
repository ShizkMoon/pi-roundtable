using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PiRoundtable.Distribution;

/// <summary>
/// Pure, dependency-free verifier for a version 1 signed catalog. All trust
/// anchors, time, identity expectations, and replay state are explicit inputs;
/// verification performs no I/O and mutates no process or machine state.
/// </summary>
public sealed partial class SignedCatalogVerifier
{
    public const int CurrentCatalogVersion = 1;
    public const string SignatureAlgorithm = "ECDSA_P256_SHA256";

    private const int MaximumJsonDepth = 24;
    private const int MaximumTextLength = 2048;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = MaximumJsonDepth,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> RootProperties =
    [
        "catalogVersion",
        "catalogId",
        "catalogKind",
        "channel",
        "architecture",
        "origin",
        "epoch",
        "sequence",
        "issuedAt",
        "expiresAt",
        "assets",
        "signature",
    ];

    private static readonly HashSet<string> AssetProperties =
    [
        "assetId",
        "version",
        "url",
        "fileName",
        "size",
        "sha256",
        "mediaType",
        "authenticodeRequired",
    ];

    private static readonly HashSet<string> SignatureProperties =
    [
        "algorithm",
        "keyId",
        "value",
    ];

    /// <summary>
    /// Verifies an untrusted UTF-8 catalog against trusted policy and caller
    /// supplied time. Rejected input is represented by a stable diagnostic and
    /// never throws due to document-controlled data.
    /// </summary>
    public DistributionVerificationResult<VerifiedSignedCatalog> Verify(
        ReadOnlySpan<byte> json,
        SignedCatalogPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (json.IsEmpty)
        {
            return Reject(DistributionVerificationFailure.EmptyInput, "catalog");
        }
        if (json.Length > policy.MaximumCatalogBytes)
        {
            return Reject(DistributionVerificationFailure.ContentTooLarge, "catalog");
        }

        SignedCatalogDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            var shapeFailure = ValidateShape(parsed.RootElement);
            if (shapeFailure is not null)
            {
                return DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(
                    shapeFailure.Failure,
                    shapeFailure.Field);
            }

            document = JsonSerializer.Deserialize<SignedCatalogDocument>(json, SerializerOptions)
                ?? throw new JsonException("Catalog root cannot be null.");
        }
        catch (JsonException)
        {
            return Reject(DistributionVerificationFailure.MalformedJson, "catalog");
        }
        catch (NotSupportedException)
        {
            return Reject(DistributionVerificationFailure.MalformedJson, "catalog");
        }

        return VerifyDocument(document, policy, now);
    }

    private static DistributionVerificationResult<VerifiedSignedCatalog> VerifyDocument(
        SignedCatalogDocument document,
        SignedCatalogPolicy policy,
        DateTimeOffset now)
    {
        if (document.CatalogVersion != CurrentCatalogVersion)
        {
            return Reject(DistributionVerificationFailure.UnsupportedVersion, "catalogVersion");
        }

        var textFailure = ValidateRootText(document);
        if (textFailure is not null)
        {
            return DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(
                textFailure.Failure,
                textFailure.Field);
        }
        if (document.Signature is null)
        {
            return Reject(DistributionVerificationFailure.MissingField, "signature");
        }
        if (document.Assets is null || document.Assets.Count == 0)
        {
            return Reject(DistributionVerificationFailure.MissingField, "assets");
        }
        if (document.Assets.Count > policy.MaximumAssets)
        {
            return Reject(DistributionVerificationFailure.AssetCountExceeded, "assets");
        }

        if (!string.Equals(document.CatalogId, policy.CatalogId, StringComparison.Ordinal) ||
            !string.Equals(document.CatalogKind, policy.CatalogKind, StringComparison.Ordinal) ||
            !string.Equals(document.Channel, policy.Channel, StringComparison.Ordinal) ||
            !string.Equals(document.Architecture, policy.Architecture, StringComparison.Ordinal))
        {
            return Reject(DistributionVerificationFailure.IdentityMismatch, "identity");
        }
        if (!string.Equals(document.Origin, policy.Origin.AbsoluteUri, StringComparison.Ordinal))
        {
            return Reject(DistributionVerificationFailure.AssetOriginMismatch, "origin");
        }

        var timeFailure = ValidateTimes(document, policy, now, out var issuedAt, out var expiresAt);
        if (timeFailure is not null)
        {
            return DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(
                timeFailure.Failure,
                timeFailure.Field);
        }

        var rollbackFailure = ValidateRollback(document, policy.RollbackFloor);
        if (rollbackFailure is not null)
        {
            return DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(
                rollbackFailure.Failure,
                rollbackFailure.Field);
        }

        var assetFailure = ValidateAssets(document.Assets, policy, out var verifiedAssets);
        if (assetFailure is not null)
        {
            return DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(
                assetFailure.Failure,
                assetFailure.Field);
        }

        var signatureFailure = VerifySignature(document, policy, issuedAt, now);
        if (signatureFailure is not null)
        {
            return DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(
                signatureFailure.Failure,
                signatureFailure.Field);
        }

        var verified = new VerifiedSignedCatalog(
            document.CatalogId,
            document.CatalogKind,
            document.Channel,
            document.Architecture,
            policy.Origin,
            document.Epoch,
            document.Sequence,
            issuedAt,
            expiresAt,
            document.Signature.KeyId,
            verifiedAssets.AsReadOnly(),
            new CatalogRollbackFloor(document.Epoch, document.Sequence));
        return DistributionVerificationResult<VerifiedSignedCatalog>.Verified(verified);
    }

    private static DistributionVerificationDiagnostic? ValidateShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Diagnostic(DistributionVerificationFailure.MalformedJson, "catalog");
        }

        var rootFailure = ValidateObjectShape(root, RootProperties, "catalog");
        if (rootFailure is not null)
        {
            return rootFailure;
        }

        if (root.TryGetProperty("assets", out var assets))
        {
            if (assets.ValueKind != JsonValueKind.Array)
            {
                return Diagnostic(DistributionVerificationFailure.MalformedJson, "assets");
            }
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    return Diagnostic(DistributionVerificationFailure.MalformedJson, "assets");
                }
                var failure = ValidateObjectShape(asset, AssetProperties, "assets");
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        if (root.TryGetProperty("signature", out var signature))
        {
            if (signature.ValueKind is JsonValueKind.Null)
            {
                return null;
            }
            if (signature.ValueKind != JsonValueKind.Object)
            {
                return Diagnostic(DistributionVerificationFailure.MalformedJson, "signature");
            }
            return ValidateObjectShape(signature, SignatureProperties, "signature");
        }
        return null;
    }

    private static DistributionVerificationDiagnostic? ValidateObjectShape(
        JsonElement value,
        HashSet<string> allowedProperties,
        string field)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return Diagnostic(DistributionVerificationFailure.DuplicateProperty, field);
            }
            if (!allowedProperties.Contains(property.Name))
            {
                return Diagnostic(DistributionVerificationFailure.UnknownProperty, field);
            }
        }
        if (names.Count != allowedProperties.Count)
        {
            return Diagnostic(DistributionVerificationFailure.MissingField, field);
        }
        return null;
    }

    private static DistributionVerificationDiagnostic? ValidateRootText(SignedCatalogDocument document)
    {
        return ValidateText(document.CatalogId, "catalogId", 128)
            ?? ValidateText(document.CatalogKind, "catalogKind", 64)
            ?? ValidateText(document.Channel, "channel", 64)
            ?? ValidateText(document.Architecture, "architecture", 64)
            ?? ValidateText(document.Origin, "origin", MaximumTextLength)
            ?? ValidateText(document.IssuedAt, "issuedAt", 64)
            ?? ValidateText(document.ExpiresAt, "expiresAt", 64);
    }

    private static DistributionVerificationDiagnostic? ValidateText(
        string? value,
        string field,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Diagnostic(DistributionVerificationFailure.MissingField, field);
        }
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            return Diagnostic(DistributionVerificationFailure.InvalidField, field);
        }
        return null;
    }

    private static DistributionVerificationDiagnostic? ValidateTimes(
        SignedCatalogDocument document,
        SignedCatalogPolicy policy,
        DateTimeOffset now,
        out DateTimeOffset issuedAt,
        out DateTimeOffset expiresAt)
    {
        issuedAt = default;
        expiresAt = default;
        if (!TryParseRoundTripTime(document.IssuedAt, out issuedAt) ||
            !TryParseRoundTripTime(document.ExpiresAt, out expiresAt) ||
            expiresAt <= issuedAt ||
            expiresAt - issuedAt > policy.MaximumLifetime)
        {
            return Diagnostic(DistributionVerificationFailure.InvalidTime, "validity");
        }
        if (issuedAt > now && issuedAt - now > policy.AllowedClockSkew)
        {
            return Diagnostic(DistributionVerificationFailure.NotYetValid, "issuedAt");
        }
        if (expiresAt <= now)
        {
            return Diagnostic(DistributionVerificationFailure.Expired, "expiresAt");
        }
        return null;
    }

    private static bool TryParseRoundTripTime(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);

    private static DistributionVerificationDiagnostic? ValidateRollback(
        SignedCatalogDocument document,
        CatalogRollbackFloor floor)
    {
        if (document.Sequence == 0)
        {
            return Diagnostic(DistributionVerificationFailure.InvalidField, "sequence");
        }
        if (document.Epoch < floor.Epoch)
        {
            return Diagnostic(DistributionVerificationFailure.RollbackEpoch, "epoch");
        }
        if (document.Epoch == floor.Epoch && document.Sequence < floor.Sequence)
        {
            return Diagnostic(DistributionVerificationFailure.RollbackSequence, "sequence");
        }
        if (document.Epoch == floor.Epoch && document.Sequence == floor.Sequence)
        {
            return Diagnostic(DistributionVerificationFailure.DuplicateSequence, "sequence");
        }
        return null;
    }

    private static DistributionVerificationDiagnostic? ValidateAssets(
        IReadOnlyList<SignedCatalogAssetDocument> assets,
        SignedCatalogPolicy policy,
        out List<VerifiedCatalogAsset> verifiedAssets)
    {
        verifiedAssets = new List<VerifiedCatalogAsset>(assets.Count);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var asset in assets)
        {
            if (asset is null)
            {
                return Diagnostic(DistributionVerificationFailure.MissingField, "assets");
            }

            var textFailure = ValidateText(asset.AssetId, "assetId", 128)
                ?? ValidateText(asset.Version, "asset.version", 128)
                ?? ValidateText(asset.Url, "asset.url", MaximumTextLength)
                ?? ValidateText(asset.FileName, "asset.fileName", 120)
                ?? ValidateText(asset.Sha256, "asset.sha256", 64)
                ?? ValidateText(asset.MediaType, "asset.mediaType", 128);
            if (textFailure is not null)
            {
                return textFailure;
            }
            if (!IdentifierRegex().IsMatch(asset.AssetId))
            {
                return Diagnostic(DistributionVerificationFailure.InvalidAssetIdentifier, "assetId");
            }
            if (!IsSemanticVersion(asset.Version))
            {
                return Diagnostic(DistributionVerificationFailure.InvalidAssetVersion, "asset.version");
            }
            if (asset.Size <= 0 || asset.Size > policy.MaximumAssetBytes)
            {
                return Diagnostic(DistributionVerificationFailure.InvalidAssetSize, "asset.size");
            }
            try
            {
                totalSize = checked(totalSize + asset.Size);
            }
            catch (OverflowException)
            {
                return Diagnostic(DistributionVerificationFailure.TotalAssetSizeExceeded, "assets");
            }
            if (totalSize > policy.MaximumTotalAssetBytes)
            {
                return Diagnostic(DistributionVerificationFailure.TotalAssetSizeExceeded, "assets");
            }
            if (!Sha256Regex().IsMatch(asset.Sha256))
            {
                return Diagnostic(DistributionVerificationFailure.InvalidDigest, "asset.sha256");
            }
            if (!IsSafeFileName(asset.FileName))
            {
                return Diagnostic(DistributionVerificationFailure.InvalidFileName, "asset.fileName");
            }
            if (!MediaTypeRegex().IsMatch(asset.MediaType))
            {
                return Diagnostic(DistributionVerificationFailure.InvalidField, "asset.mediaType");
            }

            var uriFailure = ValidateAssetUri(asset.Url, asset.FileName, policy.Origin, out var assetUri);
            if (uriFailure is not null)
            {
                return uriFailure;
            }
            if (!assetIds.Add(asset.AssetId) ||
                !fileNames.Add(asset.FileName) ||
                !urls.Add(assetUri.AbsoluteUri))
            {
                return Diagnostic(DistributionVerificationFailure.DuplicateAsset, "assets");
            }

            verifiedAssets.Add(new VerifiedCatalogAsset(
                asset.AssetId,
                asset.Version,
                assetUri,
                asset.FileName,
                asset.Size,
                asset.Sha256.ToUpperInvariant(),
                asset.MediaType.ToLowerInvariant(),
                asset.AuthenticodeRequired));
        }
        return null;
    }

    private static DistributionVerificationDiagnostic? ValidateAssetUri(
        string rawValue,
        string fileName,
        Uri origin,
        out Uri assetUri)
    {
        assetUri = null!;
        if (rawValue.Contains('\\', StringComparison.Ordinal) ||
            !Uri.TryCreate(rawValue, UriKind.Absolute, out var parsed) ||
            !string.Equals(rawValue, parsed.AbsoluteUri, StringComparison.Ordinal) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return Diagnostic(DistributionVerificationFailure.InvalidAssetUri, "asset.url");
        }
        if (!string.Equals(parsed.Scheme, origin.Scheme, StringComparison.Ordinal) ||
            !string.Equals(parsed.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            parsed.Port != origin.Port)
        {
            return Diagnostic(DistributionVerificationFailure.AssetOriginMismatch, "asset.url");
        }
        if (!parsed.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal))
        {
            return Diagnostic(DistributionVerificationFailure.AssetPathEscape, "asset.url");
        }

        var escapedPath = parsed.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = escapedPath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
        {
            return Diagnostic(DistributionVerificationFailure.AssetPathEscape, "asset.url");
        }
        for (var index = 0; index < segments.Length; index++)
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(segments[index]);
            }
            catch (UriFormatException)
            {
                return Diagnostic(DistributionVerificationFailure.InvalidAssetUri, "asset.url");
            }
            if (decoded is "." or ".." ||
                decoded.Contains('/') ||
                decoded.Contains('\\') ||
                decoded.Any(char.IsControl))
            {
                return Diagnostic(DistributionVerificationFailure.AssetPathEscape, "asset.url");
            }
            if (index == segments.Length - 1 &&
                !string.Equals(decoded, fileName, StringComparison.Ordinal))
            {
                return Diagnostic(DistributionVerificationFailure.InvalidFileName, "asset.fileName");
            }
        }

        assetUri = parsed;
        return null;
    }

    private static DistributionVerificationDiagnostic? VerifySignature(
        SignedCatalogDocument document,
        SignedCatalogPolicy policy,
        DateTimeOffset issuedAt,
        DateTimeOffset now)
    {
        var signature = document.Signature!;
        var textFailure = ValidateText(signature.Algorithm, "signature.algorithm", 64)
            ?? ValidateText(signature.KeyId, "signature.keyId", 128)
            ?? ValidateText(signature.Value, "signature.value", 1024);
        if (textFailure is not null)
        {
            return textFailure;
        }
        if (!string.Equals(signature.Algorithm, SignatureAlgorithm, StringComparison.Ordinal))
        {
            return Diagnostic(DistributionVerificationFailure.UnsupportedAlgorithm, "signature.algorithm");
        }
        if (!policy.TrustedKeys.TryGetValue(signature.KeyId, out var trustedKey))
        {
            return Diagnostic(DistributionVerificationFailure.UnknownKey, "signature.keyId");
        }
        if (now < trustedKey.NotBefore)
        {
            return Diagnostic(DistributionVerificationFailure.KeyNotYetValid, "signature.keyId");
        }
        if (trustedKey.NotAfter is not null && now >= trustedKey.NotAfter)
        {
            return Diagnostic(DistributionVerificationFailure.KeyExpired, "signature.keyId");
        }
        if (trustedKey.RevokedAt is not null && now >= trustedKey.RevokedAt)
        {
            return Diagnostic(DistributionVerificationFailure.KeyRevoked, "signature.keyId");
        }
        if (issuedAt < trustedKey.NotBefore)
        {
            return Diagnostic(DistributionVerificationFailure.KeyNotYetValid, "signature.keyId");
        }
        if (trustedKey.NotAfter is not null && issuedAt >= trustedKey.NotAfter)
        {
            return Diagnostic(DistributionVerificationFailure.KeyExpired, "signature.keyId");
        }
        if (trustedKey.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return Diagnostic(DistributionVerificationFailure.InvalidKeyMaterial, "signature.keyId");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Value);
        }
        catch (FormatException)
        {
            return Diagnostic(DistributionVerificationFailure.InvalidSignatureEncoding, "signature.value");
        }
        if (signatureBytes.Length != 64 ||
            !string.Equals(Convert.ToBase64String(signatureBytes), signature.Value, StringComparison.Ordinal))
        {
            return Diagnostic(DistributionVerificationFailure.InvalidSignatureEncoding, "signature.value");
        }

        try
        {
            using var signer = ECDsa.Create();
            try
            {
                signer.ImportFromPem(trustedKey.PublicKeyPem);
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException)
            {
                return Diagnostic(DistributionVerificationFailure.InvalidKeyMaterial, "signature.keyId");
            }
            var parameters = signer.ExportParameters(includePrivateParameters: false);
            if (signer.KeySize != 256 || parameters.Curve.Oid.Value != P256Oid)
            {
                return Diagnostic(DistributionVerificationFailure.InvalidKeyMaterial, "signature.keyId");
            }

            var canonical = SignedCatalogCanonicalizer.Canonicalize(document);
            bool isValid;
            try
            {
                isValid = signer.VerifyData(
                    canonical,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                return Diagnostic(DistributionVerificationFailure.InvalidSignature, "signature.value");
            }
            if (!isValid)
            {
                return Diagnostic(DistributionVerificationFailure.InvalidSignature, "signature.value");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return Diagnostic(DistributionVerificationFailure.InvalidKeyMaterial, "signature.keyId");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signatureBytes);
        }
        return null;
    }

    private static bool IsSemanticVersion(string value)
    {
        if (!SemanticVersionRegex().IsMatch(value))
        {
            return false;
        }
        var hyphen = value.IndexOf('-');
        if (hyphen < 0)
        {
            return true;
        }
        var plus = value.IndexOf('+', hyphen);
        var prerelease = value[(hyphen + 1)..(plus < 0 ? value.Length : plus)];
        return prerelease.Split('.').All(identifier =>
            identifier.Length > 0 &&
            (!identifier.All(char.IsDigit) || identifier == "0" || identifier[0] != '0'));
    }

    private static bool IsSafeFileName(string value)
    {
        if (value.Length is < 1 or > 120 ||
            value is "." or ".." ||
            value.EndsWith(' ') ||
            value.EndsWith('.') ||
            value.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(value).TrimEnd(' ', '.');
        return stem.Length > 0 && !ReservedWindowsNameRegex().IsMatch(stem);
    }

    private static DistributionVerificationResult<VerifiedSignedCatalog> Reject(
        DistributionVerificationFailure failure,
        string field) =>
        DistributionVerificationResult<VerifiedSignedCatalog>.Rejected(failure, KnownField(field));

    private static DistributionVerificationDiagnostic Diagnostic(
        DistributionVerificationFailure failure,
        string field) =>
        new(failure, KnownField(field));

    private static DistributionVerificationField KnownField(string field) => field switch
    {
        "catalog" => DistributionVerificationField.Catalog,
        "catalogVersion" => DistributionVerificationField.CatalogVersion,
        "catalogId" => DistributionVerificationField.CatalogId,
        "catalogKind" => DistributionVerificationField.CatalogKind,
        "channel" => DistributionVerificationField.Channel,
        "architecture" => DistributionVerificationField.Architecture,
        "identity" => DistributionVerificationField.Identity,
        "origin" => DistributionVerificationField.Origin,
        "epoch" => DistributionVerificationField.Epoch,
        "sequence" => DistributionVerificationField.Sequence,
        "issuedAt" => DistributionVerificationField.IssuedAt,
        "expiresAt" => DistributionVerificationField.ExpiresAt,
        "validity" => DistributionVerificationField.Validity,
        "assets" => DistributionVerificationField.Assets,
        "assetId" => DistributionVerificationField.AssetId,
        "asset.version" => DistributionVerificationField.AssetVersion,
        "asset.url" => DistributionVerificationField.AssetUrl,
        "asset.fileName" => DistributionVerificationField.AssetFileName,
        "asset.size" => DistributionVerificationField.AssetSize,
        "asset.sha256" => DistributionVerificationField.AssetSha256,
        "asset.mediaType" => DistributionVerificationField.AssetMediaType,
        "signature" => DistributionVerificationField.Signature,
        "signature.algorithm" => DistributionVerificationField.SignatureAlgorithm,
        "signature.keyId" => DistributionVerificationField.SignatureKeyId,
        "signature.value" => DistributionVerificationField.SignatureValue,
        _ => throw new InvalidOperationException("Diagnostic fields must be selected from the closed taxonomy."),
    };

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9._-]{0,127})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaTypeRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM(?:[1-9¹²³])|LPT(?:[1-9¹²³]))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsNameRegex();
}

/// <summary>
/// Deterministic version 1 producer canonicalization. Every semantic field,
/// asset order, signature algorithm, and key identifier is bound; only the
/// signature value itself is omitted.
/// </summary>
public static class SignedCatalogCanonicalizer
{
    /// <summary>Produces UTF-8, LF-terminated bytes for ECDSA signing.</summary>
    public static byte[] Canonicalize(SignedCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Assets is null || document.Signature is null)
        {
            throw new ArgumentException("Catalog assets and signature metadata are required.", nameof(document));
        }

        var lines = new List<string>
        {
            $"catalogVersion={document.CatalogVersion.ToString(CultureInfo.InvariantCulture)}",
            $"catalogId={document.CatalogId}",
            $"catalogKind={document.CatalogKind}",
            $"channel={document.Channel}",
            $"architecture={document.Architecture}",
            $"origin={document.Origin}",
            $"epoch={document.Epoch.ToString(CultureInfo.InvariantCulture)}",
            $"sequence={document.Sequence.ToString(CultureInfo.InvariantCulture)}",
            $"issuedAt={document.IssuedAt}",
            $"expiresAt={document.ExpiresAt}",
            $"assets.count={document.Assets.Count.ToString(CultureInfo.InvariantCulture)}",
        };
        for (var index = 0; index < document.Assets.Count; index++)
        {
            var asset = document.Assets[index];
            var prefix = $"assets[{index.ToString(CultureInfo.InvariantCulture)}]";
            lines.Add($"{prefix}.assetId={asset.AssetId}");
            lines.Add($"{prefix}.version={asset.Version}");
            lines.Add($"{prefix}.url={asset.Url}");
            lines.Add($"{prefix}.fileName={asset.FileName}");
            lines.Add($"{prefix}.size={asset.Size.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"{prefix}.sha256={asset.Sha256.ToUpperInvariant()}");
            lines.Add($"{prefix}.mediaType={asset.MediaType.ToLowerInvariant()}");
            lines.Add($"{prefix}.authenticodeRequired={asset.AuthenticodeRequired.ToString().ToLowerInvariant()}");
        }
        lines.Add($"signature.algorithm={document.Signature.Algorithm}");
        lines.Add($"signature.keyId={document.Signature.KeyId}");
        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }
}
