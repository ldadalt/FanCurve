using FanCurve.Core;

namespace FanCurve.Tests;

public class TimelineTests
{
    [Fact]
    public void SixHourHoleBecomesTwoSegments()
    {
        long hour = 3_600_000;
        var stamps = new long[] { 0, 1_000, 2_000, 6 * hour, (6 * hour) + 1_000 };
        var segments = Timeline.SplitOnGaps(stamps);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new SeriesSegment(0, 3), segments[0]);
        Assert.Equal(new SeriesSegment(3, 2), segments[1]);
    }

    [Fact]
    public void ClockGoingBackwardsAlsoSplits()
    {
        var segments = Timeline.SplitOnGaps([10_000, 11_000, 5_000]);
        Assert.Equal(2, segments.Count);
        Assert.Equal(1, segments[1].Count);
    }
}
