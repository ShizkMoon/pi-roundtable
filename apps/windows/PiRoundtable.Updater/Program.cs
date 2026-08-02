using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace PiRoundtable.Updater;

public static class InstallerExitCodes
{
    public static bool IsSuccessful(int exitCode) => exitCode is 0 or 3010;

    public static bool RestartWasInitiated(int exitCode) => exitCode == 1641;
}

internal sealed record UpdateArguments(
    string MsiPath,
    long ExpectedSize,
    byte[] ExpectedSha256,
    int ParentProcessId,
    long ParentStartTimeUtcTicks,
    string RestartExecutable);

internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PiRoundtable",
        "updates",
        "updater.log");

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = ParseArguments(args);
            Log("Updater started; waiting for the verified parent process to exit.");
            await WaitForParentAsync(parsed);
            await using var packageLock = await VerifiedPackageLock.OpenAsync(
                parsed.MsiPath,
                parsed.ExpectedSize,
                parsed.ExpectedSha256);
            Log("Staged MSI was reverified and locked against replacement.");

            var startInfo = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(parsed.MsiPath)!,
            };
            startInfo.ArgumentList.Add("/i");
            startInfo.ArgumentList.Add(parsed.MsiPath);
            startInfo.ArgumentList.Add("/passive");
            startInfo.ArgumentList.Add("/norestart");

            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Installer could not be started.");
            await installer.WaitForExitAsync();
            var exitCode = installer.ExitCode;
            await packageLock.DisposeAsync();
            Log($"Windows Installer exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.");

            if (InstallerExitCodes.IsSuccessful(exitCode))
            {
                Restart(parsed.RestartExecutable);
                TryDelete(parsed.MsiPath);
                return 0;
            }
            if (InstallerExitCodes.RestartWasInitiated(exitCode))
            {
                Log("Windows Installer initiated a system restart; application relaunch is skipped.");
                return 0;
            }
            return exitCode == 0 ? 1 : exitCode;
        }
        catch (Exception exception)
        {
            Log($"Updater failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static UpdateArguments ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Updater arguments are incomplete or duplicated.");
            }
        }

        var msiPath = RequireAbsoluteFile(values, "--msi", ".msi");
        var restartExecutable = RequireAbsoluteFile(values, "--restart-exe", ".exe");
        if (!string.Equals(Path.GetFileName(restartExecutable), "PiRoundtable.Windows.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The restart target is not PiRoundtable.Windows.exe.");
        }
        if (!values.TryGetValue("--expected-size", out var sizeText) ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedSize) ||
            expectedSize <= 0 ||
            !values.TryGetValue("--expected-sha256", out var hashText) ||
            !TryParseSha256(hashText, out var expectedSha256) ||
            !values.TryGetValue("--parent-pid", out var parentText) ||
            !int.TryParse(parentText, NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) ||
            parentProcessId <= 0 ||
            !values.TryGetValue("--parent-start-time-utc-ticks", out var ticksText) ||
            !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks <= 0 ||
            values.Count != 6)
        {
            throw new ArgumentException("The parent process identity is invalid.");
        }
        return new UpdateArguments(msiPath, expectedSize, expectedSha256, parentProcessId, ticks, restartExecutable);
    }

    private static bool TryParseSha256(string value, out byte[] hash)
    {
        hash = [];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }
        hash = Convert.FromHexString(value);
        return hash.Length == 32;
    }

    private static string RequireAbsoluteFile(
        IReadOnlyDictionary<string, string> values,
        string name,
        string extension)
    {
        if (!values.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
        {
            throw new ArgumentException($"{name} must be an absolute file path.");
        }
        var path = Path.GetFullPath(raw);
        if (!File.Exists(path) ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException($"{name} is missing, has the wrong extension, or is a reparse point.", path);
        }
        return path;
    }

    private static async Task WaitForParentAsync(UpdateArguments arguments)
    {
        using var parent = Process.GetProcessById(arguments.ParentProcessId);
        var actualTicks = parent.StartTime.ToUniversalTime().Ticks;
        if (actualTicks != arguments.ParentStartTimeUtcTicks)
        {
            throw new InvalidOperationException("Parent process identity changed before update handoff.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await parent.WaitForExitAsync(timeout.Token);
    }

    private static void Restart(string executable)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The updated application executable is missing.", executable);
        }
        _ = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        });
        Log("Updated application relaunched.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log("Verified MSI cleanup was deferred.");
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

public static class VerifiedPackageLock
{
    public static async Task<FileStream> OpenAsync(
        string path,
        long expectedSize,
        ReadOnlyMemory<byte> expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSize <= 0 || expectedSha256.Length != 32)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize), "Expected MSI size and SHA-256 must be complete.");
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The staged MSI cannot be a reparse point.");
        }
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != expectedSize)
            {
                throw new InvalidDataException("The staged MSI size changed after client verification.");
            }
            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedSha256.Span))
            {
                throw new CryptographicException("The staged MSI hash changed after client verification.");
            }
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
