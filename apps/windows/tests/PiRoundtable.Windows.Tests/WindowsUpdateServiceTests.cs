using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiRoundtable.Updater;
using PiRoundtable.Windows.Services.Updater;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class WindowsUpdateServiceTests
{
    [TestMethod]
    public async Task Signed_manifest_and_exact_payload_are_staged_atomically()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("controlled-msi-payload");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);

        var check = await service.CheckAsync();
        var staged = await service.DownloadAndStageAsync(check.Manifest);

        Assert.AreEqual(UpdateAvailability.Available, check.Availability);
        Assert.AreEqual(new Version(0, 2, 0), check.AvailableVersion);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(staged.PackagePath));
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(staged.PackagePath)!, "staged-update.json")));
        Assert.IsFalse(File.Exists(staged.PackagePath + ".partial"));
    }

    [TestMethod]
    public async Task Manifest_tampering_duplicate_fields_and_unknown_fields_fail_closed()
    {
        using var fixture = new UpdateFixture();
        byte[] payload = [1, 2, 3, 4];
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        var valid = Encoding.UTF8.GetString(fixture.SignAndSerialize(manifest));
        var tampered = valid.Replace("\"version\":\"0.2.0\"", "\"version\":\"9.9.9\"", StringComparison.Ordinal);
        var duplicated = valid.Replace("{\"manifestVersion\":1", "{\"manifestVersion\":1,\"manifestVersion\":1", StringComparison.Ordinal);
        var unknown = valid.Replace("{\"manifestVersion\":1", "{\"unexpected\":true,\"manifestVersion\":1", StringComparison.Ordinal);

        await AssertManifestFailsAsync(fixture, tampered, typeof(CryptographicException));
        await AssertManifestFailsAsync(fixture, duplicated, typeof(InvalidDataException));
        await AssertManifestFailsAsync(fixture, unknown, typeof(JsonException));
    }

    [TestMethod]
    public async Task Wrong_payload_hash_is_deleted_and_never_promoted()
    {
        using var fixture = new UpdateFixture();
        var expected = Encoding.UTF8.GetBytes("expected-payload");
        var wrong = Encoding.UTF8.GetBytes("tampered-payload");
        var manifest = fixture.CreateManifest(expected, "0.2.0");
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(wrong),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);
        var check = await service.CheckAsync();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAndStageAsync(check.Manifest));
        var versionDirectory = Path.Combine(fixture.StagingRoot, "0.2.0");
        Assert.IsFalse(File.Exists(Path.Combine(versionDirectory, "PiRoundtable-0.2.0-win-x64.msi")));
        Assert.IsFalse(File.Exists(Path.Combine(versionDirectory, "PiRoundtable-0.2.0-win-x64.msi.partial")));
    }

    [TestMethod]
    public async Task Required_authenticode_fails_closed_when_windows_does_not_trust_package()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("unsigned-msi");
        var manifest = fixture.CreateManifest(payload, "0.2.0", authenticodeRequired: true);
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);
        var check = await service.CheckAsync();

        await Assert.ThrowsExactlyAsync<CryptographicException>(() => service.DownloadAndStageAsync(check.Manifest));
    }

    [TestMethod]
    public async Task Redirect_to_plain_http_is_rejected()
    {
        using var fixture = new UpdateFixture(allowLoopbackHttp: false);
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://example.test/manifest") },
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [TestMethod]
    public async Task Equal_or_older_signed_release_is_not_offered_as_an_update()
    {
        using var fixture = new UpdateFixture();
        var manifest = fixture.CreateManifest([1], "0.1.0");
        using var client = CreateClient(_ => JsonResponse(fixture.SignAndSerialize(manifest)));
        using var service = fixture.CreateService(client, authenticodeTrusted: false);

        var check = await service.CheckAsync();

        Assert.AreEqual(UpdateAvailability.UpToDate, check.Availability);
    }

    [TestMethod]
    public void Installer_exit_codes_restart_only_after_completed_install()
    {
        Assert.IsTrue(InstallerExitCodes.IsSuccessful(0));
        Assert.IsTrue(InstallerExitCodes.IsSuccessful(3010));
        Assert.IsFalse(InstallerExitCodes.IsSuccessful(1641));
        Assert.IsTrue(InstallerExitCodes.RestartWasInitiated(1641));
        Assert.IsFalse(InstallerExitCodes.IsSuccessful(1603));
    }

    [TestMethod]
    public async Task Updater_reverifies_and_locks_the_staged_package_before_elevation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-roundtable-updater-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "update.msi");
        var payload = Encoding.UTF8.GetBytes("verified-msi-payload");
        await File.WriteAllBytesAsync(path, payload);
        try
        {
            await using var packageLock = await VerifiedPackageLock.OpenAsync(
                path,
                payload.Length,
                SHA256.HashData(payload));

            Assert.ThrowsExactly<IOException>(() =>
                new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Production_trust_anchor_accepts_committed_release_manifest()
    {
        var manifestPath = FindRepositoryFile("packaging", "windows-x64", "update-manifest.json");
        var verifier = new UpdateManifestVerifier(new UpdateManifestPolicy(
            "PiRoundtable.Windows",
            "stable",
            "x64",
            new Dictionary<string, string>
            {
                [UpdateTrustAnchor.KeyId] = UpdateTrustAnchor.PublicKeyPem,
            }));

        var verified = verifier.ParseAndVerify(File.ReadAllBytes(manifestPath), DateTimeOffset.Parse("2026-08-03T00:00:00Z"));

        Assert.AreEqual(new Version(0, 2, 0), verified.Version);
        Assert.AreEqual("stable-2026-08", verified.Document.Signature.KeyId);
        Assert.AreEqual(158380344, verified.Document.Asset.Size);
        Assert.AreEqual("7C2E477E43511DDD67B6C7ADBF501F090AE7056A862E9B412071D2A838CE2597", verified.Document.Asset.Sha256);
    }

    private static async Task AssertManifestFailsAsync(UpdateFixture fixture, string json, Type expectedType)
    {
        using var client = CreateClient(_ => JsonResponse(Encoding.UTF8.GetBytes(json)));
        using var service = fixture.CreateService(client, authenticodeTrusted: false);
        Exception? exception = null;
        try
        {
            await service.CheckAsync();
        }
        catch (Exception caught)
        {
            exception = caught;
        }
        Assert.IsNotNull(exception, "Expected manifest verification to fail.");
        Assert.IsInstanceOfType(exception, expectedType);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new DelegateHandler(handler)) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Unable to locate repository fixture.", Path.Combine(segments));
    }

    private static HttpResponseMessage JsonResponse(byte[] content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new("application/json") },
            },
        };
    }

    private static HttpResponseMessage BytesResponse(byte[] content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class ConstantAuthenticodeVerifier(bool trusted) : IAuthenticodeVerifier
    {
        public bool IsTrusted(string filePath) => trusted;
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create();
        private readonly bool _allowLoopbackHttp;

        public UpdateFixture(bool allowLoopbackHttp = true)
        {
            _key.GenerateKey(ECCurve.NamedCurves.nistP256);
            _allowLoopbackHttp = allowLoopbackHttp;
            StagingRoot = Path.Combine(Path.GetTempPath(), $"pi-roundtable-update-{Guid.NewGuid():N}");
        }

        public string StagingRoot { get; }

        public UpdateManifestDocument CreateManifest(
            byte[] payload,
            string version,
            bool authenticodeRequired = false)
        {
            return new UpdateManifestDocument
            {
                ManifestVersion = 1,
                ProductId = "PiRoundtable.Windows",
                Channel = "stable",
                Architecture = "x64",
                Version = version,
                PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                Asset = new UpdateAssetDocument
                {
                    Url = "http://127.0.0.1/release.msi",
                    FileName = $"PiRoundtable-{version}-win-x64.msi",
                    Size = payload.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(payload)),
                    AuthenticodeRequired = authenticodeRequired,
                },
                Signature = new UpdateSignatureDocument
                {
                    Algorithm = UpdateManifestVerifier.SignatureAlgorithm,
                    KeyId = "test-key",
                },
            };
        }

        public byte[] SignAndSerialize(UpdateManifestDocument manifest)
        {
            manifest.Signature.Value = Convert.ToBase64String(_key.SignData(
                UpdateManifestCanonicalizer.Canonicalize(manifest),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        public WindowsUpdateService CreateService(HttpClient client, bool authenticodeTrusted)
        {
            var options = new WindowsUpdateServiceOptions(
                new Uri("http://127.0.0.1/manifest"),
                new UpdateManifestPolicy(
                    "PiRoundtable.Windows",
                    "stable",
                    "x64",
                    new Dictionary<string, string> { ["test-key"] = _key.ExportSubjectPublicKeyInfoPem() },
                    AllowLoopbackHttp: _allowLoopbackHttp),
                StagingRoot,
                new Version(0, 1, 0));
            return new WindowsUpdateService(options, client, new ConstantAuthenticodeVerifier(authenticodeTrusted));
        }

        public void Dispose()
        {
            _key.Dispose();
            if (Directory.Exists(StagingRoot))
            {
                Directory.Delete(StagingRoot, recursive: true);
            }
        }
    }
}
