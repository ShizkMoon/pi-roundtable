using System.Security.Cryptography;
using System.Text;
using PiRoundtable.Distribution;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class ArtifactIntegrityTests
{
    [TestMethod]
    public async Task Copy_verification_accepts_exact_content_and_reports_written_bytes()
    {
        var payload = Encoding.UTF8.GetBytes("signed-module-payload");
        var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
        await using var source = new MemoryStream(payload, writable: false);
        await using var destination = new MemoryStream();
        var progress = new ByteProgress();

        await ArtifactVerifier.CopyAndVerifyAsync(source, destination, spec, progress);

        CollectionAssert.AreEqual(payload, destination.ToArray());
        Assert.AreEqual(payload.Length, progress.LastValue);
    }

    [TestMethod]
    public async Task Verification_spec_copies_the_digest_before_async_io()
    {
        var payload = Encoding.UTF8.GetBytes("immutable-verification-target");
        var expectedHash = SHA256.HashData(payload);
        var spec = new ArtifactVerificationSpec(payload.Length, expectedHash);
        expectedHash[0] ^= 0xff;
        await using var source = new MemoryStream(payload, writable: false);
        await using var destination = new MemoryStream();

        await ArtifactVerifier.CopyAndVerifyAsync(source, destination, spec);
    }

    [TestMethod]
    public async Task Copy_verification_rejects_oversized_content_before_writing_extra_bytes()
    {
        byte[] expected = [1, 2, 3];
        byte[] oversized = [1, 2, 3, 4];
        var spec = new ArtifactVerificationSpec(expected.Length, SHA256.HashData(expected));
        await using var source = new MemoryStream(oversized, writable: false);
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
            ArtifactVerifier.CopyAndVerifyAsync(source, destination, spec));

        Assert.AreEqual(ArtifactIntegrityFailure.SizeExceeded, exception.Failure);
        Assert.AreEqual(0, destination.Length);
    }

    [TestMethod]
    public async Task Copy_verification_distinguishes_truncation_and_digest_mismatch()
    {
        byte[] expected = [1, 2, 3, 4];
        var spec = new ArtifactVerificationSpec(expected.Length, SHA256.HashData(expected));
        await using var shortSource = new MemoryStream([1, 2, 3], writable: false);
        await using var firstDestination = new MemoryStream();

        var shortException = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
            ArtifactVerifier.CopyAndVerifyAsync(shortSource, firstDestination, spec));

        Assert.AreEqual(ArtifactIntegrityFailure.SizeMismatch, shortException.Failure);

        await using var wrongSource = new MemoryStream([4, 3, 2, 1], writable: false);
        await using var secondDestination = new MemoryStream();
        var hashException = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
            ArtifactVerifier.CopyAndVerifyAsync(wrongSource, secondDestination, spec));

        Assert.AreEqual(ArtifactIntegrityFailure.Sha256Mismatch, hashException.Failure);
    }

    [TestMethod]
    public async Task Verified_file_handle_prevents_write_replacement_until_disposed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-roundtable-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "module.bin");
        var payload = Encoding.UTF8.GetBytes("locked-after-verification");
        await File.WriteAllBytesAsync(path, payload);
        try
        {
            var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
            await using var verified = await ArtifactVerifier.OpenVerifiedReadAsync(path, spec);

            Assert.AreEqual(0, verified.Stream.Position);
            Assert.ThrowsExactly<IOException>(() =>
                new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Existing_file_match_returns_false_for_missing_or_tampered_content()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-roundtable-artifact-match-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "module.bin");
        byte[] expected = [1, 2, 3];
        var spec = new ArtifactVerificationSpec(expected.Length, SHA256.HashData(expected));
        try
        {
            Assert.IsFalse(await ArtifactVerifier.MatchesFileAsync(path, spec));
            await File.WriteAllBytesAsync(path, [3, 2, 1]);
            Assert.IsFalse(await ArtifactVerifier.MatchesFileAsync(path, spec));
            await File.WriteAllBytesAsync(path, expected);
            Assert.IsTrue(await ArtifactVerifier.MatchesFileAsync(path, spec));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Verified_file_rejects_reparse_point_leaf_and_parent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-roundtable-artifact-reparse-{Guid.NewGuid():N}");
        var targetDirectory = Path.Combine(directory, "target");
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, "module.bin");
        byte[] payload = [1, 2, 3];
        await File.WriteAllBytesAsync(targetPath, payload);
        var linkedFile = Path.Combine(directory, "linked-file.bin");
        var linkedDirectory = Path.Combine(directory, "linked-directory");
        File.CreateSymbolicLink(linkedFile, targetPath);
        Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
        var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
        try
        {
            var leafException = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
                ArtifactVerifier.OpenVerifiedReadAsync(linkedFile, spec));
            Assert.AreEqual(ArtifactIntegrityFailure.ReparsePoint, leafException.Failure);

            var parentException = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
                ArtifactVerifier.OpenVerifiedReadAsync(Path.Combine(linkedDirectory, "module.bin"), spec));
            Assert.AreEqual(ArtifactIntegrityFailure.ReparsePoint, parentException.Failure);
        }
        finally
        {
            File.Delete(linkedFile);
            Directory.Delete(linkedDirectory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Bounded_content_accepts_exact_limit_and_rejects_one_extra_byte()
    {
        await using var exact = new MemoryStream([1, 2, 3, 4], writable: false);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4 },
            await BoundedContent.ReadAllBytesAsync(exact, maximumBytes: 4));

        await using var oversized = new MemoryStream([1, 2, 3, 4, 5], writable: false);
        var exception = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
            BoundedContent.ReadAllBytesAsync(oversized, maximumBytes: 4));

        Assert.AreEqual(ArtifactIntegrityFailure.ContentTooLarge, exception.Failure);
    }

    [TestMethod]
    public async Task Maximum_numeric_limits_do_not_overflow_sentinel_reads()
    {
        var hugeSpec = new ArtifactVerificationSpec(long.MaxValue, new byte[ArtifactVerifier.Sha256Length]);
        await using var emptySource = new MemoryStream([], writable: false);
        await using var destination = new MemoryStream();

        var sizeException = await Assert.ThrowsExactlyAsync<ArtifactIntegrityException>(() =>
            ArtifactVerifier.CopyAndVerifyAsync(emptySource, destination, hugeSpec));

        Assert.AreEqual(ArtifactIntegrityFailure.SizeMismatch, sizeException.Failure);

        await using var oneByte = new MemoryStream([42], writable: false);
        CollectionAssert.AreEqual(
            new byte[] { 42 },
            await BoundedContent.ReadAllBytesAsync(oneByte, int.MaxValue));
    }

    [TestMethod]
    public async Task Staging_uses_one_locked_handle_for_copy_verify_and_atomic_promotion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-roundtable-staging-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "nested", "version");
        var payload = Encoding.UTF8.GetBytes("handle-relative-payload");
        var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
        string temporaryPath;
        try
        {
            await using (var staging = ArtifactStager.CreateNew(directory, "update.msi"))
            {
                temporaryPath = staging.CurrentPath;
                await using var source = new MemoryStream(payload, writable: false);
                await staging.CopyAndVerifyAsync(source, spec);

                var observed = new byte[payload.Length];
                Assert.AreEqual(
                    payload.Length,
                    RandomAccess.Read(staging.FileHandle, observed, fileOffset: 0));
                CollectionAssert.AreEqual(payload, observed);
                Assert.IsTrue(File.Exists(temporaryPath));
                Assert.ThrowsExactly<IOException>(() =>
                    Directory.Move(directory, directory + "-moved"));
                Assert.ThrowsExactly<IOException>(() =>
                    Directory.Move(Path.Combine(root, "nested"), Path.Combine(root, "nested-moved")));
                Assert.ThrowsExactly<IOException>(() =>
                    Directory.Move(root, root + "-moved"));

                staging.Promote();

                Assert.IsTrue(staging.IsPromoted);
                Assert.AreEqual(Path.Combine(directory, "update.msi"), staging.CurrentPath);
                Assert.IsFalse(File.Exists(temporaryPath));
                Assert.ThrowsExactly<IOException>(() =>
                    new FileStream(staging.CurrentPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            }

            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(directory, "update.msi")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Promotion_replaces_existing_leaf_and_unpromoted_disposal_deletes_by_handle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-roundtable-staging-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var finalPath = Path.Combine(root, "update.msi");
        await File.WriteAllBytesAsync(finalPath, [9, 9, 9]);
        byte[] payload = [1, 2, 3, 4];
        var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
        try
        {
            await using (var abandoned = ArtifactStager.CreateNew(root, "abandoned.msi"))
            {
                var abandonedPath = abandoned.CurrentPath;
                await using var source = new MemoryStream(payload, writable: false);
                await abandoned.CopyAndVerifyAsync(source, spec);
                Assert.IsTrue(File.Exists(abandonedPath));
            }
            Assert.HasCount(0, Directory.GetFiles(root, "*.partial.msi"));

            await using (var staging = ArtifactStager.CreateNew(root, "update.msi"))
            {
                await using var source = new MemoryStream(payload, writable: false);
                await staging.CopyAndVerifyAsync(source, spec);
                staging.Promote();
                Assert.IsFalse(staging.TryDiscard());
            }

            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(finalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Staging_rejects_a_reparse_directory_before_creating_an_artifact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-roundtable-staging-link-{Guid.NewGuid():N}");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(root, "linked");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            var exception = Assert.ThrowsExactly<ArtifactIntegrityException>(() =>
                ArtifactStager.CreateNew(Path.Combine(link, "version"), "update.msi"));

            Assert.AreEqual(ArtifactIntegrityFailure.ReparsePoint, exception.Failure);
            Assert.HasCount(0, Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Directory_lease_serializes_waiters_and_cleans_only_owned_orphans()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-roundtable-staging-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stalePath = Path.Combine(root, $"update.{Guid.NewGuid():N}.partial.msi");
        var unrelatedPath = Path.Combine(root, "update.not-a-guid.partial.msi");
        await File.WriteAllBytesAsync(stalePath, [1, 2, 3]);
        await File.WriteAllBytesAsync(unrelatedPath, [4, 5, 6]);
        try
        {
            await using (var owner = await ArtifactStager.AcquireDirectoryAsync(root))
            {
                using (var reader = new FileStream(stalePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.AreEqual(0, owner.DeleteStaleArtifactsFor("update.msi"));
                    Assert.IsTrue(File.Exists(stalePath));
                }

                Assert.AreEqual(1, owner.DeleteStaleArtifactsFor("update.msi"));
                Assert.IsFalse(File.Exists(stalePath));
                Assert.IsTrue(File.Exists(unrelatedPath));
                await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                    ArtifactStager.AcquireDirectoryAsync(
                        root,
                        timeout: TimeSpan.FromMilliseconds(150)));

                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
                    ArtifactStager.AcquireDirectoryAsync(
                        root,
                        timeout: TimeSpan.FromSeconds(5),
                        cancellationToken: cancellation.Token));
            }

            await using var successor = await ArtifactStager.AcquireDirectoryAsync(
                root,
                timeout: TimeSpan.FromSeconds(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Promotion_supports_extended_length_destination_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-roundtable-staging-long-{Guid.NewGuid():N}");
        var directory = root;
        while (directory.Length < 280)
        {
            directory = Path.Combine(directory, new string('a', 36));
        }
        byte[] payload = [7, 8, 9];
        var spec = new ArtifactVerificationSpec(payload.Length, SHA256.HashData(payload));
        try
        {
            await using (var staging = ArtifactStager.CreateNew(directory, "update.msi"))
            {
                await using var source = new MemoryStream(payload, writable: false);
                await staging.CopyAndVerifyAsync(source, spec);
                staging.Promote();
            }

            CollectionAssert.AreEqual(
                payload,
                await File.ReadAllBytesAsync(Path.Combine(directory, "update.msi")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Verification_spec_rejects_invalid_hash_material()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ArtifactVerificationSpec(-1, new byte[32]));
        Assert.ThrowsExactly<ArgumentException>(() => new ArtifactVerificationSpec(1, new byte[31]));
        Assert.ThrowsExactly<ArgumentException>(() => ArtifactVerificationSpec.FromSha256Hex(1, new string('z', 64)));
    }

    private sealed class ByteProgress : IProgress<long>
    {
        public long LastValue { get; private set; }

        public void Report(long value)
        {
            LastValue = value;
        }
    }
}
