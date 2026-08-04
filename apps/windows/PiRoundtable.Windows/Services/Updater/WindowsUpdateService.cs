using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiRoundtable.Distribution;

namespace PiRoundtable.Windows.Services.Updater;

internal sealed record WindowsUpdateServiceOptions(
    Uri ManifestUri,
    UpdateManifestPolicy ManifestPolicy,
    string StagingRoot,
    Version CurrentVersion,
    int MaximumRedirects = 5)
{
    public static WindowsUpdateServiceOptions CreateProduction()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("更新器仅支持 Windows x64 与 arm64。"),
        };
        var assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1, 0);
        var currentVersion = new Version(
            Math.Max(0, assemblyVersion.Major),
            Math.Max(0, assemblyVersion.Minor),
            Math.Max(0, assemblyVersion.Build));
        return new WindowsUpdateServiceOptions(
            new Uri("https://raw.githubusercontent.com/ShizkMoon/pi-roundtable/main/packaging/windows-x64/update-manifest.json"),
            new UpdateManifestPolicy(
                "PiRoundtable.Windows",
                "stable",
                architecture,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UpdateTrustAnchor.KeyId] = UpdateTrustAnchor.PublicKeyPem,
                },
                ReleaseAssetBaseUri: new Uri("https://github.com/ShizkMoon/pi-roundtable/releases/download/"),
                MinimumAuthenticodeVersion: new Version(0, 4, 0)),
            Path.Combine(LocalDataRoot.Resolve(), "updates"),
            currentVersion);
    }
}

internal sealed class WindowsUpdateService : IDisposable
{
    private static readonly JsonSerializerOptions StateSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly WindowsUpdateServiceOptions _options;
    private readonly UpdateManifestVerifier _manifestVerifier;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public WindowsUpdateService(
        WindowsUpdateServiceOptions? options = null,
        HttpClient? httpClient = null,
        IAuthenticodeVerifier? authenticodeVerifier = null)
    {
        _options = options ?? WindowsUpdateServiceOptions.CreateProduction();
        _manifestVerifier = new UpdateManifestVerifier(_options.ManifestPolicy);
        _authenticodeVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier();
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            })
            {
                Timeout = TimeSpan.FromSeconds(45),
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public Version CurrentVersion => _options.CurrentVersion;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ValidateInitialUri(_options.ManifestUri, UpdateRequestKind.Manifest);
        using var response = await SendWithRedirectsAsync(
            _options.ManifestUri,
            UpdateRequestKind.Manifest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > UpdateManifestVerifier.MaximumManifestBytes)
        {
            throw new InvalidDataException("更新清单超过大小限制。");
        }
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] json;
        try
        {
            json = await BoundedContent.ReadAllBytesAsync(
                responseStream,
                UpdateManifestVerifier.MaximumManifestBytes,
                cancellationToken);
        }
        catch (ArtifactIntegrityException exception) when (exception.Failure == ArtifactIntegrityFailure.ContentTooLarge)
        {
            throw new InvalidDataException("更新清单超过大小限制。", exception);
        }
        var manifest = _manifestVerifier.ParseAndVerify(json, DateTimeOffset.UtcNow);
        var availability = manifest.Version > _options.CurrentVersion
            ? UpdateAvailability.Available
            : UpdateAvailability.UpToDate;
        return new UpdateCheckResult(availability, _options.CurrentVersion, manifest.Version, manifest);
    }

    public async Task<StagedUpdatePackage> DownloadAndStageAsync(
        VerifiedUpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (manifest.Version <= _options.CurrentVersion)
        {
            throw new InvalidDataException("更新包版本必须高于当前已安装版本。");
        }
        var versionDirectory = Path.Combine(_options.StagingRoot, manifest.Version.ToString(3));
        EnsureSafeStagingPath(versionDirectory);

        var finalPath = Path.Combine(versionDirectory, manifest.Document.Asset.FileName);
        await using var directoryLease = await ArtifactStager.AcquireDirectoryAsync(
            versionDirectory,
            cancellationToken: cancellationToken);
        directoryLease.DeleteStaleArtifactsFor(manifest.Document.Asset.FileName);
        directoryLease.DeleteStaleArtifactsFor("staged-update.json");
        var verificationSpec = new ArtifactVerificationSpec(
            manifest.Document.Asset.Size,
            manifest.ExpectedSha256);
        if (await TryUseExistingPackageAsync(
                finalPath,
                versionDirectory,
                manifest,
                verificationSpec,
                cancellationToken))
        {
            return new StagedUpdatePackage(manifest, finalPath);
        }

        ValidateInitialUri(manifest.AssetUri, UpdateRequestKind.Asset);
        using var response = await SendWithRedirectsAsync(
            manifest.AssetUri,
            UpdateRequestKind.Asset,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != manifest.Document.Asset.Size)
        {
            throw new InvalidDataException("更新包 Content-Length 与签名清单不一致。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var staging = ArtifactStager.CreateNew(
            versionDirectory,
            manifest.Document.Asset.FileName);
        try
        {
            try
            {
                await staging.CopyAndVerifyAsync(
                    source,
                    verificationSpec,
                    progress is null ? null : new DownloadProgress(progress, manifest.Document.Asset.Size),
                    cancellationToken);
            }
            catch (ArtifactIntegrityException exception)
            {
                throw new InvalidDataException("更新包大小或 SHA-256 与签名清单不一致。", exception);
            }
            if (manifest.Document.Asset.AuthenticodeRequired &&
                !_authenticodeVerifier.IsTrusted(staging.CurrentPath, staging.FileHandle))
            {
                throw new CryptographicException("更新清单要求 Authenticode，但安装包签名不受 Windows 信任。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            staging.Promote();
            await WriteStateAsync(versionDirectory, manifest, finalPath, CancellationToken.None);
            progress?.Report(1);
            return new StagedUpdatePackage(manifest, finalPath);
        }
        catch
        {
            // Promotion publishes a fully verified package. If the replaceable
            // state sidecar then fails, keep the package so a later invocation
            // can re-lock, re-verify, and repair state without downloading it
            // again. Before promotion, disposal retries handle-based cleanup.
            if (!staging.IsPromoted)
            {
                _ = staging.TryDiscard();
            }
            throw;
        }
    }

    public Process LaunchInstallerHelper(StagedUpdatePackage package)
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "PiRoundtable.Updater.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("安装包中缺少独立更新辅助程序。", helperPath);
        }
        if (!File.Exists(package.PackagePath))
        {
            throw new FileNotFoundException("已验证的更新包不存在。", package.PackagePath);
        }

        using var current = Process.GetCurrentProcess();
        var restartExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前客户端路径。");
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            WorkingDirectory = _options.StagingRoot,
        };
        startInfo.ArgumentList.Add("--msi");
        startInfo.ArgumentList.Add(package.PackagePath);
        startInfo.ArgumentList.Add("--expected-size");
        startInfo.ArgumentList.Add(package.Manifest.Document.Asset.Size.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--expected-sha256");
        startInfo.ArgumentList.Add(package.Manifest.Document.Asset.Sha256.ToUpperInvariant());
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-start-time-utc-ticks");
        startInfo.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--restart-exe");
        startInfo.ArgumentList.Add(restartExecutable);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动独立更新辅助程序。");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri initialUri,
        UpdateRequestKind requestKind,
        CancellationToken cancellationToken)
    {
        var uri = initialUri;
        for (var redirect = 0; redirect <= _options.MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PiRoundtable-Windows-Updater/1");
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                var finalUri = response.RequestMessage?.RequestUri ?? uri;
                if (!IsAllowedRequestUri(finalUri, initialUri, requestKind))
                {
                    response.Dispose();
                    throw new InvalidDataException("更新请求被重定向到不安全的地址。");
                }
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new HttpRequestException("更新服务器返回了没有 Location 的重定向。");
            }
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!IsAllowedRequestUri(uri, initialUri, requestKind))
            {
                throw new InvalidDataException("更新请求被重定向到不安全的地址。");
            }
        }
        throw new HttpRequestException("更新请求重定向次数过多。");
    }

    private void ValidateInitialUri(Uri uri, UpdateRequestKind requestKind)
    {
        var expected = requestKind == UpdateRequestKind.Manifest ? _options.ManifestUri : uri;
        if (!IsAllowedRequestUri(uri, expected, requestKind) || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidDataException("更新地址必须使用无凭据、查询或片段的 HTTPS 地址。");
        }
    }

    private bool IsAllowedRequestUri(Uri uri, Uri initialUri, UpdateRequestKind requestKind)
    {
        if (_options.ManifestPolicy.AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return UpdateManifestVerifier.IsAllowedNetworkUri(
                uri,
                allowLoopbackHttp: true,
                allowQuery: false);
        }
        if (requestKind == UpdateRequestKind.Manifest)
        {
            return UpdateManifestVerifier.IsAllowedNetworkUri(
                    uri,
                    allowLoopbackHttp: false,
                    allowQuery: false) &&
                string.Equals(uri.AbsoluteUri, initialUri.AbsoluteUri, StringComparison.Ordinal);
        }
        if (string.Equals(uri.AbsoluteUri, initialUri.AbsoluteUri, StringComparison.Ordinal))
        {
            return UpdateManifestVerifier.IsAllowedNetworkUri(
                uri,
                allowLoopbackHttp: false,
                allowQuery: false);
        }
        return IsAllowedGitHubReleaseCdnUri(uri);
    }

    private static bool IsAllowedGitHubReleaseCdnUri(Uri uri)
    {
        if (!UpdateManifestVerifier.IsAllowedNetworkUri(
                uri,
                allowLoopbackHttp: false,
                allowQuery: true) ||
            !string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.IsDefaultPort is false ||
            string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
            string.Equals(segments[0], "github-production-release-asset", StringComparison.Ordinal) &&
            ulong.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            Guid.TryParse(segments[2], out _);
    }

    private static async Task WriteStateAsync(
        string directory,
        VerifiedUpdateManifest manifest,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var state = new
        {
            manifest.Document.ProductId,
            manifest.Document.Channel,
            Version = manifest.Version.ToString(3),
            PackageFileName = Path.GetFileName(packagePath),
            Sha256 = manifest.Document.Asset.Sha256.ToUpperInvariant(),
            StagedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var content = JsonSerializer.SerializeToUtf8Bytes(state, StateSerializerOptions);
        var spec = new ArtifactVerificationSpec(content.Length, SHA256.HashData(content));
        await using var source = new MemoryStream(content, writable: false);
        await using var staging = ArtifactStager.CreateNew(directory, "staged-update.json");
        await staging.CopyAndVerifyAsync(source, spec, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        staging.Promote();
    }

    private async Task<bool> TryUseExistingPackageAsync(
        string packagePath,
        string versionDirectory,
        VerifiedUpdateManifest manifest,
        ArtifactVerificationSpec verificationSpec,
        CancellationToken cancellationToken)
    {
        VerifiedArtifactLease verifiedPackage;
        try
        {
            verifiedPackage = await ArtifactVerifier.OpenVerifiedReadAsync(
                packagePath,
                verificationSpec,
                FileShare.Read,
                cancellationToken);
        }
        catch (ArtifactIntegrityException exception) when (exception.Failure != ArtifactIntegrityFailure.ReparsePoint)
        {
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return false;
        }

        await using (verifiedPackage)
        {
            if (manifest.Document.Asset.AuthenticodeRequired &&
                !_authenticodeVerifier.IsTrusted(packagePath, verifiedPackage.Stream.SafeFileHandle))
            {
                throw new CryptographicException("更新清单要求 Authenticode，但缓存安装包签名不受 Windows 信任。");
            }
            await WriteStateAsync(
                versionDirectory,
                manifest,
                packagePath,
                cancellationToken);
            return true;
        }
    }

    private void EnsureSafeStagingPath(string path)
    {
        var root = Path.GetFullPath(_options.StagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新 staging 路径越界。");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;
    }

    private enum UpdateRequestKind
    {
        Manifest,
        Asset,
    }

    private sealed class DownloadProgress(IProgress<double> progress, long expectedSize) : IProgress<long>
    {
        public void Report(long value)
        {
            progress.Report(expectedSize == 0 ? 1 : (double)value / expectedSize);
        }
    }
}
