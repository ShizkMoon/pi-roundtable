using System.Text;
using System.Text.Json;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class SessionTransferServiceTests
{
    [TestMethod]
    public void Public_export_omits_private_content_and_roundtrips_through_strict_preflight()
    {
        var session = CreateSession();

        var package = SessionTransferService.CreatePackage(
            session,
            includePrivateMessages: false,
            DateTimeOffset.Parse("2026-08-02T04:00:00Z"));
        var json = SessionTransferService.SerializeJson(package);
        var text = Encoding.UTF8.GetString(json);
        var preflight = SessionTransferService.Preflight(json);

        Assert.HasCount(1, package.Messages);
        Assert.IsFalse(text.Contains("PRIVATE-SECRET", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("credentialRef", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("toolArgs", StringComparison.Ordinal));
        Assert.AreEqual(1, preflight.PublicMessageCount);
        Assert.AreEqual(0, preflight.PrivateMessageCount);
    }

    [TestMethod]
    public void Explicit_private_export_preserves_audience_and_markdown_marks_scope()
    {
        var package = SessionTransferService.CreatePackage(
            CreateSession(),
            includePrivateMessages: true,
            DateTimeOffset.Parse("2026-08-02T04:00:00Z"));

        var preflight = SessionTransferService.Preflight(SessionTransferService.SerializeJson(package));
        var markdown = SessionTransferService.RenderMarkdown(package);

        Assert.AreEqual(1, preflight.PublicMessageCount);
        Assert.AreEqual(1, preflight.PrivateMessageCount);
        CollectionAssert.AreEqual(
            new[] { "role.architect", "user.direct_host" },
            package.Messages.Single(message => message.Visibility == "private").AudienceRoleIds);
        StringAssert.Contains(markdown, "明确包含的私聊");
        StringAssert.Contains(markdown, "私聊 audience");
    }

    [TestMethod]
    public void Preflight_rejects_unknown_duplicate_out_of_order_and_invalid_audience()
    {
        var package = SessionTransferService.CreatePackage(
            CreateSession(),
            includePrivateMessages: true,
            DateTimeOffset.Parse("2026-08-02T04:00:00Z"));
        var valid = Encoding.UTF8.GetString(SessionTransferService.SerializeJson(package));
        var unknown = "{\"credentialRef\":\"secret://must-not-enter\"," + valid[1..];
        var duplicate = valid.Replace("\"packageVersion\": 1", "\"packageVersion\": 1,\n  \"packageVersion\": 1", StringComparison.Ordinal);
        var invalidAudiencePackage = SessionTransferService.CreatePackage(CreateSession(), false);
        invalidAudiencePackage.Messages[0].AudienceRoleIds.Add("role.architect");
        var invalidAudience = SessionTransferService.SerializeJson(invalidAudiencePackage);
        (package.Messages[0], package.Messages[1]) = (package.Messages[1], package.Messages[0]);
        var outOfOrder = SessionTransferService.SerializeJson(package);

        Assert.ThrowsExactly<JsonException>(() => SessionTransferService.Preflight(Encoding.UTF8.GetBytes(unknown)));
        Assert.ThrowsExactly<InvalidDataException>(() => SessionTransferService.Preflight(Encoding.UTF8.GetBytes(duplicate)));
        Assert.ThrowsExactly<InvalidDataException>(() => SessionTransferService.Preflight(invalidAudience));
        Assert.ThrowsExactly<InvalidDataException>(() => SessionTransferService.Preflight(outOfOrder));
    }

    [TestMethod]
    public void Preflight_is_read_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-roundtable-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var before = Directory.EnumerateFileSystemEntries(root).ToArray();
            var package = SessionTransferService.CreatePackage(CreateSession(), false);

            _ = SessionTransferService.Preflight(SessionTransferService.SerializeJson(package));

            var after = Directory.EnumerateFileSystemEntries(root).ToArray();
            CollectionAssert.AreEqual(before, after);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SessionItem CreateSession()
    {
        var session = new SessionItem("session.export-test", "# 架构复盘");
        session.Transcript.Add(new TranscriptItem(
            "user.direct_host",
            "我",
            "公开结论：`runtimeGeneration` 必须校验。",
            "已完成",
            "host",
            "public",
            [],
            "message.public-1",
            DateTimeOffset.Parse("2026-08-02T08:00:00Z")));
        session.GetPrivateThread("role.architect").Add(new TranscriptItem(
            "role.architect",
            "体系架构师",
            "PRIVATE-SECRET credentialRef toolArgs",
            "已完成",
            "role",
            "private",
            ["user.direct_host", "role.architect"],
            "message.private-1",
            DateTimeOffset.Parse("2026-08-02T08:01:00Z")));
        return session;
    }
}
