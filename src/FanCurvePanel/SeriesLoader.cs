using FanCurve.Core;

namespace FanCurvePanel;

sealed class LoadedSeries
{
    public required ChannelDesc Channel { get; init; }

    public required long[] Times { get; init; }

    public required double[] Values { get; init; }

    public double[]? Min { get; init; }

    public double[]? Max { get; init; }

    public int[]? SamplesAt100 { get; init; }

    public bool MinuteResolution { get; init; }
}

static class SeriesLoader
{
    public const long DayMs = 24L * 60 * 60 * 1000;

    public static List<ChannelDesc> KnownChannels(string dataDirectory, string historyDirectory)
    {
        var byId = new Dictionary<string, ChannelDesc>(StringComparer.Ordinal);
        var index = Path.Combine(dataDirectory, RingFiles.IndexName);
        if (File.Exists(index))
        {
            using var reader = RecentRingReader.Open(dataDirectory);
            foreach (var channel in reader.Channels)
            {
                byId[Key(channel)] = channel;
            }
        }

        foreach (var table in HistoryReader.ListChannelTables(historyDirectory))
        {
            foreach (var channel in table.Channels)
            {
                byId.TryAdd(Key(channel), channel);
            }
        }

        return byId.Values.ToList();
    }

    public static List<LoadedSeries> Load(
        string dataDirectory,
        string historyDirectory,
        long fromUnixMs,
        long toUnixMs,
        IReadOnlyList<ChannelDesc> selected)
    {
        if (selected.Count == 0 || toUnixMs <= fromUnixMs)
        {
            return [];
        }

        return toUnixMs - fromUnixMs > DayMs
            ? LoadMinutes(historyDirectory, fromUnixMs, toUnixMs, selected)
            : LoadRing(dataDirectory, fromUnixMs, toUnixMs, selected);
    }

    private static List<LoadedSeries> LoadRing(
        string dataDirectory,
        long fromUnixMs,
        long toUnixMs,
        IReadOnlyList<ChannelDesc> selected)
    {
        if (!File.Exists(Path.Combine(dataDirectory, RingFiles.IndexName)))
        {
            return [];
        }

        using var reader = RecentRingReader.Open(dataDirectory);
        var channels = reader.Channels.ToArray();
        var indexes = new int[selected.Count];
        for (var i = 0; i < selected.Count; i++)
        {
            indexes[i] = IndexOf(channels, selected[i]);
        }

        var status = reader.ReadStatus();
        var span = (int)Math.Clamp((toUnixMs - fromUnixMs) / 1000 + 30, 1, Math.Max(1, reader.SlotCount));
        var fromIndex = status.WriteCount > (ulong)span ? status.WriteCount - (ulong)span : 0;
        var times = new List<long>();
        var columns = new List<double>[selected.Count];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = [];
        }

        var batch = 512;
        var count = reader.ChannelCount;
        var batchTimes = new long[batch];
        var batchValues = new Half[batch * Math.Max(count, 1)];
        var cursor = fromIndex;
        while (count > 0)
        {
            var copied = reader.CopyNew(cursor, batchTimes, batchValues, out var next);
            if (copied == 0)
            {
                break;
            }

            for (var i = 0; i < copied; i++)
            {
                var time = batchTimes[i];
                if (time < fromUnixMs || time >= toUnixMs)
                {
                    continue;
                }

                times.Add(time);
                for (var c = 0; c < selected.Count; c++)
                {
                    var index = indexes[c];
                    columns[c].Add(index < 0 ? double.NaN : LiveFeed.ToDouble(batchValues[(i * count) + index]));
                }
            }

            cursor = next;
            if (copied < batch)
            {
                break;
            }
        }

        var result = new List<LoadedSeries>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            result.Add(new LoadedSeries
            {
                Channel = selected[i],
                Times = times.ToArray(),
                Values = columns[i].ToArray(),
            });
        }

        return result;
    }

    private static List<LoadedSeries> LoadMinutes(
        string historyDirectory,
        long fromUnixMs,
        long toUnixMs,
        IReadOnlyList<ChannelDesc> selected)
    {
        var fromMinute = MinuteAggregator.MinuteOf(fromUnixMs);
        var toMinute = MinuteAggregator.MinuteOf(toUnixMs) + 60_000;
        var slices = HistoryReader.ReadSlices(historyDirectory, fromMinute, toMinute);
        var buckets = new SortedDictionary<long, double>[selected.Count];
        var lows = new SortedDictionary<long, double>[selected.Count];
        var highs = new SortedDictionary<long, double>[selected.Count];
        var full = new SortedDictionary<long, int>[selected.Count];
        for (var i = 0; i < selected.Count; i++)
        {
            buckets[i] = [];
            lows[i] = [];
            highs[i] = [];
            full[i] = [];
        }

        foreach (var slice in slices)
        {
            var table = slice.Channels.Channels;
            for (var s = 0; s < selected.Count; s++)
            {
                var index = IndexOf(table, selected[s]);
                if (index < 0 || slice.Record.MinuteUnix < fromMinute || slice.Record.MinuteUnix >= toMinute)
                {
                    continue;
                }

                var minute = slice.Record.MinuteUnix;
                buckets[s][minute] = LiveFeed.ToDouble(slice.Record.Avg[index]);
                lows[s][minute] = LiveFeed.ToDouble(slice.Record.Min[index]);
                highs[s][minute] = LiveFeed.ToDouble(slice.Record.Max[index]);
                full[s][minute] = slice.Record.SamplesAt100[index];
            }
        }

        var result = new List<LoadedSeries>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var times = buckets[i].Keys.ToArray();
            result.Add(new LoadedSeries
            {
                Channel = selected[i],
                Times = times,
                Values = buckets[i].Values.ToArray(),
                Min = lows[i].Values.ToArray(),
                Max = highs[i].Values.ToArray(),
                SamplesAt100 = full[i].Values.ToArray(),
                MinuteResolution = true,
            });
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<ChannelDesc> channels, ChannelDesc wanted)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            if (Same(channels[i], wanted))
            {
                return i;
            }
        }

        return -1;
    }

    public static bool Same(ChannelDesc left, ChannelDesc right) =>
        string.Equals(Key(left), Key(right), StringComparison.Ordinal);

    public static string Key(ChannelDesc channel) =>
        string.IsNullOrEmpty(channel.SourceId) ? channel.Kind + ":" + channel.Name : channel.SourceId;
}
