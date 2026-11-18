using FanCurve.Core;

namespace FanCurve.Tests;

public class RecentRingTests
{
    [Fact]
    public void RoundTripKeepsValuesAndResumesWriteCount()
    {
        using var dir = new TempFolder();
        var channels = Channels(5);
        var options = new RecentRingOptions { SlotCount = 32 };
        using (var writer = RecentRingWriter.Open(dir.Path, channels, options))
        {
            var sample = new Half[5];
            for (var i = 0; i < 3; i++)
            {
                sample[0] = (Half)(20 + i);
                sample[4] = Half.NaN;
                writer.Write(1_000 + i, sample);
            }

            writer.SetControlState(0x5, 2, [(Half)80, (Half)40]);
            Assert.Equal(3ul, writer.WriteCount);
        }

        using (var writer = RecentRingWriter.Open(dir.Path, channels, options))
        {
            Assert.Equal(3ul, writer.WriteCount);
            writer.Write(1_003, [(Half)23, default, default, default, (Half)1]);
        }

        using var reader = RecentRingReader.Open(dir.Path);
        var times = new long[8];
        var values = new Half[8 * 5];
        var kept = reader.CopyNew(0, times, values, out var next);
        Assert.Equal(4, kept);
        Assert.Equal(4ul, next);
        Assert.Equal(20f, (float)values[0]);
        Assert.True(Half.IsNaN(values[4]));
        Assert.Equal(1_003, times[3]);
        Assert.Equal(1f, (float)values[(3 * 5) + 4]);

        var rules = new Half[2];
        Assert.Equal(2, reader.ReadRuleOutputs(rules));
        Assert.Equal(80f, (float)rules[0]);
        var status = reader.ReadStatus();
        Assert.Equal(0x5u, status.FailsafeMask);
        Assert.Equal(2u, status.ResumeCount);
        Assert.Equal(4ul, status.WriteCount);

        var info = new FileInfo(Path.Combine(dir.Path, RingFiles.BinName(1)));
        var expected = StorageLayout.RingHeaderSize(5) + (32L * StorageLayout.RingSlotSize(5));
        Assert.Equal(expected, info.Length);
    }

    [Fact]
    public void SecondWriterIsRejectedUntilTheFirstReleases()
    {
        using var dir = new TempFolder();
        var channels = Channels(1);
        var options = new RecentRingOptions { SlotCount = 8 };
        using var first = RecentRingWriter.Open(dir.Path, channels, options);
        var error = Assert.Throws<IOException>(() => RecentRingWriter.Open(dir.Path, channels, options));
        Assert.Contains("gravando", error.Message);
        first.Dispose();
        using var second = RecentRingWriter.Open(dir.Path, channels, options);
        Assert.Equal(0ul, second.WriteCount);
    }

    [Fact]
    public void ReaderReopensWhenTheLayoutChanges()
    {
        using var dir = new TempFolder();
        var options = new RecentRingOptions { SlotCount = 8 };
        var cpu = Channels(1);
        using (var writer = RecentRingWriter.Open(dir.Path, cpu, options))
        {
            writer.Write(10, [(Half)40]);
        }

        using var reader = RecentRingReader.Open(dir.Path);
        Assert.Equal(1, reader.ChannelCount);
        Assert.False(reader.TryReload());

        var both = ChannelTable.Create(
            new ChannelDesc(ChannelKind.TemperatureC, "CPU", "cpu"),
            new ChannelDesc(ChannelKind.Rpm, "Fan", "fan"));
        using (var writer = RecentRingWriter.Open(dir.Path, both, options))
        {
            writer.Write(20, [(Half)41, (Half)900]);
        }

        Assert.True(reader.TryReload());
        Assert.Equal(2, reader.ChannelCount);
        var times = new long[4];
        var values = new Half[8];
        var kept = reader.CopyNew(0, times, values, out _);
        Assert.Equal(1, kept);
        Assert.Equal(20, times[0]);
        Assert.Equal(900f, (float)values[1]);

        using var cleanup = RecentRingWriter.Open(dir.Path, both, options);
        cleanup.Dispose();
        var bins = Directory.GetFiles(dir.Path, "recent-*.bin");
        Assert.Single(bins);
    }

    [Fact]
    public void AcceptedSamplesStayConsistentAcrossWrap()
    {
        using var dir = new TempFolder();
        const int slots = 32;
        var channels = ChannelTable.Create(
            new ChannelDesc(ChannelKind.Internal, "Low", "counter:lo"),
            new ChannelDesc(ChannelKind.Internal, "High", "counter:hi"));
        using var writer = RecentRingWriter.Open(dir.Path, channels, new RecentRingOptions { SlotCount = slots });
        using var reader = RecentRingReader.Open(dir.Path);

        var inconsistencies = 0;
        var accepted = 0;
        long maxSeen = -1;
        Exception? readerError = null;
        var done = 0;
        var readerThread = new Thread(() =>
        {
            try
            {
                var times = new long[slots];
                var values = new Half[slots * 2];
                ulong cursor = 0;
                while (Volatile.Read(ref done) == 0)
                {
                    var count = reader.CopyNew(cursor, times, values, out cursor);
                    if (count == 0)
                    {
                        Thread.Yield();
                    }
                    for (var i = 0; i < count; i++)
                    {
                        var low = BitConverter.HalfToUInt16Bits(values[i * 2]);
                        var high = BitConverter.HalfToUInt16Bits(values[(i * 2) + 1]);
                        var expectedLow = (ushort)times[i];
                        var expectedHigh = (ushort)~expectedLow;
                        if (low != expectedLow || high != expectedHigh)
                        {
                            Interlocked.Increment(ref inconsistencies);
                        }
                        else
                        {
                            Interlocked.Increment(ref accepted);
                            if (times[i] > maxSeen)
                            {
                                maxSeen = times[i];
                            }
                        }
                    }
                }

                var tail = reader.CopyNew(cursor, times, values, out _);
                for (var i = 0; i < tail; i++)
                {
                    var low = BitConverter.HalfToUInt16Bits(values[i * 2]);
                    var high = BitConverter.HalfToUInt16Bits(values[(i * 2) + 1]);
                    if (low != (ushort)times[i] || high != (ushort)~(ushort)times[i])
                    {
                        Interlocked.Increment(ref inconsistencies);
                    }
                    else
                    {
                        Interlocked.Increment(ref accepted);
                    }
                }
            }
            catch (Exception ex)
            {
                readerError = ex;
            }
        });
        readerThread.Start();

        var sample = new Half[2];
        var until = System.Diagnostics.Stopwatch.StartNew();
        long seq = 0;
        while (until.Elapsed < TimeSpan.FromSeconds(10))
        {
            sample[0] = BitConverter.UInt16BitsToHalf((ushort)seq);
            sample[1] = BitConverter.UInt16BitsToHalf((ushort)~(ushort)seq);
            writer.Write(seq, sample);
            seq++;
            if ((seq & 63) == 0)
            {
                Thread.Yield();
            }
        }

        Volatile.Write(ref done, 1);
        Assert.True(readerThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(readerError);
        Assert.Equal(0, inconsistencies);
        Assert.True(accepted > 0, "O leitor não aceitou nenhuma amostra.");
        Assert.True(maxSeen >= slots, "O teste não chegou a dar a volta no anel.");
    }

    [Fact]
    public void DayOfSamplesCopiesInUnderASecondAndKeepsTheHole()
    {
        using var dir = new TempFolder();
        var channels = Channels(2);
        using (var writer = RecentRingWriter.Open(dir.Path, channels, new RecentRingOptions { SlotCount = 86_400 }))
        {
            var sample = new Half[] { (Half)45, (Half)60 };
            var time = 1_700_000_000_000L;
            for (var i = 0; i < 86_400; i++)
            {
                if (i == 10_000)
                {
                    time += 6 * 3_600_000;
                }

                writer.Write(time, sample);
                time += 1000;
            }
        }

        using var reader = RecentRingReader.Open(dir.Path);
        var times = new long[86_400];
        var values = new Half[86_400 * 2];
        var copy = System.Diagnostics.Stopwatch.StartNew();
        var kept = reader.CopyNew(0, times, values, out _);
        copy.Stop();
        Assert.Equal(86_399, kept);
        Assert.Equal(2, Timeline.SplitOnGaps(times.AsSpan(0, kept)).Count);
        Assert.True(copy.ElapsedMilliseconds < 1000, $"cópia levou {copy.ElapsedMilliseconds} ms");
    }

    private static ChannelTable Channels(int count)
    {
        var list = new ChannelDesc[count];
        for (var i = 0; i < count; i++)
        {
            list[i] = new ChannelDesc(ChannelKind.TemperatureC, "C" + i, "src:" + i);
        }

        return ChannelTable.Create(list);
    }
}
