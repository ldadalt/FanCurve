namespace FanCurve.Core;

public sealed class MinuteRecord
{
    public MinuteRecord(
        long minuteUnix,
        ushort sampleCount,
        Half[] min,
        Half[] avg,
        Half[] max,
        ushort[] samplesAt100)
    {
        ArgumentNullException.ThrowIfNull(min);
        ArgumentNullException.ThrowIfNull(avg);
        ArgumentNullException.ThrowIfNull(max);
        ArgumentNullException.ThrowIfNull(samplesAt100);
        if (avg.Length != min.Length || max.Length != min.Length || samplesAt100.Length != min.Length)
        {
            throw new ArgumentException("Os canais do minuto não têm o mesmo tamanho.");
        }

        MinuteUnix = minuteUnix;
        SampleCount = sampleCount;
        Min = min;
        Avg = avg;
        Max = max;
        SamplesAt100 = samplesAt100;
    }

    public long MinuteUnix { get; }

    public ushort SampleCount { get; }

    public Half[] Min { get; }

    public Half[] Avg { get; }

    public Half[] Max { get; }

    public ushort[] SamplesAt100 { get; }

    public int ChannelCount => Min.Length;
}

public sealed class MinuteAggregator
{
    private readonly ChannelTable _channels;
    private readonly float[] _sum;
    private readonly int[] _valid;
    private readonly Half[] _min;
    private readonly Half[] _max;
    private readonly int[] _at100;
    private long _minute;
    private int _samples;
    private bool _open;

    public MinuteAggregator(ChannelTable channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
        _sum = new float[channels.Count];
        _valid = new int[channels.Count];
        _min = new Half[channels.Count];
        _max = new Half[channels.Count];
        _at100 = new int[channels.Count];
    }

    public static long MinuteOf(long unixMs)
    {
        if (unixMs >= 0)
        {
            return unixMs / 60_000 * 60_000;
        }

        var quotient = unixMs / 60_000;
        if (unixMs % 60_000 != 0)
        {
            quotient--;
        }

        return quotient * 60_000;
    }

    /// <summary>
    /// Acumula uma amostra. Quando o minuto UTC muda, devolve o minuto que fechou.
    /// </summary>
    public MinuteRecord? Add(long unixMs, ReadOnlySpan<Half> values)
    {
        RequireValues(values);
        var minute = MinuteOf(unixMs);
        MinuteRecord? closed = null;
        if (!_open)
        {
            Reset(minute);
        }
        else if (minute != _minute)
        {
            closed = Build();
            Reset(minute);
        }

        Accumulate(values);
        return closed;
    }

    public MinuteRecord? Flush()
    {
        if (!_open || _samples == 0)
        {
            _open = false;
            return null;
        }

        var record = Build();
        _open = false;
        _samples = 0;
        return record;
    }

    private void RequireValues(ReadOnlySpan<Half> values)
    {
        if (values.Length != _channels.Count)
        {
            throw new ArgumentException("A amostra não tem o número de canais da tabela.", nameof(values));
        }
    }

    private void Reset(long minute)
    {
        _minute = minute;
        _samples = 0;
        Array.Clear(_sum);
        Array.Clear(_valid);
        Array.Clear(_at100);
        _open = true;
    }

    private void Accumulate(ReadOnlySpan<Half> values)
    {
        if (_samples < ushort.MaxValue)
        {
            _samples++;
        }

        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (Half.IsNaN(value))
            {
                continue;
            }

            var number = (float)value;
            if (_valid[i] == 0)
            {
                _min[i] = value;
                _max[i] = value;
            }
            else
            {
                if (number < (float)_min[i])
                {
                    _min[i] = value;
                }

                if (number > (float)_max[i])
                {
                    _max[i] = value;
                }
            }

            _sum[i] += number;
            _valid[i]++;
            if (_channels.CountsTowardFullScale(i) && number >= 100f)
            {
                _at100[i]++;
            }
        }
    }

    private MinuteRecord Build()
    {
        var count = _channels.Count;
        var min = new Half[count];
        var avg = new Half[count];
        var max = new Half[count];
        var at100 = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            if (_valid[i] == 0)
            {
                min[i] = Half.NaN;
                avg[i] = Half.NaN;
                max[i] = Half.NaN;
            }
            else
            {
                min[i] = _min[i];
                avg[i] = (Half)(_sum[i] / _valid[i]);
                max[i] = _max[i];
            }

            at100[i] = (ushort)Math.Min(_at100[i], ushort.MaxValue);
        }

        return new MinuteRecord(_minute, (ushort)_samples, min, avg, max, at100);
    }
}
