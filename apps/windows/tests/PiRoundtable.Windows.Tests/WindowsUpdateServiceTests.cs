using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using PiRoundtable.Distribution;
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
        AssertNoPartialArtifacts(Path.GetDirectoryName(staged.PackagePath)!);
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
    [DataRow("asset")]
    [DataRow("signature")]
    public async Task Null_manifest_objects_fail_as_invalid_data_instead_of_crashing(string propertyName)
    {
        using var fixture = new UpdateFixture();
        var manifest = fixture.CreateManifest([1], "0.2.0");
        var document = JsonNode.Parse(fixture.SignAndSerialize(manifest))!.AsObject();
        document[propertyName] = null;

        await AssertManifestFailsAsync(fixture, document.ToJsonString(), typeof(InvalidDataException));
    }

    [TestMethod]
    [DataRow("00.2.0")]
    [DataRow("256.0.0")]
    [DataRow("0.256.0")]
    [DataRow("0.0.65536")]
    public async Task Signed_manifest_rejects_noncanonical_or_uninstallable_versions(string invalidVersion)
    {
        using var fixture = new UpdateFixture();
        var manifest = fixture.CreateManifest([1], invalidVersion);

        await AssertManifestFailsAsync(
            fixture,
            Encoding.UTF8.GetString(fixture.SignAndSerialize(manifest)),
            typeof(InvalidDataException));
    }

    [TestMethod]
    [DataRow("https://example.test/ShizkMoon/pi-roundtable/releases/download/v0.2.0/PiRoundtable-0.2.0-win-x64.msi")]
    [DataRow("https://github.com/OtherOwner/pi-roundtable/releases/download/v0.2.0/PiRoundtable-0.2.0-win-x64.msi")]
    [DataRow("https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.1.0/PiRoundtable-0.2.0-win-x64.msi")]
    [DataRow("https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.2.0/PiRoundtable-0.2.0-win-x64.msi?download=1")]
    public void Signed_manifest_rejects_assets_outside_the_exact_repository_release_path(string assetUrl)
    {
        using var fixture = new UpdateFixture();
        var manifest = fixture.CreateManifest([1], "0.2.0");
        manifest.Asset.Url = assetUrl;
        var verifier = new UpdateManifestVerifier(fixture.ProductionPolicy);

        Assert.ThrowsExactly<InvalidDataException>(() => verifier.ParseAndVerify(
            fixture.SignAndSerialize(manifest),
            DateTimeOffset.UtcNow));
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
        AssertNoPartialArtifacts(versionDirectory);
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
        var versionDirectory = Path.Combine(fixture.StagingRoot, "0.2.0");
        Assert.IsFalse(File.Exists(Path.Combine(versionDirectory, manifest.Asset.FileName)));
        Assert.IsFalse(File.Exists(Path.Combine(versionDirectory, "staged-update.json")));
        AssertNoPartialArtifacts(versionDirectory);
    }

    [TestMethod]
    public void Production_policy_accepts_manifest_that_does_not_require_authenticode()
    {
        using var fixture = new UpdateFixture();
        var manifest = fixture.CreateManifest(Encoding.UTF8.GetBytes("unsigned-msi"), "0.4.0");
        manifest.Asset.Url = "https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.4.0/PiRoundtable-0.4.0-win-x64.msi";
        var verifier = new UpdateManifestVerifier(fixture.ProductionPolicy);

        var verified = verifier.ParseAndVerify(fixture.SignAndSerialize(manifest), DateTimeOffset.UtcNow);

        Assert.AreEqual(new Version(0, 4, 0), verified.Version);
        Assert.IsFalse(verified.Document.Asset.AuthenticodeRequired);
    }

    [TestMethod]
    public async Task Required_authenticode_observes_the_same_locked_leaf_that_is_promoted()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("signed-msi-fixture");
        var manifest = fixture.CreateManifest(payload, "0.2.0", authenticodeRequired: true);
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var authenticode = new InspectingAuthenticodeVerifier(payload, trusted: true);
        using var service = fixture.CreateService(client, authenticode);

        var check = await service.CheckAsync();
        var staged = await service.DownloadAndStageAsync(check.Manifest);

        Assert.IsTrue(authenticode.WasCalled);
        Assert.IsTrue(authenticode.WriteReplacementWasBlocked);
        Assert.IsNotNull(authenticode.ObservedPath);
        Assert.EndsWith(".msi", authenticode.ObservedPath, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(authenticode.ObservedPath.Contains(".partial", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(authenticode.ObservedPath));
        Assert.AreEqual(
            Path.GetFullPath(staged.PackagePath),
            Path.GetFullPath(authenticode.GetCurrentPath()),
            ignoreCase: true);
        authenticode.Dispose();
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(staged.PackagePath));
    }

    [TestMethod]
    public async Task Existing_verified_package_is_relocked_retrusted_and_repairs_missing_state_without_download()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("cached-signed-msi");
        var manifest = fixture.CreateManifest(payload, "0.2.0", authenticodeRequired: true);
        using (var firstClient = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }))
        using (var firstAuthenticode = new InspectingAuthenticodeVerifier(payload, trusted: true))
        using (var firstService = fixture.CreateService(firstClient, firstAuthenticode))
        {
            var firstCheck = await firstService.CheckAsync();
            var firstStaged = await firstService.DownloadAndStageAsync(firstCheck.Manifest);
            File.Delete(Path.Combine(Path.GetDirectoryName(firstStaged.PackagePath)!, "staged-update.json"));
        }

        using var offlineClient = CreateClient(_ =>
            throw new AssertFailedException("A verified cached package must not be downloaded again."));
        using var secondAuthenticode = new InspectingAuthenticodeVerifier(payload, trusted: true);
        using var secondService = fixture.CreateService(offlineClient, secondAuthenticode);

        var reused = await secondService.DownloadAndStageAsync(
            new UpdateManifestVerifier(fixture.Policy).ParseAndVerify(
                fixture.SignAndSerialize(manifest),
                DateTimeOffset.UtcNow));

        Assert.IsTrue(secondAuthenticode.WasCalled);
        Assert.IsTrue(secondAuthenticode.WriteReplacementWasBlocked);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(reused.PackagePath));
        var versionDirectory = Path.GetDirectoryName(reused.PackagePath)!;
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(versionDirectory, "staged-update.json")))!.AsObject();
        Assert.AreEqual(manifest.Asset.Sha256.ToUpperInvariant(), state["sha256"]!.GetValue<string>());
        Assert.AreEqual(manifest.Asset.FileName, state["packageFileName"]!.GetValue<string>());
        AssertNoPartialArtifacts(versionDirectory);
    }

    [TestMethod]
    public async Task Tampered_cached_package_is_replaced_by_exactly_one_verified_download()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("expected-cached-payload");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        var verifiedManifest = new UpdateManifestVerifier(fixture.Policy).ParseAndVerify(
            fixture.SignAndSerialize(manifest),
            DateTimeOffset.UtcNow);
        var versionDirectory = Path.Combine(fixture.StagingRoot, "0.2.0");
        Directory.CreateDirectory(versionDirectory);
        var packagePath = Path.Combine(versionDirectory, manifest.Asset.FileName);
        await File.WriteAllBytesAsync(packagePath, Encoding.UTF8.GetBytes("tampered-cached-content"));
        var downloads = 0;
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/release.msi")
            {
                Interlocked.Increment(ref downloads);
                return BytesResponse(payload);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);

        var staged = await service.DownloadAndStageAsync(verifiedManifest);

        Assert.AreEqual(1, downloads);
        Assert.AreEqual(packagePath, staged.PackagePath);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(packagePath));
        AssertNoPartialArtifacts(versionDirectory);
    }

    [TestMethod]
    public async Task Cached_package_with_untrusted_required_authenticode_fails_without_network()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("cached-authenticode-payload");
        var manifest = fixture.CreateManifest(payload, "0.2.0", authenticodeRequired: true);
        var verifiedManifest = new UpdateManifestVerifier(fixture.Policy).ParseAndVerify(
            fixture.SignAndSerialize(manifest),
            DateTimeOffset.UtcNow);
        var versionDirectory = Path.Combine(fixture.StagingRoot, "0.2.0");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, manifest.Asset.FileName), payload);
        using var offlineClient = CreateClient(_ =>
            throw new AssertFailedException("An untrusted cached package must fail before network access."));
        using var service = fixture.CreateService(offlineClient, authenticodeTrusted: false);

        await Assert.ThrowsExactlyAsync<CryptographicException>(() =>
            service.DownloadAndStageAsync(verifiedManifest));

        Assert.IsFalse(File.Exists(Path.Combine(versionDirectory, "staged-update.json")));
        AssertNoPartialArtifacts(versionDirectory);
    }

    [TestMethod]
    public async Task Concurrent_same_version_staging_is_serialized_and_recovers_crash_orphans()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("single-download-payload");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        var verifiedManifest = new UpdateManifestVerifier(fixture.Policy).ParseAndVerify(
            fixture.SignAndSerialize(manifest),
            DateTimeOffset.UtcNow);
        var versionDirectory = Path.Combine(fixture.StagingRoot, "0.2.0");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(versionDirectory, $"PiRoundtable-0.2.0-win-x64.{Guid.NewGuid():N}.partial.msi"),
            [1]);
        await File.WriteAllBytesAsync(
            Path.Combine(versionDirectory, $"staged-update.{Guid.NewGuid():N}.partial.json"),
            [2]);
        await File.WriteAllBytesAsync(
            Path.Combine(versionDirectory, $"{manifest.Asset.FileName}.partial"),
            [3]);
        await File.WriteAllBytesAsync(
            Path.Combine(versionDirectory, "staged-update.json.tmp"),
            [4]);
        var assetDownloads = 0;
        HttpResponseMessage Handle(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath == "/release.msi")
            {
                Interlocked.Increment(ref assetDownloads);
                return BytesResponse(payload);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        using var firstClient = CreateClient(Handle);
        using var secondClient = CreateClient(Handle);
        using var firstService = fixture.CreateService(firstClient, authenticodeTrusted: false);
        using var secondService = fixture.CreateService(secondClient, authenticodeTrusted: false);

        var results = await Task.WhenAll(
            firstService.DownloadAndStageAsync(verifiedManifest),
            secondService.DownloadAndStageAsync(verifiedManifest));

        Assert.AreEqual(1, assetDownloads);
        Assert.AreEqual(results[0].PackagePath, results[1].PackagePath);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(results[0].PackagePath));
        AssertNoPartialArtifacts(versionDirectory);
    }

    [TestMethod]
    public async Task State_commit_failure_preserves_verified_package_for_offline_repair()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("recoverable-package");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        var versionDirectory = Path.Combine(fixture.StagingRoot, "0.2.0");
        Directory.CreateDirectory(Path.Combine(versionDirectory, "staged-update.json"));
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using (var service = fixture.CreateService(client, authenticodeTrusted: false))
        {
            var check = await service.CheckAsync();

            await Assert.ThrowsExactlyAsync<Win32Exception>(() =>
                service.DownloadAndStageAsync(check.Manifest));
        }

        var packagePath = Path.Combine(versionDirectory, manifest.Asset.FileName);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(packagePath));
        AssertNoPartialArtifacts(versionDirectory);
        Directory.Delete(Path.Combine(versionDirectory, "staged-update.json"));

        using var offlineClient = CreateClient(_ =>
            throw new AssertFailedException("Recovery must reuse the already verified package."));
        using var recoveryService = fixture.CreateService(offlineClient, authenticodeTrusted: false);
        var recovered = await recoveryService.DownloadAndStageAsync(
            new UpdateManifestVerifier(fixture.Policy).ParseAndVerify(
                fixture.SignAndSerialize(manifest),
                DateTimeOffset.UtcNow));

        Assert.AreEqual(packagePath, recovered.PackagePath);
        Assert.IsTrue(File.Exists(Path.Combine(versionDirectory, "staged-update.json")));
        AssertNoPartialArtifacts(versionDirectory);
    }

    [TestMethod]
    public async Task Staging_rejects_reparse_root_without_writing_to_its_target()
    {
        using var fixture = new UpdateFixture();
        var outside = Path.Combine(Path.GetTempPath(), $"pi-roundtable-update-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(fixture.StagingRoot, outside);
        var payload = Encoding.UTF8.GetBytes("must-not-escape");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        using var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/manifest" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "/release.msi" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);
        try
        {
            var check = await service.CheckAsync();

            var exception = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
                service.DownloadAndStageAsync(check.Manifest));

            Assert.AreEqual(ArtifactIntegrityFailure.ReparsePoint, exception.Failure);
            Assert.HasCount(0, Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(fixture.StagingRoot);
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task Windows_authenticode_verifier_accepts_a_borrowed_staging_handle_contract()
    {
        var trustDataType = typeof(WindowsAuthenticodeVerifier).GetNestedType(
            "WinTrustData",
            System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(trustDataType);
        Assert.AreEqual(IntPtr.Size == 8 ? 88 : 52, Marshal.SizeOf(trustDataType));

        var directory = Path.Combine(Path.GetTempPath(), $"pi-roundtable-auth-handle-{Guid.NewGuid():N}");
        byte[] payload = [1, 2, 3, 4];
        var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
        try
        {
            await using var staging = ArtifactStager.CreateNew(directory, "unsigned.msi");
            await using var source = new MemoryStream(payload, writable: false);
            await staging.CopyAndVerifyAsync(source, spec);

            Assert.IsFalse(new WindowsAuthenticodeVerifier().IsTrusted(staging.CurrentPath, staging.FileHandle));
            Assert.IsFalse(staging.FileHandle.IsClosed);
            var observed = new byte[payload.Length];
            Assert.AreEqual(payload.Length, RandomAccess.Read(staging.FileHandle, observed, fileOffset: 0));
            CollectionAssert.AreEqual(payload, observed);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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
    public async Task Production_asset_redirect_to_the_GitHub_release_CDN_is_accepted()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("release-cdn-payload");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        manifest.Asset.Url = "https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.2.0/PiRoundtable-0.2.0-win-x64.msi";
        var cdnUri = new Uri(
            "https://release-assets.githubusercontent.com/github-production-release-asset/123/01234567-89ab-cdef-0123-456789abcdef?token=bounded");
        using var client = CreateClient(request => request.RequestUri!.Host switch
        {
            "raw.githubusercontent.com" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "github.com" => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = cdnUri },
            },
            "release-assets.githubusercontent.com" => BytesResponse(payload),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = fixture.CreateService(
            client,
            authenticodeTrusted: false,
            manifestUri: UpdateFixture.ProductionManifestUri,
            policy: fixture.ProductionPolicy);

        var check = await service.CheckAsync();
        var staged = await service.DownloadAndStageAsync(check.Manifest);

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(staged.PackagePath));
    }

    [TestMethod]
    public async Task Production_asset_redirect_to_an_arbitrary_HTTPS_host_is_rejected()
    {
        using var fixture = new UpdateFixture();
        var payload = Encoding.UTF8.GetBytes("release-redirect-payload");
        var manifest = fixture.CreateManifest(payload, "0.2.0");
        manifest.Asset.Url = "https://github.com/ShizkMoon/pi-roundtable/releases/download/v0.2.0/PiRoundtable-0.2.0-win-x64.msi";
        using var client = CreateClient(request => request.RequestUri!.Host switch
        {
            "raw.githubusercontent.com" => JsonResponse(fixture.SignAndSerialize(manifest)),
            "github.com" => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://example.test/release.msi") },
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = fixture.CreateService(
            client,
            authenticodeTrusted: false,
            manifestUri: UpdateFixture.ProductionManifestUri,
            policy: fixture.ProductionPolicy);
        var check = await service.CheckAsync();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAndStageAsync(check.Manifest));
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
    public async Task Equal_or_older_signed_release_cannot_be_staged_directly()
    {
        using var fixture = new UpdateFixture();
        var manifest = fixture.CreateManifest([1], "0.1.0");
        var requests = 0;
        using var client = CreateClient(_ =>
        {
            requests += 1;
            return JsonResponse(fixture.SignAndSerialize(manifest));
        });
        using var service = fixture.CreateService(client, authenticodeTrusted: false);
        var check = await service.CheckAsync();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAndStageAsync(check.Manifest));

        Assert.AreEqual(1, requests, "Rollback rejection must occur before any package request.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.StagingRoot, "0.1.0")));
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
    public async Task Updater_accepts_a_verified_parent_that_already_exited_before_wait_registration()
    {
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c timeout.exe /t 1 /nobreak > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Unable to start updater parent fixture.");
        var processId = parent.Id;
        var startTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks;
        await parent.WaitForExitAsync();

        await ParentProcessFence.WaitForExitAsync(
            processId,
            startTimeUtcTicks,
            TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task Updater_rejects_a_reused_parent_process_identity()
    {
        using var current = Process.GetCurrentProcess();
        var wrongStartTime = current.StartTime.ToUniversalTime().Ticks + 1;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ParentProcessFence.WaitForExitAsync(
                current.Id,
                wrongStartTime,
                TimeSpan.FromSeconds(1)));
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
            },
            ReleaseAssetBaseUri: new Uri("https://github.com/ShizkMoon/pi-roundtable/releases/download/")));

        var verified = verifier.ParseAndVerify(File.ReadAllBytes(manifestPath), DateTimeOffset.Parse("2026-08-05T00:00:00Z"));

        Assert.AreEqual(new Version(0, 4, 0), verified.Version);
        Assert.AreEqual("stable-2026-08", verified.Document.Signature.KeyId);
        Assert.AreEqual(148848943, verified.Document.Asset.Size);
        Assert.AreEqual("D9399C65596BAF368AB790122992A2C0C104771764CCA7CBB02B49C4A711CC4F", verified.Document.Asset.Sha256);
        Assert.IsFalse(verified.Document.Asset.AuthenticodeRequired);
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

    private static void AssertNoPartialArtifacts(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        var temporaryEntries = Directory.GetFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetFileName(path).Contains(".partial.", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.HasCount(0, temporaryEntries);
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
        public bool IsTrusted(string filePath, SafeFileHandle fileHandle) => trusted;
    }

    private sealed class InspectingAuthenticodeVerifier(byte[] expectedContent, bool trusted)
        : IAuthenticodeVerifier, IDisposable
    {
        private SafeFileHandle? _observedHandle;

        public bool WasCalled { get; private set; }
        public bool WriteReplacementWasBlocked { get; private set; }
        public string? ObservedPath { get; private set; }

        public bool IsTrusted(string filePath, SafeFileHandle fileHandle)
        {
            WasCalled = true;
            ObservedPath = filePath;
            var observed = new byte[expectedContent.Length];
            Assert.AreEqual(
                expectedContent.Length,
                RandomAccess.Read(fileHandle, observed, fileOffset: 0));
            CollectionAssert.AreEqual(expectedContent, observed);
            Assert.IsTrue(DuplicateHandle(
                GetCurrentProcess(),
                fileHandle,
                GetCurrentProcess(),
                out var duplicate,
                desiredAccess: 0,
                inheritHandle: false,
                options: 2));
            _observedHandle?.Dispose();
            _observedHandle = duplicate;
            try
            {
                new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose();
            }
            catch (IOException)
            {
                WriteReplacementWasBlocked = true;
            }
            return trusted;
        }

        public string GetCurrentPath()
        {
            var handle = _observedHandle
                ?? throw new InvalidOperationException("Authenticode was not invoked.");
            var path = new StringBuilder(32_768);
            var length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, flags: 0);
            if (length == 0 || length >= path.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return path.ToString() switch
            {
                var value when value.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal) => $"\\\\{value[8..]}",
                var value when value.StartsWith("\\\\?\\", StringComparison.Ordinal) => value[4..],
                var value => value,
            };
        }

        public void Dispose()
        {
            _observedHandle?.Dispose();
            _observedHandle = null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(
            nint sourceProcess,
            SafeFileHandle sourceHandle,
            nint targetProcess,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);
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

        public static Uri ProductionManifestUri { get; } = new(
            "https://raw.githubusercontent.com/ShizkMoon/pi-roundtable/main/packaging/windows-x64/update-manifest.json");

        public UpdateManifestPolicy Policy => new(
            "PiRoundtable.Windows",
            "stable",
            "x64",
            new Dictionary<string, string> { ["test-key"] = _key.ExportSubjectPublicKeyInfoPem() },
            AllowLoopbackHttp: _allowLoopbackHttp);

        public UpdateManifestPolicy ProductionPolicy => new(
            "PiRoundtable.Windows",
            "stable",
            "x64",
            new Dictionary<string, string> { ["test-key"] = _key.ExportSubjectPublicKeyInfoPem() },
            ReleaseAssetBaseUri: new Uri("https://github.com/ShizkMoon/pi-roundtable/releases/download/"));

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

        public WindowsUpdateService CreateService(
            HttpClient client,
            bool authenticodeTrusted,
            Uri? manifestUri = null,
            UpdateManifestPolicy? policy = null)
        {
            return CreateService(
                client,
                new ConstantAuthenticodeVerifier(authenticodeTrusted),
                manifestUri,
                policy);
        }

        public WindowsUpdateService CreateService(
            HttpClient client,
            IAuthenticodeVerifier authenticodeVerifier,
            Uri? manifestUri = null,
            UpdateManifestPolicy? policy = null)
        {
            var options = new WindowsUpdateServiceOptions(
                manifestUri ?? new Uri("http://127.0.0.1/manifest"),
                policy ?? Policy,
                StagingRoot,
                new Version(0, 1, 0));
            return new WindowsUpdateService(options, client, authenticodeVerifier);
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
