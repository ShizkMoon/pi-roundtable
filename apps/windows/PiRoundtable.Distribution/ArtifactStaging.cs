using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PiRoundtable.Distribution;

/// <summary>
/// Creates and atomically publishes one Windows artifact while retaining
/// no-delete handles for every directory component and one no-follow leaf
/// handle for the entire copy, verification, trust, and promotion sequence.
/// </summary>
public static class ArtifactStager
{
    private const int DefaultBufferSize = 128 * 1024;
    private static readonly TimeSpan DefaultDirectoryLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Acquires a cross-process lease for one artifact directory. The durable
    /// lock leaf is opened without following reparse points and with no sharing;
    /// a process crash releases the kernel handle automatically. Callers should
    /// hold the lease across stale cleanup, cache reuse, download, and commit.
    /// </summary>
    public static async Task<ArtifactDirectoryLease> AcquireDirectoryAsync(
        string directoryPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Handle-relative artifact staging requires Windows.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var effectiveTimeout = timeout ?? DefaultDirectoryLockTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        var pathLocks = WindowsNoFollowFile.OpenOrCreateDirectoryTree(
            fullDirectoryPath,
            finalDirectoryAccess: 0);
        try
        {
            var lockPath = Path.Combine(fullDirectoryPath, ".pi-roundtable.staging.lock");
            var startedAt = Environment.TickCount64;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lockHandle = WindowsArtifactFile.TryOpenExclusiveLock(lockPath);
                if (lockHandle is not null)
                {
                    return new ArtifactDirectoryLease(fullDirectoryPath, lockHandle, pathLocks);
                }
                if (TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt) >= effectiveTimeout)
                {
                    throw new TimeoutException("Timed out waiting for the artifact directory lease.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
        catch
        {
            WindowsNoFollowFile.DisposeHandles(pathLocks);
            throw;
        }
    }

    /// <summary>
    /// Creates a unique temporary leaf below <paramref name="directoryPath"/>.
    /// Missing directory components are created one at a time while already
    /// resolved ancestors remain locked. This production boundary is Windows
    /// only; callers must not silently fall back to path-only promotion.
    /// </summary>
    public static ArtifactStagingLease CreateNew(
        string directoryPath,
        string publishedFileName,
        int bufferSize = DefaultBufferSize)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Handle-relative artifact staging requires Windows.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ValidateLeafName(publishedFileName, nameof(publishedFileName));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        var pathLocks = WindowsNoFollowFile.OpenOrCreateDirectoryTree(
            fullDirectoryPath,
            finalDirectoryAccess: 0);
        try
        {
            var extension = Path.GetExtension(publishedFileName);
            var stem = Path.GetFileNameWithoutExtension(publishedFileName);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var temporaryFileName = $"{stem}.{Guid.NewGuid():N}.partial{extension}";
                var temporaryPath = Path.Combine(fullDirectoryPath, temporaryFileName);
                var fileHandle = WindowsArtifactFile.TryCreateNew(temporaryPath);
                if (fileHandle is null)
                {
                    continue;
                }
                try
                {
                    WindowsNoFollowFile.RejectReparsePoint(fileHandle);
                    var stream = new FileStream(
                        fileHandle,
                        FileAccess.ReadWrite,
                        bufferSize,
                        isAsync: true);
                    return new ArtifactStagingLease(
                        stream,
                        pathLocks,
                        fullDirectoryPath,
                        temporaryFileName,
                        publishedFileName);
                }
                catch
                {
                    WindowsArtifactFile.TryMarkDelete(fileHandle);
                    fileHandle.Dispose();
                    throw;
                }
            }
            throw new IOException("Unable to allocate a unique artifact staging leaf.");
        }
        catch
        {
            WindowsNoFollowFile.DisposeHandles(pathLocks);
            throw;
        }
    }

    internal static void ValidateLeafName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 180 ||
            value is "." or ".." ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
        {
            throw new ArgumentException("Artifact name must be one safe leaf name.", parameterName);
        }
    }

    internal static bool IsTemporaryLeafFor(string candidate, string publishedFileName)
    {
        if (candidate.Equals($"{publishedFileName}.partial", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals($"{publishedFileName}.tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var extension = Path.GetExtension(publishedFileName);
        var prefix = $"{Path.GetFileNameWithoutExtension(publishedFileName)}.";
        var suffix = $".partial{extension}";
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            candidate.Length != prefix.Length + 32 + suffix.Length)
        {
            return false;
        }
        return candidate.AsSpan(prefix.Length, 32).IndexOfAnyExcept(
            "0123456789abcdefABCDEF".AsSpan()) < 0;
    }
}

/// <summary>
/// Cross-process, crash-released ownership of one artifact directory. Stale
/// cleanup is intentionally available only while this lease is held.
/// </summary>
public sealed class ArtifactDirectoryLease : IAsyncDisposable
{
    private SafeFileHandle? _lockHandle;
    private List<SafeFileHandle>? _pathLocks;

    internal ArtifactDirectoryLease(
        string directoryPath,
        SafeFileHandle lockHandle,
        List<SafeFileHandle> pathLocks)
    {
        DirectoryPath = directoryPath;
        _lockHandle = lockHandle;
        _pathLocks = pathLocks;
    }

    public string DirectoryPath { get; }

    /// <summary>
    /// Deletes crash-orphaned temporary leaves produced for one published
    /// artifact. Names must match the current GUID staging grammar or one exact
    /// legacy suffix; unrelated files are never selected. A matching reparse
    /// leaf fails closed.
    /// </summary>
    public int DeleteStaleArtifactsFor(string publishedFileName)
    {
        ArtifactStager.ValidateLeafName(publishedFileName, nameof(publishedFileName));
        ObjectDisposedException.ThrowIf(_lockHandle is null, this);
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ArtifactStager.IsTemporaryLeafFor(Path.GetFileName(path), publishedFileName))
            {
                continue;
            }
            if (WindowsArtifactFile.TryDeletePathNoFollow(path))
            {
                deleted++;
            }
        }
        return deleted;
    }

    public ValueTask DisposeAsync()
    {
        var lockHandle = Interlocked.Exchange(ref _lockHandle, null);
        var pathLocks = Interlocked.Exchange(ref _pathLocks, null);
        lockHandle?.Dispose();
        if (pathLocks is not null)
        {
            WindowsNoFollowFile.DisposeHandles(pathLocks);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Single-owner state machine for one staged artifact. The borrowed
/// <see cref="FileHandle"/> must never be closed by a consumer. Promotion uses
/// that same source handle while every destination directory component remains
/// locked against replacement.
/// </summary>
public sealed class ArtifactStagingLease : IAsyncDisposable
{
    private FileStream? _stream;
    private List<SafeFileHandle>? _pathLocks;
    private readonly string _directoryPath;
    private readonly string _temporaryFileName;
    private readonly string _publishedFileName;
    private StagingState _state = StagingState.Created;

    internal ArtifactStagingLease(
        FileStream stream,
        List<SafeFileHandle> pathLocks,
        string directoryPath,
        string temporaryFileName,
        string publishedFileName)
    {
        _stream = stream;
        _pathLocks = pathLocks;
        _directoryPath = directoryPath;
        _temporaryFileName = temporaryFileName;
        _publishedFileName = publishedFileName;
    }

    /// <summary>Gets the current canonical path for the still-open leaf.</summary>
    public string CurrentPath => Path.Combine(
        _directoryPath,
        _state == StagingState.Promoted ? _publishedFileName : _temporaryFileName);

    /// <summary>Gets whether the handle has been atomically renamed to its published leaf.</summary>
    public bool IsPromoted => _state == StagingState.Promoted;

    /// <summary>
    /// Gets a borrowed handle for same-file trust verification. The lease must
    /// remain alive for the complete native verification call.
    /// </summary>
    public SafeFileHandle FileHandle =>
        (_stream ?? throw new ObjectDisposedException(nameof(ArtifactStagingLease))).SafeFileHandle;

    /// <summary>
    /// Copies, bounds, hashes, flushes, and re-verifies the artifact using the
    /// same open leaf. A failed or cancelled copy remains unpublished and is
    /// deleted by disposal through that handle.
    /// </summary>
    public async Task CopyAndVerifyAsync(
        Stream source,
        ArtifactVerificationSpec spec,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spec);
        EnsureState(StagingState.Created);
        var stream = _stream ?? throw new ObjectDisposedException(nameof(ArtifactStagingLease));
        await ArtifactVerifier.CopyAndVerifyAsync(
            source,
            stream,
            spec,
            progress,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        await ArtifactVerifier.VerifyOpenStreamAsync(stream, spec, cancellationToken);
        stream.Position = 0;
        _state = StagingState.Verified;
    }

    /// <summary>
    /// Atomically renames the verified open leaf by its source handle while all
    /// destination path components remain locked. This is the commit point and
    /// is intentionally synchronous and non-cancellable.
    /// </summary>
    public void Promote()
    {
        EnsureState(StagingState.Verified);
        var pathLocks = _pathLocks ?? throw new ObjectDisposedException(nameof(ArtifactStagingLease));
        WindowsNoFollowFile.RejectReparsePoint(pathLocks[^1], requireDirectory: true);
        WindowsArtifactFile.Rename(
            FileHandle,
            Path.Combine(_directoryPath, _publishedFileName),
            replaceIfExists: true);
        _state = StagingState.Promoted;
    }

    /// <summary>
    /// Requests deletion of the current leaf through its open handle. This can
    /// roll back a promotion without resolving a potentially replaced path.
    /// </summary>
    public bool TryDiscard()
    {
        if (_stream is null || _state is StagingState.Discarded or StagingState.Disposed)
        {
            return true;
        }
        if (_state == StagingState.Promoted)
        {
            return false;
        }
        if (!WindowsArtifactFile.TryMarkDelete(_stream.SafeFileHandle))
        {
            return false;
        }
        _state = StagingState.Discarded;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var pathLocks = Interlocked.Exchange(ref _pathLocks, null);
        if (stream is null)
        {
            return;
        }
        try
        {
            if (_state is StagingState.Created or StagingState.Verified)
            {
                _ = WindowsArtifactFile.TryMarkDelete(stream.SafeFileHandle);
            }
            await stream.DisposeAsync();
        }
        finally
        {
            if (pathLocks is not null)
            {
                WindowsNoFollowFile.DisposeHandles(pathLocks);
            }
            _state = StagingState.Disposed;
        }
    }

    private void EnsureState(StagingState required)
    {
        if (_state != required)
        {
            throw new InvalidOperationException(
                $"Artifact staging operation requires state {required}; current state is {_state}.");
        }
    }

    private enum StagingState
    {
        Created,
        Verified,
        Promoted,
        Discarded,
        Disposed,
    }
}

internal static class WindowsArtifactFile
{
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;

    internal static SafeFileHandle? TryCreateNew(string path)
    {
        var handle = CreateFileW(
            WindowsNoFollowFile.ToExtendedPath(path),
            GenericRead | GenericWrite | DeleteAccess,
            (uint)FileShare.Read,
            nint.Zero,
            CreateNew,
            FileFlagOpenReparsePoint |
                FileFlagSequentialScan |
                FileFlagOverlapped |
                FileFlagWriteThrough,
            nint.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is ErrorFileExists or ErrorAlreadyExists)
        {
            return null;
        }
        throw new Win32Exception(error);
    }

    internal static SafeFileHandle? TryOpenExclusiveLock(string path)
    {
        var handle = CreateFileW(
            WindowsNoFollowFile.ToExtendedPath(path),
            GenericRead | GenericWrite,
            shareMode: 0,
            nint.Zero,
            OpenAlways,
            FileFlagOpenReparsePoint | FileFlagWriteThrough,
            nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is ErrorSharingViolation or ErrorLockViolation)
            {
                return null;
            }
            throw new Win32Exception(error);
        }
        try
        {
            WindowsNoFollowFile.RejectReparsePoint(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static bool TryDeletePathNoFollow(string path)
    {
        var handle = CreateFileW(
            WindowsNoFollowFile.ToExtendedPath(path),
            DeleteAccess | GenericRead,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return true;
            }
            if (error is ErrorSharingViolation or ErrorLockViolation)
            {
                return false;
            }
            throw new Win32Exception(error);
        }
        try
        {
            WindowsNoFollowFile.RejectReparsePoint(handle);
            if (TryMarkDelete(handle, out var error))
            {
                return true;
            }
            if (error is ErrorSharingViolation or ErrorLockViolation)
            {
                return false;
            }
            throw new Win32Exception(error);
        }
        finally
        {
            handle.Dispose();
        }
    }

    internal static void Rename(
        SafeFileHandle source,
        string targetPath,
        bool replaceIfExists)
    {
        var nameBytes = Encoding.Unicode.GetBytes(WindowsNoFollowFile.ToExtendedPath(targetPath));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var nameLengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = nameLengthOffset + sizeof(int);
        var structureSize = IntPtr.Size == 8 ? 24 : 16;
        var bufferSize = checked(structureSize + nameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteInt32(buffer, replaceIfExists ? 1 : 0);
            Marshal.WriteIntPtr(buffer, rootOffset, nint.Zero);
            Marshal.WriteInt32(buffer, nameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
            if (!SetFileInformationByHandle(
                    source,
                    FileRenameInfoClass,
                    buffer,
                    (uint)bufferSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool TryMarkDelete(SafeFileHandle handle)
    {
        return TryMarkDelete(handle, out _);
    }

    private static bool TryMarkDelete(SafeFileHandle handle, out int error)
    {
        var disposition = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(disposition, 1);
            var deleted = SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                disposition,
                1);
            error = deleted ? 0 : Marshal.GetLastPInvokeError();
            return deleted;
        }
        finally
        {
            Marshal.FreeHGlobal(disposition);
        }
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
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);
}
