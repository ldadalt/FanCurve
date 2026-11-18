namespace FanCurve.Core;

public readonly record struct SeriesSegment(int Offset, int Count);

public static class Timeline
{
    public static List<SeriesSegment> SplitOnGaps(ReadOnlySpan<long> unixMs, long gapLimitMs = FanCurveLimits.GapLimitMs)
    {
        var segments = new List<SeriesSegment>();
        if (unixMs.IsEmpty)
        {
            return segments;
        }

        if (gapLimitMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gapLimitMs));
        }

        var start = 0;
        for (var i = 1; i < unixMs.Length; i++)
        {
            var step = unixMs[i] - unixMs[i - 1];
            if (step > gapLimitMs || unixMs[i] < unixMs[i - 1])
            {
                segments.Add(new SeriesSegment(start, i - start));
                start = i;
            }
        }

        segments.Add(new SeriesSegment(start, unixMs.Length - start));
        return segments;
    }
}
