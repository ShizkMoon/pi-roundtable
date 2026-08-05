using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class ArtifactStoreTests
{
    [TestMethod]
    public async Task Content_addressed_artifact_is_shared_and_deleted_after_its_last_meeting_binding()
    {
        var root = TestRoot();
        try
        {
            var source = Path.Combine(root, "decision.md");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(source, "reviewed decision");
            var preflight = await new DocumentPipeline().PreflightAsync(source);
            var store = new ArtifactStore(root, quotaBytes: 1024);

            await store.ImportAsync(source, preflight.Descriptor);
            await store.BindToMeetingAsync(preflight.Descriptor.ArtifactId, "meeting-a");
            await store.BindToMeetingAsync(preflight.Descriptor.ArtifactId, "meeting-b");
            Assert.AreEqual(1, (await store.GetUsageAsync()).ArtifactCount);

            await store.DeleteMeetingAsync("meeting-a");
            Assert.AreEqual(1, (await store.GetUsageAsync()).ArtifactCount);
            await store.DeleteMeetingAsync("meeting-b");
            Assert.AreEqual(0, (await store.GetUsageAsync()).ArtifactCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Quota_evicts_only_unbound_artifacts_and_fails_closed_for_bound_content()
    {
        var root = TestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var firstPath = Path.Combine(root, "first.md");
            var secondPath = Path.Combine(root, "second.md");
            var thirdPath = Path.Combine(root, "third.md");
            await File.WriteAllTextAsync(firstPath, "123456");
            await File.WriteAllTextAsync(secondPath, "abcdefg");
            await File.WriteAllTextAsync(thirdPath, "hijklmn");
            var pipeline = new DocumentPipeline();
            var first = await pipeline.PreflightAsync(firstPath);
            var second = await pipeline.PreflightAsync(secondPath);
            var third = await pipeline.PreflightAsync(thirdPath);
            var store = new ArtifactStore(root, quotaBytes: 12);

            await store.ImportAsync(firstPath, first.Descriptor);
            await store.ImportAsync(secondPath, second.Descriptor);
            var afterEviction = await store.GetUsageAsync();
            Assert.AreEqual(1, afterEviction.ArtifactCount);
            Assert.AreEqual(7L, afterEviction.StoredBytes);
            await store.BindToMeetingAsync(second.Descriptor.ArtifactId, "meeting-bound");

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => store.ImportAsync(thirdPath, third.Descriptor));
            Assert.AreEqual(1, (await store.GetUsageAsync()).ArtifactCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Import_rehashes_the_source_and_rejects_post_preflight_changes()
    {
        var root = TestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "changed.md");
            await File.WriteAllTextAsync(source, "before");
            var preflight = await new DocumentPipeline().PreflightAsync(source);
            await File.WriteAllTextAsync(source, "after!");

            var store = new ArtifactStore(root, quotaBytes: 1024);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => store.ImportAsync(source, preflight.Descriptor));
            Assert.AreEqual(0, (await store.GetUsageAsync()).ArtifactCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TestRoot() =>
        Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
