namespace PiRoundtable.Distribution;

/// <summary>
/// Stable, content-free failure codes shared by signed catalogs, updater
/// manifests, artifact imports, and offline-layout verification. A diagnostic
/// must never contain credentials, raw content, a local path, or an untrusted
/// URI.
/// </summary>
public enum DistributionVerificationFailure
{
    EmptyInput,
    ContentTooLarge,
    MalformedJson,
    DuplicateProperty,
    UnknownProperty,
    UnsupportedVersion,
    MissingField,
    InvalidField,
    IdentityMismatch,
    InvalidTime,
    NotYetValid,
    Expired,
    RollbackEpoch,
    RollbackSequence,
    DuplicateSequence,
    UnsupportedAlgorithm,
    UnknownKey,
    KeyNotYetValid,
    KeyExpired,
    KeyRevoked,
    InvalidKeyMaterial,
    InvalidSignatureEncoding,
    InvalidSignature,
    AssetCountExceeded,
    DuplicateAsset,
    InvalidAssetIdentifier,
    InvalidAssetVersion,
    InvalidAssetSize,
    TotalAssetSizeExceeded,
    InvalidDigest,
    InvalidFileName,
    InvalidAssetUri,
    AssetOriginMismatch,
    AssetPathEscape,
}

/// <summary>
/// Closed protocol locations that may appear in diagnostics. Using an enum
/// prevents rejected JSON, URIs, paths, key material, or credentials from
/// being copied into telemetry by an otherwise well-meaning caller.
/// </summary>
public enum DistributionVerificationField
{
    Catalog,
    CatalogVersion,
    CatalogId,
    CatalogKind,
    Channel,
    Architecture,
    Identity,
    Origin,
    Epoch,
    Sequence,
    IssuedAt,
    ExpiresAt,
    Validity,
    Assets,
    AssetId,
    AssetVersion,
    AssetUrl,
    AssetFileName,
    AssetSize,
    AssetSha256,
    AssetMediaType,
    Signature,
    SignatureAlgorithm,
    SignatureKeyId,
    SignatureValue,
}

/// <summary>
/// A machine-readable rejection that identifies only the protocol field and
/// failure category. <see cref="Field"/> cannot contain document-controlled
/// text by construction.
/// </summary>
public sealed record DistributionVerificationDiagnostic(
    DistributionVerificationFailure Failure,
    DistributionVerificationField? Field = null,
    bool Retryable = false);

/// <summary>
/// Represents either one verified value or one credential-free diagnostic.
/// The mutually exclusive state makes it impossible for a caller to consume a
/// partially verified document as trusted data.
/// </summary>
/// <typeparam name="T">The immutable verified value type.</typeparam>
public sealed class DistributionVerificationResult<T>
    where T : class
{
    private DistributionVerificationResult(
        T? value,
        DistributionVerificationDiagnostic? diagnostic)
    {
        Value = value;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets whether verification produced a trusted value.</summary>
    public bool IsVerified => Value is not null;

    /// <summary>Gets the trusted value, or <see langword="null"/> on rejection.</summary>
    public T? Value { get; }

    /// <summary>Gets the content-free rejection, or <see langword="null"/> on success.</summary>
    public DistributionVerificationDiagnostic? Diagnostic { get; }

    /// <summary>Creates a successful verification result.</summary>
    public static DistributionVerificationResult<T> Verified(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    /// <summary>Creates a rejected verification result.</summary>
    public static DistributionVerificationResult<T> Rejected(
        DistributionVerificationFailure failure,
        DistributionVerificationField? field = null,
        bool retryable = false) =>
        new(null, new DistributionVerificationDiagnostic(failure, field, retryable));
}
