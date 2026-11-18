using FanCurve.Core;

namespace FanCurve.Tests;

public class LayoutTests
{
    [Fact]
    public void SlotSizeIsMultipleOfEightForEveryChannelCount()
    {
        for (var channels = 1; channels <= 64; channels++)
        {
            var slot = StorageLayout.RingSlotSize(channels);
            Assert.Equal(0, slot % 8);
            Assert.True(slot >= 8 + (2 * channels));
        }

        Assert.Equal(56, StorageLayout.RingSlotSize(24));
        Assert.Equal(64, StorageLayout.RingSlotSize(25));
        Assert.Equal(208, StorageLayout.HistoryRecordSize(24));
    }

    [Fact]
    public void SharedHeaderFieldsAreAligned()
    {
        Assert.Equal(0, RecentFormat.WriteCount % 8);
        Assert.Equal(0, RecentFormat.LastTick % 8);
        Assert.Equal(0, RecentFormat.LayoutHash % 8);
        Assert.Equal(RecentFormat.ChannelTable, RecentFormat.RuleOutput + (FanCurveLimits.MaxRules * 2));
        Assert.Equal(0, StorageLayout.RingHeaderSize(1) % 4096);
        Assert.Equal(0, StorageLayout.RingHeaderSize(24) % 4096);
    }

    [Fact]
    public void Utf8NameStopsOnACharacterBoundary()
    {
        var kept = new string('a', 46) + "á";
        var cut = new string('b', 47) + "á";
        var table = ChannelTable.Create(
            new ChannelDesc(ChannelKind.TemperatureC, kept, "cpu"),
            new ChannelDesc(ChannelKind.Rpm, cut, "fan"));
        var bytes = new byte[table.Count * ChannelTable.EntryBytes];
        table.Write(bytes);
        var roundTrip = ChannelTable.Read(bytes, table.Count);

        Assert.Equal(kept, roundTrip.Channels[0].Name);
        Assert.Equal(new string('b', 47), roundTrip.Channels[1].Name);
        Assert.Equal(table.LayoutHash, roundTrip.LayoutHash);
    }

    [Fact]
    public void PrivateBytesArePositive()
    {
        Assert.True(PrivateBytes.Get() > 0);
    }
}
