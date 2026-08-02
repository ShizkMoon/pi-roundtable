using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class ToolApprovalItemTests
{
    [TestMethod]
    public void Deadline_disables_late_decision_and_explains_automatic_denial()
    {
        var requestedAt = DateTimeOffset.Parse("2026-08-02T10:00:00Z");
        var approval = new ToolApprovalItem(
            "approval-1",
            "role-1",
            "角色",
            "Notes",
            "Write note",
            requestedAt,
            requestedAt.AddSeconds(30));

        approval.RefreshDeadline(requestedAt.AddSeconds(29));
        Assert.IsTrue(approval.CanResolve);
        StringAssert.Contains(approval.ExpiryLabel, "剩余 1 秒");

        approval.RefreshDeadline(requestedAt.AddSeconds(30));
        Assert.IsFalse(approval.CanResolve);
        Assert.IsTrue(approval.IsExpired);
        StringAssert.Contains(approval.ExpiryLabel, "Runtime 自动拒绝");
    }
}
