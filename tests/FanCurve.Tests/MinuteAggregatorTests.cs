using FanCurve.Core;

namespace FanCurve.Tests;

public class MinuteAggregatorTests
{
    [Fact]
    public void NanIsLeftOutOfMinAvgMaxAndFullScaleStillCountsTheSample()
    {
        var table = ChannelTable.Create(new ChannelDesc(ChannelKind.Percent, "Fan", "rule:cpu:out"));
        var aggregator = new MinuteAggregator(table);
        var minute = 1_700_000_040_000;

        Assert.Null(aggregator.Add(minute + 1_000, [(Half)10]));
        Assert.Null(aggregator.Add(minute + 2_000, [Half.NaN]));
        Assert.Null(aggregator.Add(minute + 3_000, [(Half)30]));
        var record = aggregator.Flush();

        Assert.NotNull(record);
        Assert.Equal(minute, record.MinuteUnix);
        Assert.Equal(3, record.SampleCount);
        Assert.Equal(10f, (float)record.Min[0]);
        Assert.Equal(30f, (float)record.Max[0]);
        Assert.Equal(20f, (float)record.Avg[0]);
        Assert.Equal(0, record.SamplesAt100[0]);
    }

    [Fact]
    public void SamplesAt100CountsOnlyPercentChannels()
    {
        var table = ChannelTable.Create(
            new ChannelDesc(ChannelKind.Percent, "Fan", "rule:cpu:out"),
            new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var aggregator = new MinuteAggregator(table);
        var minute = 1_700_000_100_000;
        aggregator.Add(minute, [(Half)100, (Half)70]);
        aggregator.Add(minute + 1_000, [(Half)50, (Half)100]);
        aggregator.Add(minute + 2_000, [(Half)100, (Half)71]);
        aggregator.Add(minute + 3_000, [(Half)99, (Half)72]);
        var record = aggregator.Flush();

        Assert.NotNull(record);
        Assert.Equal(2, record!.SamplesAt100[0]);
        Assert.Equal(0, record.SamplesAt100[1]);
        Assert.Equal(87.25f, (float)record.Avg[0]);
    }

    [Fact]
    public void HourJumpClosesThePartialMinuteWithoutFillingTheGap()
    {
        var table = ChannelTable.Create(new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var aggregator = new MinuteAggregator(table);
        var first = 1_700_000_160_000;
        Assert.Null(aggregator.Add(first, [(Half)40]));
        var closed = aggregator.Add(first + (3 * 3_600_000), [(Half)55]);

        Assert.NotNull(closed);
        Assert.Equal(MinuteAggregator.MinuteOf(first), closed!.MinuteUnix);
        Assert.Equal(1, closed.SampleCount);
        Assert.Equal(40f, (float)closed.Avg[0]);

        var second = aggregator.Flush();
        Assert.NotNull(second);
        Assert.Equal(1, second!.SampleCount);
        Assert.True(second.MinuteUnix - closed.MinuteUnix >= 3 * 3_600_000 - 60_000);
    }
}
