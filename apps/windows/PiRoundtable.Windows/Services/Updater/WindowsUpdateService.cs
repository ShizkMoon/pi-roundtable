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
                }),
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
        ValidateInitialUri(_options.ManifestUri);
        using var response = await SendWithRedirectsAsync(_options.ManifestUri, cancellationToken);
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
        var versionDirectory = Path.Combine(_options.StagingRoot, manifest.Version.ToString(3));
        EnsureSafeStagingPath(versionDirectory);
        Directory.CreateDirectory(versionDirectory);
        EnsureNoReparsePoint(versionDirectory);

        var finalPath = Path.Combine(versionDirectory, manifest.Document.Asset.FileName);
        var partialPath = finalPath + ".partial";
        var verificationSpec = new ArtifactVerificationSpec(
            manifest.Document.Asset.Size,
            manifest.ExpectedSha256);
        if (await ArtifactVerifier.MatchesFileAsync(finalPath, verificationSpec, cancellationToken))
        {
            return new StagedUpdatePackage(manifest, finalPath);
        }

        TryDelete(partialPath);
        TryDelete(finalPath);
        ValidateInitialUri(manifest.AssetUri);
        var finalPromoted = false;
        try
        {
            using var response = await SendWithRedirectsAsync(manifest.AssetUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != manifest.Document.Asset.Size)
            {
                throw new InvalidDataException("更新包 Content-Length 与签名清单不一致。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                try
                {
                    await ArtifactVerifier.CopyAndVerifyAsync(
                        source,
                        destination,
                        verificationSpec,
                        progress is null ? null : new DownloadProgress(progress, manifest.Document.Asset.Size),
                        cancellationToken);
                }
                catch (ArtifactIntegrityException exception)
                {
                    throw new InvalidDataException("更新包大小或 SHA-256 与签名清单不一致。", exception);
                }
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            File.Move(partialPath, finalPath, overwrite: true);
            finalPromoted = true;
            await using (var verifiedPackage = await ArtifactVerifier.OpenVerifiedReadAsync(
                finalPath,
                verificationSpec,
                FileShare.Read,
                cancellationToken))
            {
                // Keep both the verified leaf and its parent path locked while
                // WinVerifyTrust resolves the path, so signature verification
                // cannot observe a replacement that differs from the bytes
                // reverified above.
                if (manifest.Document.Asset.AuthenticodeRequired && !_authenticodeVerifier.IsTrusted(finalPath))
                {
                    throw new CryptographicException("更新清单要求 Authenticode，但安装包签名不受 Windows 信任。");
                }
                await WriteStateAsync(versionDirectory, manifest, finalPath, cancellationToken);
            }
            progress?.Report(1);
            return new StagedUpdatePackage(manifest, finalPath);
        }
        catch
        {
            TryDelete(partialPath);
            if (finalPromoted)
            {
                TryDelete(finalPath);
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

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
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
                if (!UpdateManifestVerifier.IsAllowedNetworkUri(
                        finalUri,
                        _options.ManifestPolicy.AllowLoopbackHttp,
                        allowQuery: true))
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
            if (!UpdateManifestVerifier.IsAllowedNetworkUri(
                    uri,
                    _options.ManifestPolicy.AllowLoopbackHttp,
                    allowQuery: true))
            {
                throw new InvalidDataException("更新请求被重定向到不安全的地址。");
            }
        }
        throw new HttpRequestException("更新请求重定向次数过多。");
    }

    private void ValidateInitialUri(Uri uri)
    {
        if (!UpdateManifestVerifier.IsAllowedNetworkUri(
                uri,
                _options.ManifestPolicy.AllowLoopbackHttp,
                allowQuery: false))
        {
            throw new InvalidDataException("更新地址必须使用无凭据、查询或片段的 HTTPS 地址。");
        }
    }

    private static async Task WriteStateAsync(
        string directory,
        VerifiedUpdateManifest manifest,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(directory, "staged-update.json");
        var temporaryPath = statePath + ".tmp";
        var state = new
        {
            manifest.Document.ProductId,
            manifest.Document.Channel,
            Version = manifest.Version.ToString(3),
            PackageFileName = Path.GetFileName(packagePath),
            Sha256 = manifest.Document.Asset.Sha256.ToUpperInvariant(),
            StagedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, StateSerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, statePath, overwrite: true);
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

    private static void EnsureNoReparsePoint(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null && current.Exists; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("更新 staging 路径不能包含重解析点。");
            }
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class DownloadProgress(IProgress<double> progress, long expectedSize) : IProgress<long>
    {
        public void Report(long value)
        {
            progress.Report(expectedSize == 0 ? 1 : (double)value / expectedSize);
        }
    }
}
