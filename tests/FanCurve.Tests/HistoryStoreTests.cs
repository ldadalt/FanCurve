using FanCurve.Core;

namespace FanCurve.Tests;

public class HistoryStoreTests
{
    [Fact]
    public void RoundTripKeepsNanLimitsAndSamplesAt100()
    {
        using var dir = new TempFolder();
        var table = ChannelTable.Create(
            new ChannelDesc(ChannelKind.Percent, "Fan", "rule:cpu:out"),
            new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var minute = DateTimeOffset.Parse("2026-11-12T15:04:00Z").ToUnixTimeMilliseconds();
        var record = new MinuteRecord(
            minute,
            4,
            [(Half)0, (Half)(-40)],
            [(Half)74.75f, Half.NaN],
            [(Half)100, (Half)65504f],
            [2, 0]);

        using (var writer = new HistoryWriter(dir.Path, table))
        {
            writer.Append(record);
            writer.Append(record);
            var visible = HistoryReader.Read(dir.Path, minute, minute + 60_000);
            Assert.Single(visible);
        }

        var read = HistoryReader.Read(dir.Path, 0, long.MaxValue);
        Assert.Single(read);
        Assert.Equal(minute, read[0].MinuteUnix);
        Assert.Equal(4, read[0].SampleCount);
        Assert.Equal(2, read[0].SamplesAt100[0]);
        Assert.Equal(0, read[0].SamplesAt100[1]);
        Assert.Equal(0f, (float)read[0].Min[0]);
        Assert.Equal(-40f, (float)read[0].Min[1]);
        Assert.Equal(74.75f, (float)read[0].Avg[0]);
        Assert.True(Half.IsNaN(read[0].Avg[1]));
        Assert.Equal(100f, (float)read[0].Max[0]);
        Assert.Equal(65504f, (float)read[0].Max[1]);
    }

    [Fact]
    public void PartialTailIsTruncatedAndClockBackOpensAnotherFile()
    {
        using var dir = new TempFolder();
        var table = ChannelTable.Create(new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var later = DateTimeOffset.Parse("2026-11-12T12:00:00Z").ToUnixTimeMilliseconds();
        var earlier = later - (2 * 3_600_000);

        using (var writer = new HistoryWriter(dir.Path, table))
        {
            writer.Append(Minute(later, (Half)60));
        }

        var file = Path.Combine(dir.Path, "2026-11.fch");
        using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Write(new byte[7]);
        }

        using (var writer = new HistoryWriter(dir.Path, table))
        {
            writer.Append(Minute(earlier, (Half)40));
        }

        Assert.True(File.Exists(Path.Combine(dir.Path, "2026-11.2.fch")));
        var length = new FileInfo(file).Length;
        var expected = HistoryFormat.HeaderSizeFor(1) + StorageLayout.HistoryRecordSize(1);
        Assert.Equal(expected, length);

        var read = HistoryReader.Read(dir.Path, earlier, later + 60_000);
        Assert.Equal(2, read.Count);
        Assert.Equal(earlier, read[0].MinuteUnix);
        Assert.Equal(40f, (float)read[0].Avg[0]);
        Assert.Equal(later, read[1].MinuteUnix);
        Assert.Equal(60f, (float)read[1].Avg[0]);
    }

    [Fact]
    public void LayoutChangeKeepsThePreviousFile()
    {
        using var dir = new TempFolder();
        var minute = DateTimeOffset.Parse("2026-06-01T00:00:00Z").ToUnixTimeMilliseconds();
        var one = ChannelTable.Create(new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var two = ChannelTable.Create(
            new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"),
            new ChannelDesc(ChannelKind.Rpm, "Fan", "fan"));

        using (var writer = new HistoryWriter(dir.Path, one))
        {
            writer.Append(Minute(minute, (Half)30));
        }

        using (var writer = new HistoryWriter(dir.Path, two))
        {
            writer.Append(new MinuteRecord(minute + 60_000, 1, [(Half)31, (Half)500], [(Half)31, (Half)500], [(Half)31, (Half)500], [0, 0]));
        }

        Assert.True(File.Exists(Path.Combine(dir.Path, "2026-06.fch")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "2026-06.2.fch")));
        var read = HistoryReader.Read(dir.Path, minute, minute + 120_000);
        Assert.Equal(2, read.Count);
        Assert.Equal(1, read[0].ChannelCount);
        Assert.Equal(2, read[1].ChannelCount);
    }

    [Fact]
    public void HourGapDoesNotInventMinutes()
    {
        using var dir = new TempFolder();
        var table = ChannelTable.Create(new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z").ToUnixTimeMilliseconds();
        using var writer = new HistoryWriter(dir.Path, table);
        writer.Append(Minute(start, (Half)10));
        writer.Append(Minute(start + (3 * 3_600_000), (Half)20));
        var read = HistoryReader.Read(dir.Path, start, start + (4 * 3_600_000));
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public void RetentionDeletesOnlyMonthsOutsideTheWindow()
    {
        using var dir = new TempFolder();
        var table = ChannelTable.Create(new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"));
        var january = DateTimeOffset.Parse("2026-01-15T00:00:00Z").ToUnixTimeMilliseconds();
        var march = DateTimeOffset.Parse("2026-03-15T00:00:00Z").ToUnixTimeMilliseconds();
        using var writer = new HistoryWriter(dir.Path, table, retentionMonths: 1);
        writer.Append(Minute(january, (Half)1));
        writer.Append(Minute(march, (Half)2));
        var removed = writer.ApplyRetention(DateTimeOffset.Parse("2026-03-20T00:00:00Z"));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(dir.Path, "2026-01.fch")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "2026-03.fch")));
        var read = HistoryReader.Read(dir.Path, january, march + 60_000);
        Assert.Single(read);
        Assert.Equal(march, read[0].MinuteUnix);
    }

    private static MinuteRecord Minute(long unixMs, Half value) =>
        new(unixMs, 1, [value], [value], [value], [0]);
}
