using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class SessionStoreTests
{
    [TestMethod]
    public async Task Session_store_round_trips_a_draft_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RoundtableSessionStore(root);
            var session = new RoundtableSessionConfiguration
            {
                SessionId = "session-test",
                WorkspaceId = "workspace-test",
                Title = "恢复测试",
                Phase = "draft",
                Agenda = new SessionAgendaConfiguration { Subject = "验证持久化" },
            };

            await store.SaveAsync(session);
            var loaded = await store.LoadAllAsync();

            Assert.HasCount(1, loaded);
            Assert.AreEqual("session-test", loaded[0].SessionId);
            Assert.AreEqual("恢复测试", loaded[0].Title);
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "sessions"), "*.tmp").Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
