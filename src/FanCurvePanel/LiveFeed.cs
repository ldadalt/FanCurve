using FanCurve.Core;

namespace FanCurvePanel;

sealed class LiveFeed : IDisposable
{
    public const int Window = 600;

    private RecentRingReader? _reader;
    private ulong _next;
    private readonly List<long> _times = [];
    private List<double>[] _columns = [];

    public IReadOnlyList<ChannelDesc> Channels { get; private set; } = [];

    public RingStatus Status { get; private set; }

    public string? Error { get; private set; }

    public bool HasReader => _reader is not null;

    public IReadOnlyList<long> Times => _times;

    public IReadOnlyList<double> Column(int index) => _columns[index];

    public void Refresh(string directory)
    {
        try
        {
            if (_reader is null)
            {
                var index = Path.Combine(directory, RingFiles.IndexName);
                if (!File.Exists(index))
                {
                    Error = "O FanCurve ainda não gravou amostras nesta pasta.";
                    return;
                }

                _reader = RecentRingReader.Open(directory);
                Reset(_reader);
            }
            else if (_reader.TryReload())
            {
                Reset(_reader);
            }

            var channels = _reader.ChannelCount;
            var batch = 240;
            var times = new long[batch];
            var values = new Half[batch * Math.Max(channels, 1)];
            while (channels > 0)
            {
                var copied = _reader.CopyNew(_next, times, values, out var next);
                if (copied == 0)
                {
                    break;
                }

                for (var i = 0; i < copied; i++)
                {
                    _times.Add(times[i]);
                    for (var c = 0; c < channels; c++)
                    {
                        _columns[c].Add(ToDouble(values[(i * channels) + c]));
                    }
                }

                _next = next;
                if (copied < batch)
                {
                    break;
                }
            }

            var extra = _times.Count - Window;
            if (extra > 0)
            {
                _times.RemoveRange(0, extra);
                foreach (var column in _columns)
                {
                    column.RemoveRange(0, extra);
                }
            }

            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (Window * 1000L);
            var stale = 0;
            while (stale < _times.Count && _times[stale] < cutoff)
            {
                stale++;
            }

            if (stale > 0)
            {
                _times.RemoveRange(0, stale);
                foreach (var column in _columns)
                {
                    column.RemoveRange(0, stale);
                }
            }

            Status = _reader.ReadStatus();
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageFormatException)
        {
            Error = ex.Message;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
    }

    private void Reset(RecentRingReader reader)
    {
        Channels = reader.Channels.ToArray();
        _columns = new List<double>[reader.ChannelCount];
        for (var i = 0; i < _columns.Length; i++)
        {
            _columns[i] = [];
        }

        _times.Clear();
        var status = reader.ReadStatus();
        var back = (ulong)Window;
        _next = status.WriteCount > back ? status.WriteCount - back : 0;
    }

    public static double ToDouble(Half value)
    {
        var number = (float)value;
        return float.IsNaN(number) ? double.NaN : number;
    }
}
