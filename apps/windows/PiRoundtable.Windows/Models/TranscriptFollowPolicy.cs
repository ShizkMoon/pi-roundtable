namespace PiRoundtable.Windows.Models;

internal static class TranscriptFollowPolicy
{
    internal const double DefaultThreshold = 24;

    internal static bool IsAtLatest(
        double verticalOffset,
        double scrollableHeight,
        double threshold = DefaultThreshold)
    {
        if (!double.IsFinite(verticalOffset) ||
            !double.IsFinite(scrollableHeight) ||
            !double.IsFinite(threshold))
        {
            return false;
        }

        return verticalOffset >= Math.Max(0, scrollableHeight - Math.Max(0, threshold));
    }
}
