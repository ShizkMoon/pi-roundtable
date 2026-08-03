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
