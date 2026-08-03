using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PiRoundtable.Distribution;

/// <summary>
/// Identifies a machine-readable integrity failure without including a local
/// path, URI, or content in diagnostics.
/// </summary>
public enum ArtifactIntegrityFailure
{
    SizeExceeded,
    SizeMismatch,
    Sha256Mismatch,
    ReparsePoint,
    ContentTooLarge,
}

/// <summary>
/// Reports a failed artifact-integrity invariant. Callers may translate the
/// exception into product-specific language, but must preserve <see cref="Failure"/>
/// for content-free diagnostics.
/// </summary>
public sealed class ArtifactIntegrityException : IOException
{
    public ArtifactIntegrityException(ArtifactIntegrityFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public ArtifactIntegrityFailure Failure { get; }
}

/// <summary>
/// Immutable expected byte count and SHA-256 for one signed or otherwise
/// trusted artifact descriptor. The digest is copied at construction so a
/// caller cannot change the verification target while I/O is in progress.
/// </summary>
public sealed class ArtifactVerificationSpec
{
    private readonly byte[] _expectedSha256;

    public ArtifactVerificationSpec(long expectedSize, ReadOnlySpan<byte> expectedSha256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        if (expectedSha256.Length != ArtifactVerifier.Sha256Length)
        {
            throw new ArgumentException("Expected SHA-256 must contain exactly 32 bytes.", nameof(expectedSha256));
        }

        ExpectedSize = expectedSize;
        _expectedSha256 = expectedSha256.ToArray();
    }

    public long ExpectedSize { get; }

    internal ReadOnlySpan<byte> ExpectedSha256 => _expectedSha256;

    public static ArtifactVerificationSpec FromSha256Hex(long expectedSize, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedSha256.Length != ArtifactVerifier.Sha256Length * 2 ||
            expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Expected SHA-256 must be a 64-character hexadecimal value.", nameof(expectedSha256));
        }

        return new ArtifactVerificationSpec(expectedSize, Convert.FromHexString(expectedSha256));
    }
}

/// <summary>
/// Owns the verified file handle and, on Windows, the no-delete parent
/// directory handles used to keep every resolved path component stable until
/// the consumer finishes using the artifact.
/// </summary>
public sealed class VerifiedArtifactLease : IAsyncDisposable
{
    private FileStream? _stream;
    private List<SafeFileHandle>? _pathLocks;

    internal VerifiedArtifactLease(FileStream stream, List<SafeFileHandle>? pathLocks = null)
    {
        _stream = stream;
        _pathLocks = pathLocks;
    }

    public FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(VerifiedArtifactLease));

    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var pathLocks = Interlocked.Exchange(ref _pathLocks, null);
        try
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
        finally
        {
            if (pathLocks is not null)
            {
                for (var index = pathLocks.Count - 1; index >= 0; index--)
                {
                    pathLocks[index].Dispose();
                }
            }
        }
    }
}

/// <summary>
/// Dependency-free byte-count and SHA-256 verification shared by the desktop
/// updater, module catalog, offline layout, and artifact workers. This class
/// deliberately does not decide manifest trust, URI policy, Authenticode, or
/// install authorization; those remain with the owning boundary.
/// </summary>
public static class ArtifactVerifier
{
    public const int Sha256Length = 32;
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Copies one untrusted stream while enforcing the trusted descriptor.
    /// Bytes beyond the declared size are rejected before they are written.
    /// The destination is not flushed or promoted by this method.
    /// </summary>
    public static async Task CopyAndVerifyAsync(
        Stream source,
        Stream destination,
        ArtifactVerificationSpec spec,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(spec);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        }
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            var requested = GetReadSizeWithSentinel(spec.ExpectedSize, total, buffer.Length);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (read > spec.ExpectedSize - total)
            {
                throw new ArtifactIntegrityException(
                    ArtifactIntegrityFailure.SizeExceeded,
                    "Artifact contains more bytes than its trusted descriptor allows.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }

        VerifyCompleted(total, hash.GetHashAndReset(), spec);
    }

    /// <summary>
    /// Opens a regular file without following a Windows reparse-point leaf,
    /// verifies it from the open handle, and returns that same handle. On
    /// Windows, parent directories are opened without delete sharing while the
    /// leaf is acquired, preventing a path-component replacement race. Holding
    /// the returned handle with <see cref="FileShare.Read"/> prevents write or
    /// delete replacement between verification and use.
    /// </summary>
    public static async Task<VerifiedArtifactLease> OpenVerifiedReadAsync(
        string path,
        ArtifactVerificationSpec spec,
        FileShare fileShare = FileShare.Read,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(spec);
        var lease = OperatingSystem.IsWindows()
            ? WindowsNoFollowFile.OpenRead(path, fileShare, BufferSize)
            : OpenPortableRead(path, fileShare);
        try
        {
            await VerifyAsync(lease.Stream, spec, cancellationToken);
            lease.Stream.Position = 0;
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns false only for a missing file or integrity mismatch. Access and
    /// unexpected I/O failures remain visible to the caller.
    /// </summary>
    public static async Task<bool> MatchesFileAsync(
        string path,
        ArtifactVerificationSpec spec,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var verified = await OpenVerifiedReadAsync(
                path,
                spec,
                FileShare.Read,
                cancellationToken);
            return true;
        }
        catch (ArtifactIntegrityException exception) when (exception.Failure != ArtifactIntegrityFailure.ReparsePoint)
        {
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return false;
        }
    }

    private static async Task VerifyAsync(
        Stream source,
        ArtifactVerificationSpec spec,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length - source.Position != spec.ExpectedSize)
        {
            throw new ArtifactIntegrityException(
                ArtifactIntegrityFailure.SizeMismatch,
                "Artifact byte count does not match its trusted descriptor.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            var requested = GetReadSizeWithSentinel(spec.ExpectedSize, total, buffer.Length);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (read > spec.ExpectedSize - total)
            {
                throw new ArtifactIntegrityException(
                    ArtifactIntegrityFailure.SizeExceeded,
                    "Artifact contains more bytes than its trusted descriptor allows.");
            }
            total += read;
            hash.AppendData(buffer.AsSpan(0, read));
        }

        VerifyCompleted(total, hash.GetHashAndReset(), spec);
    }

    private static void VerifyCompleted(long actualSize, ReadOnlySpan<byte> actualSha256, ArtifactVerificationSpec spec)
    {
        if (actualSize != spec.ExpectedSize)
        {
            throw new ArtifactIntegrityException(
                ArtifactIntegrityFailure.SizeMismatch,
                "Artifact byte count does not match its trusted descriptor.");
        }
        if (!CryptographicOperations.FixedTimeEquals(actualSha256, spec.ExpectedSha256))
        {
            throw new ArtifactIntegrityException(
                ArtifactIntegrityFailure.Sha256Mismatch,
                "Artifact SHA-256 does not match its trusted descriptor.");
        }
    }

    private static int GetReadSizeWithSentinel(long expectedSize, long consumed, int bufferLength)
    {
        var remaining = expectedSize - consumed;
        return remaining >= bufferLength
            ? bufferLength
            : checked((int)remaining + 1);
    }

    private static VerifiedArtifactLease OpenPortableRead(string path, FileShare fileShare)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArtifactIntegrityException(
                ArtifactIntegrityFailure.ReparsePoint,
                "Artifact leaf cannot be a reparse point.");
        }
        return new VerifiedArtifactLease(new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            fileShare,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
}

internal static class WindowsNoFollowFile
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int FileAttributeTagInfoClass = 9;

    public static VerifiedArtifactLease OpenRead(string path, FileShare fileShare, int bufferSize)
    {
        var fullPath = Path.GetFullPath(path);
        var parentHandles = OpenParentDirectories(fullPath);
        var fileHandle = CreateFileW(
            ToExtendedPath(fullPath),
            GenericRead,
            (uint)fileShare,
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagOverlapped,
            nint.Zero);
        if (fileHandle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            fileHandle.Dispose();
            DisposeHandles(parentHandles);
            throw new Win32Exception(error);
        }
        try
        {
            RejectReparsePoint(fileHandle);
            var stream = new FileStream(fileHandle, FileAccess.Read, bufferSize, isAsync: true);
            return new VerifiedArtifactLease(stream, parentHandles);
        }
        catch
        {
            fileHandle.Dispose();
            DisposeHandles(parentHandles);
            throw;
        }
    }

    private static List<SafeFileHandle> OpenParentDirectories(string fullPath)
    {
        var directory = Directory.GetParent(fullPath);
        var directories = new Stack<string>();
        while (directory is not null)
        {
            directories.Push(directory.FullName);
            directory = directory.Parent;
        }

        var handles = new List<SafeFileHandle>(directories.Count);
        try
        {
            while (directories.TryPop(out var path))
            {
                var handle = CreateFileW(
                    ToExtendedPath(path),
                    0,
                    (uint)(FileShare.Read | FileShare.Write),
                    nint.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                    nint.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new Win32Exception(error);
                }
                try
                {
                    RejectReparsePoint(handle);
                    handles.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            return handles;
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private static void RejectReparsePoint(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new ArtifactIntegrityException(
                ArtifactIntegrityFailure.ReparsePoint,
                "Artifact path cannot contain a reparse point.");
        }
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return path;
        }
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return $"\\\\?\\UNC\\{path[2..]}";
        }
        return $"\\\\?\\{path}";
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);
}

/// <summary>
/// Reads small untrusted control documents without trusting a declared content
/// length. The caller remains responsible for media type and schema validation.
/// </summary>
public static class BoundedContent
{
    public static async Task<byte[]> ReadAllBytesAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        }

        using var content = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var bufferLength = maximumBytes < 16 * 1024 ? maximumBytes + 1 : 16 * 1024;
        var buffer = new byte[bufferLength];
        while (true)
        {
            var remaining = maximumBytes - content.Length;
            var requested = remaining >= buffer.Length
                ? buffer.Length
                : checked((int)remaining + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                return content.ToArray();
            }
            if (content.Length + read > maximumBytes)
            {
                throw new ArtifactIntegrityException(
                    ArtifactIntegrityFailure.ContentTooLarge,
                    "Control document exceeds its configured byte limit.");
            }
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
