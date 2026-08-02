using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class CatalogImportService
{
    private const int MaxFileCount = 2000;
    private const long MaxTotalBytes = 64L * 1024 * 1024;
    private const long MaxFileBytes = 4L * 1024 * 1024;
    private const int MaxSnapshotBytes = 192 * 1024;
    private const int MaxSnapshotFiles = 16;
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "gitlab.com",
        "codeberg.org",
        "gitee.com",
        "bitbucket.org",
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".toml", ".yaml", ".yml", ".xml",
        ".ts", ".tsx", ".js", ".mjs", ".cjs", ".py", ".rs", ".go", ".java", ".cs",
    };
    private readonly string _temporaryRoot;
    private readonly string _catalogRoot;

    public CatalogImportService(string? rootDirectory = null)
    {
        var root = LocalDataRoot.Resolve(rootDirectory);
        _temporaryRoot = Path.Combine(root, "imports");
        _catalogRoot = Path.Combine(root, "catalog");
        CleanupStaleImports();
    }

    public async Task<CatalogCheckout> PrepareAsync(Uri source, CancellationToken cancellationToken = default)
    {
        var parsed = ParseSource(source);
        var checkoutRoot = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(checkoutRoot, "repo");
        Directory.CreateDirectory(checkoutRoot);
        try
        {
            var arguments = new List<string>
            {
                "-c", "protocol.file.allow=never", "clone", "--depth", "1", "--single-branch", "--no-tags",
            };
            if (!string.IsNullOrEmpty(parsed.Reference))
            {
                arguments.Add("--branch");
                arguments.Add(parsed.Reference);
            }
            arguments.Add(parsed.CloneUri.AbsoluteUri);
            arguments.Add(repositoryRoot);
            await RunGitAsync(arguments, checkoutRoot, TimeSpan.FromSeconds(90), cancellationToken);
            var modeOutput = await RunGitAsync(
                ["ls-files", "-s", "-z"],
                repositoryRoot,
                TimeSpan.FromSeconds(15),
                cancellationToken,
                2 * 1024 * 1024);
            foreach (var entry in modeOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var mode = entry.Split(' ', 2)[0];
                if (mode is "120000" or "160000")
                {
                    throw new InvalidOperationException("仓库包含符号链接或 Git 子模块，已拒绝自动导入。");
                }
            }

            var scopeRoot = SafeCombine(repositoryRoot, parsed.Subpath);
            if (!Directory.Exists(scopeRoot))
            {
                throw new InvalidOperationException("链接指定的仓库子目录不存在。");
            }
            var snapshot = BuildSnapshot(source, scopeRoot, parsed.Subpath);
            return new CatalogCheckout(checkoutRoot, scopeRoot, snapshot);
        }
        catch
        {
            DeleteDirectoryBestEffort(checkoutRoot);
            throw;
        }
    }

    public async Task<CatalogInstallResult> InstallAsync(
        CatalogCheckout checkout,
        string kind,
        string catalogId,
        string relativeRoot,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("skill" or "mcp") ||
            string.IsNullOrWhiteSpace(catalogId) ||
            catalogId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new InvalidOperationException("目录条目标识无效。");
        }
        var selectedRoot = SafeCombine(checkout.ScopeRoot, relativeRoot);
        if (!Directory.Exists(selectedRoot))
        {
            throw new InvalidOperationException("LLM 建议的安装目录不存在。");
        }
        if (kind == "skill" && !File.Exists(Path.Combine(selectedRoot, "SKILL.md")))
        {
            throw new InvalidOperationException("所选 Skill 目录不包含 SKILL.md。");
        }

        var kindRoot = Path.Combine(_catalogRoot, kind == "skill" ? "skills" : "mcp");
        Directory.CreateDirectory(kindRoot);
        var destination = SafeCombine(kindRoot, catalogId);
        var stage = Path.Combine(kindRoot, $".{catalogId}.stage.{Guid.NewGuid():N}");
        var backup = Path.Combine(kindRoot, $".{catalogId}.backup.{Guid.NewGuid():N}");
        try
        {
            await Task.Run(() => CopyTree(selectedRoot, stage, cancellationToken), CancellationToken.None);
            var digest = await ComputeDigestAsync(stage, cancellationToken);
            var hadExisting = Directory.Exists(destination);
            if (hadExisting)
            {
                Directory.Move(destination, backup);
            }
            try
            {
                Directory.Move(stage, destination);
            }
            catch
            {
                if (hadExisting && !Directory.Exists(destination) && Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }
                throw;
            }
            DeleteDirectoryBestEffort(backup);
            return new CatalogInstallResult(destination, digest);
        }
        finally
        {
            DeleteDirectoryBestEffort(stage);
            if (Directory.Exists(backup) && !Directory.Exists(destination))
            {
                Directory.Move(backup, destination);
            }
            else
            {
                DeleteDirectoryBestEffort(backup);
            }
        }
    }

    private static ParsedSource ParseSource(Uri source)
    {
        if (source.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Contains(source.Host) ||
            !string.IsNullOrEmpty(source.UserInfo) ||
            !string.IsNullOrEmpty(source.Query) ||
            !string.IsNullOrEmpty(source.Fragment))
        {
            throw new InvalidOperationException("只允许无凭据、无查询参数的受信 Git 平台 HTTPS 地址。");
        }
        var segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length < 2 || segments.Any(segment => segment is "." or ".." || segment.Contains('\\')))
        {
            throw new InvalidOperationException("Git 仓库地址格式无效。");
        }

        var marker = Array.FindIndex(segments, 2, segment => segment.Equals("tree", StringComparison.OrdinalIgnoreCase));
        if (marker < 0)
        {
            var gitlabMarker = Array.FindIndex(segments, 2, segment => segment == "-");
            if (gitlabMarker >= 0 && segments.ElementAtOrDefault(gitlabMarker + 1)?.Equals("tree", StringComparison.OrdinalIgnoreCase) == true)
            {
                marker = gitlabMarker + 1;
            }
        }
        string? reference = null;
        var subpath = string.Empty;
        var repositoryEnd = marker > 0 && segments[marker - 1] == "-" ? marker - 1 : marker;
        var repositorySegments = marker >= 0 ? segments[..repositoryEnd] : segments;
        if (marker >= 0)
        {
            if (segments.Length <= marker + 1)
            {
                throw new InvalidOperationException("Git tree 链接缺少分支名称。");
            }
            reference = segments[marker + 1];
            if (!IsSafeReference(reference))
            {
                throw new InvalidOperationException("Git 分支名称不符合安全约束。");
            }
            subpath = string.Join(Path.DirectorySeparatorChar, segments.Skip(marker + 2));
        }
        if (repositorySegments.Length < 2)
        {
            throw new InvalidOperationException("Git 仓库地址缺少所有者或仓库名。");
        }
        var repositoryPath = string.Join('/', repositorySegments).TrimEnd('/');
        var cloneUri = new Uri($"https://{source.Host}/{repositoryPath}");
        return new ParsedSource(cloneUri, reference, subpath);
    }

    private static bool IsSafeReference(string value) => value.Length is > 0 and <= 128 &&
        value[0] != '-' && !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static CatalogRepositorySnapshot BuildSnapshot(Uri source, string scopeRoot, string requestedSubpath)
    {
        var files = EnumerateAndValidateFiles(scopeRoot);
        var relativeFiles = files
            .Select(file => Path.GetRelativePath(scopeRoot, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var skillRoots = relativeFiles
            .Where(file => Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetDirectoryName(file)?.Replace('\\', '/') ?? ".")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var prioritized = files
            .OrderBy(file => SnapshotPriority(Path.GetFileName(file)))
            .ThenBy(file => Path.GetRelativePath(scopeRoot, file), StringComparer.Ordinal)
            .Take(MaxSnapshotFiles * 3);
        var textFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var file in prioritized)
        {
            if (textFiles.Count >= MaxSnapshotFiles || !IsSnapshotTextFile(file))
            {
                continue;
            }
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 64 * 1024 || bytes.Contains((byte)0) || total + bytes.Length > MaxSnapshotBytes)
            {
                continue;
            }
            var text = RedactPotentialSecrets(Encoding.UTF8.GetString(bytes));
            textFiles[Path.GetRelativePath(scopeRoot, file).Replace('\\', '/')] = text;
            total += bytes.Length;
        }
        return new CatalogRepositorySnapshot(source, requestedSubpath, relativeFiles, textFiles, skillRoots);
    }

    private static List<string> EnumerateAndValidateFiles(string root)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        long totalBytes = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("导入范围包含重解析点，已拒绝自动安装。");
            }
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (Path.GetFileName(childDirectory).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                directories.Push(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("导入范围包含文件链接，已拒绝自动安装。");
                }
                if (info.Length > MaxFileBytes)
                {
                    throw new InvalidOperationException("仓库包含超过 4 MiB 的单文件，已拒绝自动导入。");
                }
                totalBytes += info.Length;
                files.Add(file);
                if (files.Count > MaxFileCount || totalBytes > MaxTotalBytes)
                {
                    throw new InvalidOperationException("仓库超过 2000 文件或 64 MiB 的导入上限。");
                }
            }
        }
        return files;
    }

    private static int SnapshotPriority(string name) => name.ToLowerInvariant() switch
    {
        "skill.md" => 0,
        "readme.md" or "readme" => 1,
        "package.json" or "pyproject.toml" or "cargo.toml" or "go.mod" => 2,
        "requirements.txt" or "dockerfile" or "mcp.json" => 3,
        _ => 10,
    };

    private static bool IsSnapshotTextFile(string path)
    {
        var name = Path.GetFileName(path);
        return TextExtensions.Contains(Path.GetExtension(path)) ||
               name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                if (Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("复制期间检测到重解析点。");
                }
                var targetDirectory = Path.Combine(current.Destination, Path.GetFileName(directory));
                Directory.CreateDirectory(targetDirectory);
                pending.Push((directory, targetDirectory));
            }
            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("复制期间检测到文件链接。");
                }
                File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), true);
            }
        }
    }

    private static async Task<string> ComputeDigestAsync(string root, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(file => Path.GetRelativePath(root, file), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            await using var stream = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }
                hash.AppendData(buffer.AsSpan(0, count));
            }
        }
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static async Task<string> RunGitAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maxOutputCharacters = 64 * 1024)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git.exe",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Git。");
        }
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxOutputCharacters, timeoutSource.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, maxOutputCharacters, timeoutSource.Token);
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(timeoutSource.Token),
                stdoutTask,
                stderrTask);
        }
        catch
        {
            TryKill(process);
            throw;
        }
        var stdout = await stdoutTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Git 无法下载或读取该仓库，请检查地址、分支和网络。");
        }
        return stdout;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 16 * 1024));
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return builder.ToString();
            }
            if (builder.Length + count > maxCharacters)
            {
                throw new InvalidOperationException("Git 输出超过安全上限。");
            }
            builder.Append(buffer, 0, count);
        }
    }

    private static string RedactPotentialSecrets(string text)
    {
        var redacted = Regex.Replace(
            text,
            @"(?i)(api[_-]?key|access[_-]?token|token|client[_-]?secret|password)\s*[:=]\s*[""']?[^""'\s,;]+",
            "$1=<redacted>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        redacted = Regex.Replace(
            redacted,
            @"\b(?:sk|ghp|gho|ghu|ghs|github_pat|glpat)-[A-Za-z0-9_-]{12,}\b",
            "<redacted-token>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        redacted = Regex.Replace(
            redacted,
            @"(?is)-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----.*?-----END(?: [A-Z0-9]+)? PRIVATE KEY-----",
            "<redacted-private-key>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        redacted = Regex.Replace(
            redacted,
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            "<redacted-jwt>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        redacted = Regex.Replace(
            redacted,
            @"(?i)\b(authorization\s*:\s*bearer)\s+[^\s,;]+",
            "$1 <redacted>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        redacted = Regex.Replace(
            redacted,
            @"(?i)\b(https?|postgres(?:ql)?|mysql|mongodb(?:\+srv)?):\/\/[^\s\/@:]+:[^\s\/@]+@",
            "$1://<redacted-credentials>@",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        return Regex.Replace(
            redacted,
            @"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b",
            "<redacted-access-key>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.Equals(canonicalRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("导入路径越过了允许的目录边界。");
        }
        return candidate;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Best-effort process cleanup.
        }
    }

    internal static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Startup cleanup can retry stale import directories later.
        }
    }

    private void CleanupStaleImports()
    {
        try
        {
            if (!Directory.Exists(_temporaryRoot))
            {
                return;
            }
            foreach (var directory in Directory.EnumerateDirectories(_temporaryRoot))
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    DeleteDirectoryBestEffort(directory);
                }
            }
        }
        catch
        {
            // Cleanup never blocks application startup.
        }
    }

    private sealed record ParsedSource(Uri CloneUri, string? Reference, string Subpath);
}

internal sealed class CatalogCheckout : IAsyncDisposable
{
    private readonly string _checkoutRoot;
    private int _disposed;

    internal CatalogCheckout(string checkoutRoot, string scopeRoot, CatalogRepositorySnapshot snapshot)
    {
        _checkoutRoot = checkoutRoot;
        ScopeRoot = scopeRoot;
        Snapshot = snapshot;
    }

    public string ScopeRoot { get; }
    public CatalogRepositorySnapshot Snapshot { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await Task.Run(() =>
        {
            CatalogImportService.DeleteDirectoryBestEffort(_checkoutRoot);
        });
    }
}
