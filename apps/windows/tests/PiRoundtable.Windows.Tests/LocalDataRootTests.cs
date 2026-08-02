using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class LocalDataRootTests
{
    [TestMethod]
    public void Explicit_root_is_normalized_and_shared_by_all_local_stores()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        var resolved = LocalDataRoot.Resolve(root + Path.DirectorySeparatorChar);

        Assert.AreEqual(Path.GetFullPath(root), resolved);
        Assert.AreEqual(
            Path.Combine(resolved, "workspace.v1.json"),
            new WorkspaceConfigurationStore(resolved).ConfigurationPath);
        Assert.AreEqual(
            Path.Combine(resolved, "data", "roundtable.db"),
            new MeetingEventStore(resolved).DatabasePath);
    }

    [TestMethod]
    public void Relative_or_filesystem_root_is_rejected()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => LocalDataRoot.Resolve("relative-data"));
        Assert.ThrowsExactly<InvalidOperationException>(() => LocalDataRoot.Resolve(Path.GetPathRoot(Environment.SystemDirectory)!));
    }
}
