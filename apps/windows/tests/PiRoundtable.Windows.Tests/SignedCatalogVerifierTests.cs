using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiRoundtable.Distribution;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class SignedCatalogVerifierTests
{
    [TestMethod]
    public void Valid_catalog_produces_only_normalized_verified_values()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();

        var result = fixture.Verify(document);

        Assert.IsTrue(result.IsVerified);
        Assert.IsNull(result.Diagnostic);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual("catalog.main", result.Value.CatalogId);
        Assert.AreEqual(new CatalogRollbackFloor(1, 7), result.Value.NextRollbackFloor);
        Assert.AreEqual(fixture.Origin, result.Value.Origin);
        Assert.HasCount(1, result.Value.Assets);
        Assert.AreEqual(document.Assets![0].Sha256.ToUpperInvariant(), result.Value.Assets[0].Sha256);
        Assert.AreEqual(document.Assets[0].MediaType.ToLowerInvariant(), result.Value.Assets[0].MediaType);
        Assert.AreEqual(4L, result.Value.Assets[0].CreateVerificationSpec().ExpectedSize);
    }

    [TestMethod]
    public void Verification_result_never_exposes_rejected_values_or_untrusted_content()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();
        document.Assets![0].Url = "https://user:secret@evil.example.test/module.bin?token=credential";
        fixture.Sign(document);

        var result = fixture.Verify(document);
        var rendered = result.Diagnostic?.ToString() ?? string.Empty;

        Assert.IsFalse(result.IsVerified);
        Assert.IsNull(result.Value);
        AssertFailure(result, DistributionVerificationFailure.InvalidAssetUri);
        Assert.IsFalse(rendered.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("credential", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("evil.example.test", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Empty_oversized_and_non_object_documents_are_rejected_without_throwing()
    {
        using var fixture = new CatalogFixture();

        AssertFailure(
            fixture.Verifier.Verify([], fixture.Policy, fixture.Now),
            DistributionVerificationFailure.EmptyInput);
        AssertFailure(
            fixture.Verifier.Verify(Encoding.UTF8.GetBytes("null"), fixture.Policy, fixture.Now),
            DistributionVerificationFailure.MalformedJson);

        var smallPolicy = fixture.CreatePolicy(maximumCatalogBytes: 32);
        AssertFailure(
            fixture.Verifier.Verify(new byte[33], smallPolicy, fixture.Now),
            DistributionVerificationFailure.ContentTooLarge);
    }

    [TestMethod]
    public void Strict_json_rejects_comments_trailing_commas_wrong_case_and_null_objects()
    {
        using var fixture = new CatalogFixture();
        var validJson = fixture.SerializeSigned(fixture.CreateDocument());
        var text = Encoding.UTF8.GetString(validJson);
        var cases = new (string Json, DistributionVerificationFailure Failure)[]
        {
            (text.Insert(1, "/*comment*/"), DistributionVerificationFailure.MalformedJson),
            (text[..^1] + ",}", DistributionVerificationFailure.MalformedJson),
            (text.Replace("\"catalogId\":", "\"CatalogId\":", StringComparison.Ordinal), DistributionVerificationFailure.UnknownProperty),
            (text.Replace("\"signature\":{", "\"signature\":null,\"discarded\":{", StringComparison.Ordinal), DistributionVerificationFailure.UnknownProperty),
            (text.Replace("\"assets\":[{", "\"assets\":[null,{", StringComparison.Ordinal), DistributionVerificationFailure.MalformedJson),
        };

        foreach (var item in cases)
        {
            var result = fixture.Verifier.Verify(Encoding.UTF8.GetBytes(item.Json), fixture.Policy, fixture.Now);
            AssertFailure(result, item.Failure);
        }
    }

    [TestMethod]
    public void Duplicate_and_unknown_properties_are_rejected_at_every_object_level()
    {
        using var fixture = new CatalogFixture();
        var text = Encoding.UTF8.GetString(fixture.SerializeSigned(fixture.CreateDocument()));
        var cases = new (string Json, DistributionVerificationFailure Failure)[]
        {
            (
                text.Replace(
                    "\"catalogId\":\"catalog.main\"",
                    "\"catalogId\":\"catalog.main\",\"catalogId\":\"catalog.main\"",
                    StringComparison.Ordinal),
                DistributionVerificationFailure.DuplicateProperty),
            (
                text.Replace("\"catalogId\":", "\"unexpected\":true,\"catalogId\":", StringComparison.Ordinal),
                DistributionVerificationFailure.UnknownProperty),
            (
                text.Replace(
                    "\"assetId\":\"module.core\"",
                    "\"assetId\":\"module.core\",\"assetId\":\"module.core\"",
                    StringComparison.Ordinal),
                DistributionVerificationFailure.DuplicateProperty),
            (
                text.Replace("\"assetId\":", "\"unexpectedAsset\":true,\"assetId\":", StringComparison.Ordinal),
                DistributionVerificationFailure.UnknownProperty),
            (
                text.Replace(
                    "\"algorithm\":\"ECDSA_P256_SHA256\"",
                    "\"algorithm\":\"ECDSA_P256_SHA256\",\"algorithm\":\"ECDSA_P256_SHA256\"",
                    StringComparison.Ordinal),
                DistributionVerificationFailure.DuplicateProperty),
            (
                text.Replace("\"algorithm\":", "\"unexpectedSignature\":true,\"algorithm\":", StringComparison.Ordinal),
                DistributionVerificationFailure.UnknownProperty),
        };

        foreach (var item in cases)
        {
            AssertFailure(
                fixture.Verifier.Verify(Encoding.UTF8.GetBytes(item.Json), fixture.Policy, fixture.Now),
                item.Failure);
        }
    }

    [TestMethod]
    public void Version_identity_and_required_objects_are_fail_closed()
    {
        using var fixture = new CatalogFixture();

        var wrongVersion = fixture.CreateDocument();
        wrongVersion.CatalogVersion = 2;
        fixture.Sign(wrongVersion);
        AssertFailure(fixture.Verify(wrongVersion), DistributionVerificationFailure.UnsupportedVersion);

        var wrongIdentity = fixture.CreateDocument();
        wrongIdentity.Channel = "preview";
        fixture.Sign(wrongIdentity);
        AssertFailure(fixture.Verify(wrongIdentity), DistributionVerificationFailure.IdentityMismatch);

        var noAssets = fixture.CreateDocument();
        noAssets.Assets = [];
        fixture.Sign(noAssets);
        AssertFailure(fixture.Verify(noAssets), DistributionVerificationFailure.MissingField);

        var noSignature = fixture.CreateDocument();
        fixture.Sign(noSignature);
        noSignature.Signature = null;
        var noSignatureJson = JsonSerializer.SerializeToUtf8Bytes(noSignature, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AssertFailure(
            fixture.Verifier.Verify(noSignatureJson, fixture.Policy, fixture.Now),
            DistributionVerificationFailure.MissingField);

        var missingAssetField = Encoding.UTF8.GetString(fixture.SerializeSigned(fixture.CreateDocument()))
            .Replace(",\"authenticodeRequired\":true", string.Empty, StringComparison.Ordinal);
        AssertFailure(
            fixture.Verifier.Verify(Encoding.UTF8.GetBytes(missingAssetField), fixture.Policy, fixture.Now),
            DistributionVerificationFailure.MissingField);
    }

    [TestMethod]
    public void Validity_window_rejects_future_expired_inverted_and_overlong_catalogs()
    {
        using var fixture = new CatalogFixture();

        var future = fixture.CreateDocument();
        future.IssuedAt = fixture.Now.AddMinutes(11).ToString("O");
        future.ExpiresAt = fixture.Now.AddDays(1).ToString("O");
        fixture.Sign(future);
        AssertFailure(fixture.Verify(future), DistributionVerificationFailure.NotYetValid);

        var expired = fixture.CreateDocument();
        expired.IssuedAt = fixture.Now.AddDays(-2).ToString("O");
        expired.ExpiresAt = fixture.Now.ToString("O");
        fixture.Sign(expired);
        AssertFailure(fixture.Verify(expired), DistributionVerificationFailure.Expired);

        var inverted = fixture.CreateDocument();
        inverted.IssuedAt = fixture.Now.AddHours(1).ToString("O");
        inverted.ExpiresAt = fixture.Now.ToString("O");
        fixture.Sign(inverted);
        AssertFailure(fixture.Verify(inverted), DistributionVerificationFailure.InvalidTime);

        var overlong = fixture.CreateDocument();
        overlong.IssuedAt = fixture.Now.AddDays(-1).ToString("O");
        overlong.ExpiresAt = fixture.Now.AddDays(31).ToString("O");
        fixture.Sign(overlong);
        AssertFailure(fixture.Verify(overlong), DistributionVerificationFailure.InvalidTime);
    }

    [TestMethod]
    public void Epoch_and_sequence_floor_rejects_replay_but_accepts_epoch_advance()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();

        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(floor: new CatalogRollbackFloor(2, 1))),
            DistributionVerificationFailure.RollbackEpoch);
        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(floor: new CatalogRollbackFloor(1, 8))),
            DistributionVerificationFailure.RollbackSequence);
        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(floor: new CatalogRollbackFloor(1, 7))),
            DistributionVerificationFailure.DuplicateSequence);

        document.Epoch = 2;
        document.Sequence = 1;
        fixture.Sign(document);
        var advanced = fixture.Verify(document, fixture.CreatePolicy(floor: new CatalogRollbackFloor(1, 100)));
        Assert.IsTrue(advanced.IsVerified);
        Assert.AreEqual(new CatalogRollbackFloor(2, 1), advanced.Value!.NextRollbackFloor);
    }

    [TestMethod]
    public void Zero_sequence_and_out_of_range_unsigned_numbers_are_rejected()
    {
        using var fixture = new CatalogFixture();
        var zeroSequence = fixture.CreateDocument();
        zeroSequence.Sequence = 0;
        fixture.Sign(zeroSequence);
        AssertFailure(fixture.VerifyWithoutSigning(zeroSequence), DistributionVerificationFailure.InvalidField);

        var json = Encoding.UTF8.GetString(fixture.SerializeSigned(fixture.CreateDocument()));
        var overflow = json.Replace(
            "\"epoch\":1",
            "\"epoch\":18446744073709551616",
            StringComparison.Ordinal);
        AssertFailure(
            fixture.Verifier.Verify(Encoding.UTF8.GetBytes(overflow), fixture.Policy, fixture.Now),
            DistributionVerificationFailure.MalformedJson);
    }

    [TestMethod]
    public void Signature_binds_algorithm_key_identifier_and_every_catalog_field()
    {
        using var fixture = new CatalogFixture();

        var unsupported = fixture.CreateDocument();
        unsupported.Signature!.Algorithm = "ECDSA_P384_SHA384";
        fixture.Sign(unsupported);
        AssertFailure(fixture.Verify(unsupported), DistributionVerificationFailure.UnsupportedAlgorithm);

        var unknown = fixture.CreateDocument();
        unknown.Signature!.KeyId = "unknown-key";
        fixture.Sign(unknown);
        AssertFailure(fixture.Verify(unknown), DistributionVerificationFailure.UnknownKey);

        var tampered = fixture.CreateDocument();
        fixture.Sign(tampered);
        tampered.Assets![0].Size++;
        AssertFailure(fixture.VerifyWithoutSigning(tampered), DistributionVerificationFailure.InvalidSignature);

        var invalidEncoding = fixture.CreateDocument();
        invalidEncoding.Signature!.Value = "not-base64";
        AssertFailure(fixture.VerifyWithoutSigning(invalidEncoding), DistributionVerificationFailure.InvalidSignatureEncoding);

        var nonCanonicalEncoding = fixture.CreateDocument();
        fixture.Sign(nonCanonicalEncoding);
        nonCanonicalEncoding.Signature!.Value += "\r\n";
        AssertFailure(fixture.VerifyWithoutSigning(nonCanonicalEncoding), DistributionVerificationFailure.InvalidField);
    }

    [TestMethod]
    public void Key_rotation_accepts_new_key_and_revocation_overrides_old_signatures()
    {
        using var fixture = new CatalogFixture();
        using var rotatedSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rotatedKey = new CatalogTrustedKey(
            "catalog-2026-b",
            rotatedSigner.ExportSubjectPublicKeyInfoPem(),
            fixture.Now.AddDays(-1),
            fixture.Now.AddYears(1));
        var activeOldKey = fixture.TrustedKey;
        var rotationPolicy = fixture.CreatePolicy(keys: [activeOldKey, rotatedKey]);

        var newCatalog = fixture.CreateDocument();
        newCatalog.Signature!.KeyId = rotatedKey.KeyId;
        fixture.Sign(newCatalog, rotatedSigner);
        Assert.IsTrue(fixture.VerifyWithoutSigning(newCatalog, rotationPolicy).IsVerified);

        var revokedOldKey = activeOldKey with { RevokedAt = fixture.Now.AddMinutes(-1) };
        var revokedPolicy = fixture.CreatePolicy(keys: [revokedOldKey, rotatedKey]);
        var oldCatalog = fixture.CreateDocument();
        fixture.Sign(oldCatalog);
        AssertFailure(fixture.Verify(oldCatalog, revokedPolicy), DistributionVerificationFailure.KeyRevoked);
        Assert.IsTrue(fixture.VerifyWithoutSigning(newCatalog, revokedPolicy).IsVerified);
    }

    [TestMethod]
    public void Key_lifecycle_and_curve_material_are_enforced_before_acceptance()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();

        var futureKey = fixture.TrustedKey with { NotBefore = fixture.Now.AddMinutes(1) };
        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(keys: [futureKey])),
            DistributionVerificationFailure.KeyNotYetValid);

        var expiredKey = fixture.TrustedKey with { NotAfter = fixture.Now };
        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(keys: [expiredKey])),
            DistributionVerificationFailure.KeyExpired);

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var wrongCurveKey = new CatalogTrustedKey(
            fixture.TrustedKey.KeyId,
            p384.ExportSubjectPublicKeyInfoPem(),
            fixture.Now.AddDays(-1),
            fixture.Now.AddDays(1));
        document.Signature!.Value = Convert.ToBase64String(new byte[64]);
        AssertFailure(
            fixture.VerifyWithoutSigning(document, fixture.CreatePolicy(keys: [wrongCurveKey])),
            DistributionVerificationFailure.InvalidKeyMaterial);

        var privateKeyPolicy = fixture.CreatePolicy(keys:
        [
            fixture.TrustedKey with { PublicKeyPem = fixture.Signer.ExportPkcs8PrivateKeyPem() },
        ]);
        fixture.Sign(document);
        AssertFailure(
            fixture.VerifyWithoutSigning(document, privateKeyPolicy),
            DistributionVerificationFailure.InvalidKeyMaterial);
    }

    [TestMethod]
    public void Key_issue_revocation_and_signature_length_boundaries_are_exact()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();
        var issuedAt = DateTimeOffset.Parse(document.IssuedAt);

        var exactStart = fixture.TrustedKey with { NotBefore = issuedAt };
        Assert.IsTrue(fixture.Verify(document, fixture.CreatePolicy(keys: [exactStart])).IsVerified);

        var endedAtIssue = fixture.TrustedKey with { NotAfter = issuedAt };
        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(keys: [endedAtIssue])),
            DistributionVerificationFailure.KeyExpired);

        var revokedNow = fixture.TrustedKey with { RevokedAt = fixture.Now };
        AssertFailure(
            fixture.Verify(document, fixture.CreatePolicy(keys: [revokedNow])),
            DistributionVerificationFailure.KeyRevoked);

        foreach (var length in new[] { 63, 65 })
        {
            document = fixture.CreateDocument();
            document.Signature!.Value = Convert.ToBase64String(new byte[length]);
            AssertFailure(
                fixture.VerifyWithoutSigning(document),
                DistributionVerificationFailure.InvalidSignatureEncoding);
        }
    }

    [TestMethod]
    public void Clock_skew_and_maximum_lifetime_boundaries_are_exact()
    {
        using var fixture = new CatalogFixture();
        var atSkew = fixture.CreateDocument();
        atSkew.IssuedAt = fixture.Now.AddMinutes(10).ToString("O");
        atSkew.ExpiresAt = fixture.Now.AddDays(1).ToString("O");
        Assert.IsTrue(fixture.Verify(atSkew).IsVerified);

        var beyondSkew = fixture.CreateDocument();
        beyondSkew.IssuedAt = fixture.Now.AddMinutes(10).AddTicks(1).ToString("O");
        beyondSkew.ExpiresAt = fixture.Now.AddDays(1).ToString("O");
        AssertFailure(fixture.Verify(beyondSkew), DistributionVerificationFailure.NotYetValid);

        var exactLifetime = fixture.CreateDocument();
        exactLifetime.IssuedAt = fixture.Now.AddDays(-1).ToString("O");
        exactLifetime.ExpiresAt = fixture.Now.AddDays(30).ToString("O");
        Assert.IsTrue(fixture.Verify(exactLifetime).IsVerified);

        var beyondLifetime = fixture.CreateDocument();
        beyondLifetime.IssuedAt = fixture.Now.AddDays(-1).ToString("O");
        beyondLifetime.ExpiresAt = fixture.Now.AddDays(30).AddTicks(1).ToString("O");
        AssertFailure(fixture.Verify(beyondLifetime), DistributionVerificationFailure.InvalidTime);

        var malformed = fixture.CreateDocument();
        malformed.IssuedAt = "2026-08-03";
        fixture.Sign(malformed);
        AssertFailure(fixture.VerifyWithoutSigning(malformed), DistributionVerificationFailure.InvalidTime);
    }

    [TestMethod]
    public void Asset_uri_requires_canonical_https_same_origin_and_contained_path()
    {
        using var fixture = new CatalogFixture();
        string[] rejectedUris =
        [
            "http://packages.example.test/catalog/module.bin",
            "https://evil.example.test/catalog/module.bin",
            "https://user:pass@packages.example.test/catalog/module.bin",
            "https://packages.example.test/catalog/module.bin?token=secret",
            "https://packages.example.test/catalog/module.bin#fragment",
            "https://packages.example.test/outside/module.bin",
            "https://packages.example.test/catalog/../module.bin",
            "https://packages.example.test/catalog/%2e%2e/module.bin",
            "https://packages.example.test/catalog/nested%2Fmodule.bin",
            "https://packages.example.test/catalog-evil/module.bin",
            "https://packages.example.test/catalog//module.bin",
            "https://packages.example.test/catalog\\module.bin",
        ];

        foreach (var rejectedUri in rejectedUris)
        {
            var document = fixture.CreateDocument();
            document.Assets![0].Url = rejectedUri;
            fixture.Sign(document);
            var result = fixture.VerifyWithoutSigning(document);
            Assert.IsFalse(result.IsVerified, rejectedUri);
            Assert.IsTrue(
                result.Diagnostic!.Failure is
                    DistributionVerificationFailure.InvalidAssetUri or
                    DistributionVerificationFailure.AssetOriginMismatch or
                    DistributionVerificationFailure.AssetPathEscape,
                $"Unexpected failure for {rejectedUri}: {result.Diagnostic.Failure}");
        }
    }

    [TestMethod]
    public void Asset_file_name_version_digest_and_media_type_are_bounded()
    {
        using var fixture = new CatalogFixture();
        var cases = new (Action<SignedCatalogAssetDocument> Mutate, DistributionVerificationFailure Failure)[]
        {
            (asset => asset.AssetId = "Module Core", DistributionVerificationFailure.InvalidAssetIdentifier),
            (asset => asset.Version = "01.2.3", DistributionVerificationFailure.InvalidAssetVersion),
            (asset => asset.Version = "1.2.3-01", DistributionVerificationFailure.InvalidAssetVersion),
            (asset => asset.FileName = "../module.bin", DistributionVerificationFailure.InvalidFileName),
            (asset => asset.FileName = "CON.txt", DistributionVerificationFailure.InvalidFileName),
            (asset => asset.FileName = "CON .txt", DistributionVerificationFailure.InvalidFileName),
            (asset => asset.FileName = "COM¹.txt", DistributionVerificationFailure.InvalidFileName),
            (asset => asset.FileName = "other.bin", DistributionVerificationFailure.InvalidFileName),
            (asset => asset.Sha256 = new string('z', 64), DistributionVerificationFailure.InvalidDigest),
            (asset => asset.Size = 0, DistributionVerificationFailure.InvalidAssetSize),
            (asset => asset.MediaType = "not a media type", DistributionVerificationFailure.InvalidField),
        };

        foreach (var item in cases)
        {
            var document = fixture.CreateDocument();
            item.Mutate(document.Assets![0]);
            fixture.Sign(document);
            AssertFailure(fixture.VerifyWithoutSigning(document), item.Failure);
        }
    }

    [TestMethod]
    public void Every_single_character_non_hex_digest_mutation_is_rejected()
    {
        using var fixture = new CatalogFixture();
        for (var index = 0; index < 64; index++)
        {
            var document = fixture.CreateDocument();
            var digest = document.Assets![0].Sha256.ToCharArray();
            digest[index] = 'z';
            document.Assets[0].Sha256 = new string(digest);
            fixture.Sign(document);

            AssertFailure(
                fixture.VerifyWithoutSigning(document),
                DistributionVerificationFailure.InvalidDigest);
        }
    }

    [TestMethod]
    public void Malformed_utf8_excessive_depth_and_seeded_random_bytes_never_escape_as_exceptions()
    {
        using var fixture = new CatalogFixture();
        AssertFailure(
            fixture.Verifier.Verify([0xff, 0xfe, 0xfd], fixture.Policy, fixture.Now),
            DistributionVerificationFailure.MalformedJson);

        var nested = new string('[', 30) + "0" + new string(']', 30);
        AssertFailure(
            fixture.Verifier.Verify(Encoding.UTF8.GetBytes(nested), fixture.Policy, fixture.Now),
            DistributionVerificationFailure.MalformedJson);

        var random = new Random(0x50495254);
        for (var length = 1; length <= 128; length++)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            var result = fixture.Verifier.Verify(bytes, fixture.Policy, fixture.Now);
            Assert.IsFalse(result.IsVerified, $"Random corpus item {length} unexpectedly verified.");
            Assert.IsNotNull(result.Diagnostic);
        }
    }

    [TestMethod]
    public void Asset_count_size_and_duplicate_boundaries_are_enforced()
    {
        using var fixture = new CatalogFixture();
        var twoAssets = fixture.CreateDocument();
        twoAssets.Assets!.Add(fixture.CreateAsset("module.extra", "extra.bin"));
        fixture.Sign(twoAssets);
        AssertFailure(
            fixture.Verify(twoAssets, fixture.CreatePolicy(maximumAssets: 1)),
            DistributionVerificationFailure.AssetCountExceeded);

        var oversized = fixture.CreateDocument();
        oversized.Assets![0].Size = 5;
        fixture.Sign(oversized);
        AssertFailure(
            fixture.Verify(oversized, fixture.CreatePolicy(maximumAssetBytes: 4, maximumTotalAssetBytes: 8)),
            DistributionVerificationFailure.InvalidAssetSize);

        var total = fixture.CreateDocument();
        total.Assets!.Add(fixture.CreateAsset("module.extra", "extra.bin"));
        fixture.Sign(total);
        AssertFailure(
            fixture.Verify(total, fixture.CreatePolicy(maximumAssetBytes: 4, maximumTotalAssetBytes: 7)),
            DistributionVerificationFailure.TotalAssetSizeExceeded);

        var duplicate = fixture.CreateDocument();
        duplicate.Assets!.Add(fixture.CreateAsset("module.core", "extra.bin"));
        fixture.Sign(duplicate);
        AssertFailure(fixture.VerifyWithoutSigning(duplicate), DistributionVerificationFailure.DuplicateAsset);

        var caseDuplicate = fixture.CreateDocument();
        caseDuplicate.Assets!.Add(fixture.CreateAsset("module.extra", "MODULE.BIN"));
        fixture.Sign(caseDuplicate);
        AssertFailure(fixture.VerifyWithoutSigning(caseDuplicate), DistributionVerificationFailure.DuplicateAsset);
    }

    [TestMethod]
    public void Exact_catalog_asset_count_and_byte_limits_are_accepted()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();
        var json = fixture.SerializeSigned(document);
        var exactPolicy = fixture.CreatePolicy(
            maximumCatalogBytes: json.Length,
            maximumAssets: 1,
            maximumAssetBytes: 4,
            maximumTotalAssetBytes: 4);

        var result = fixture.Verifier.Verify(json, exactPolicy, fixture.Now);

        Assert.IsTrue(result.IsVerified);
        Assert.HasCount(1, result.Value!.Assets);
    }

    [TestMethod]
    public void Reordering_assets_after_signing_invalidates_the_catalog()
    {
        using var fixture = new CatalogFixture();
        var document = fixture.CreateDocument();
        document.Assets!.Add(fixture.CreateAsset("module.extra", "extra.bin"));
        fixture.Sign(document);
        document.Assets.Reverse();

        AssertFailure(fixture.VerifyWithoutSigning(document), DistributionVerificationFailure.InvalidSignature);
    }

    [TestMethod]
    public void Policy_rejects_ambiguous_origins_duplicate_keys_and_invalid_limits()
    {
        using var fixture = new CatalogFixture();

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePolicy(
            origin: new Uri("https://packages.example.test/catalog")));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePolicy(
            origin: new Uri("https://user:pass@packages.example.test/catalog/")));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePolicy(
            keys: [fixture.TrustedKey, fixture.TrustedKey]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePolicy(
            maximumAssetBytes: 10,
            maximumTotalAssetBytes: 9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.CreatePolicy(maximumCatalogBytes: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.CreatePolicy(maximumAssets: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.CreatePolicy(maximumAssetBytes: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.CreatePolicy(maximumTotalAssetBytes: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.CreatePolicy(maximumLifetime: TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.CreatePolicy(allowedClockSkew: TimeSpan.FromTicks(-1)));
    }

    private static void AssertFailure(
        DistributionVerificationResult<VerifiedSignedCatalog> result,
        DistributionVerificationFailure expected)
    {
        Assert.IsFalse(result.IsVerified);
        Assert.IsNull(result.Value);
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual(expected, result.Diagnostic.Failure);
        Assert.IsNotNull(result.Diagnostic.Field);
    }

    private sealed class CatalogFixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public CatalogFixture()
        {
            Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
            Origin = new Uri("https://packages.example.test/catalog/");
            TrustedKey = new CatalogTrustedKey(
                "catalog-2026-a",
                Signer.ExportSubjectPublicKeyInfoPem(),
                Now.AddYears(-1),
                Now.AddYears(1));
            Policy = CreatePolicy();
        }

        public ECDsa Signer { get; }
        public DateTimeOffset Now { get; }
        public Uri Origin { get; }
        public CatalogTrustedKey TrustedKey { get; }
        public SignedCatalogPolicy Policy { get; }
        public SignedCatalogVerifier Verifier { get; } = new();

        public SignedCatalogDocument CreateDocument() => new()
        {
            CatalogVersion = SignedCatalogVerifier.CurrentCatalogVersion,
            CatalogId = "catalog.main",
            CatalogKind = "module-assets",
            Channel = "stable",
            Architecture = "win-x64",
            Origin = Origin.AbsoluteUri,
            Epoch = 1,
            Sequence = 7,
            IssuedAt = Now.AddMinutes(-5).ToString("O"),
            ExpiresAt = Now.AddDays(7).ToString("O"),
            Assets = [CreateAsset("module.core", "module.bin")],
            Signature = new SignedCatalogSignatureDocument
            {
                Algorithm = SignedCatalogVerifier.SignatureAlgorithm,
                KeyId = TrustedKey.KeyId,
            },
        };

        public SignedCatalogAssetDocument CreateAsset(string assetId, string fileName) => new()
        {
            AssetId = assetId,
            Version = "1.2.3-rc.1+build.7",
            Url = new Uri(Origin, fileName).AbsoluteUri,
            FileName = fileName,
            Size = 4,
            Sha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])),
            MediaType = "application/octet-stream",
            AuthenticodeRequired = true,
        };

        public SignedCatalogPolicy CreatePolicy(
            IEnumerable<CatalogTrustedKey>? keys = null,
            CatalogRollbackFloor floor = default,
            Uri? origin = null,
            int maximumCatalogBytes = 1024 * 1024,
            int maximumAssets = 256,
            long maximumAssetBytes = 4L * 1024 * 1024 * 1024,
            long maximumTotalAssetBytes = 16L * 1024 * 1024 * 1024,
            TimeSpan? maximumLifetime = null,
            TimeSpan? allowedClockSkew = null) =>
            new(
                "catalog.main",
                "module-assets",
                "stable",
                "win-x64",
                origin ?? Origin,
                keys ?? [TrustedKey],
                floor,
                maximumCatalogBytes,
                maximumAssets,
                maximumAssetBytes,
                maximumTotalAssetBytes,
                maximumLifetime,
                allowedClockSkew);

        public void Sign(SignedCatalogDocument document, ECDsa? signer = null)
        {
            signer ??= Signer;
            document.Signature!.Value = Convert.ToBase64String(signer.SignData(
                SignedCatalogCanonicalizer.Canonicalize(document),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public byte[] SerializeSigned(SignedCatalogDocument document)
        {
            Sign(document);
            return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        }

        public DistributionVerificationResult<VerifiedSignedCatalog> Verify(
            SignedCatalogDocument document,
            SignedCatalogPolicy? policy = null)
        {
            Sign(document);
            return VerifyWithoutSigning(document, policy);
        }

        public DistributionVerificationResult<VerifiedSignedCatalog> VerifyWithoutSigning(
            SignedCatalogDocument document,
            SignedCatalogPolicy? policy = null) =>
            Verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), policy ?? Policy, Now);

        public void Dispose() => Signer.Dispose();
    }
}
