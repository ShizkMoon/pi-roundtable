using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class TranscriptFollowPolicyTests
{
    [TestMethod]
    [DataRow(0d, 0d, true)]
    [DataRow(976d, 1000d, true)]
    [DataRow(975.9d, 1000d, false)]
    [DataRow(1200d, 1000d, true)]
    public void DetectsWhetherTheViewportIsNearTheLatestMessage(
        double verticalOffset,
        double scrollableHeight,
        bool expected)
    {
        Assert.AreEqual(expected, TranscriptFollowPolicy.IsAtLatest(verticalOffset, scrollableHeight));
    }

    [TestMethod]
    public void RejectsInvalidViewportMetrics()
    {
        Assert.IsFalse(TranscriptFollowPolicy.IsAtLatest(double.NaN, 100));
        Assert.IsFalse(TranscriptFollowPolicy.IsAtLatest(0, double.PositiveInfinity));
    }
}
