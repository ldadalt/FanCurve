using System.Diagnostics;
using FanCurve.Core;

namespace FanCurve;

public sealed class FanService : IDisposable
{
    private readonly string _configPath;
    private readonly string _dataDirectory;
    private readonly EventLogWriter _events;
    private readonly HardwareHub _hub;
    private readonly SingleWriterLock _lock;
    private AppConfig _config;
    private ControlBinding _binding;
    private DateTimeOffset _lastWall;
    private DateTimeOffset _lastConfigPoll;
    private DateTimeOffset _lastSlow;
    private DateTimeOffset _lastSelf = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRetentionDay;
    private long _configStamp;
    private ulong _configHash;
    private uint _resumeCount;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private float _cpuPercent = float.NaN;
    private float _memoryGb = float.NaN;
    private bool _selfReady;
    private RecentRingWriter? _ring;
    private HistoryWriter? _history;
    private MinuteAggregator? _aggregator;
    private readonly Stopwatch _pace = new();
    private Half[] _outputs = [];
    private Half[] _sample = [];
    private bool _disposed;

    private FanService(
        string configPath,
        string dataDirectory,
        EventLogWriter events,
        HardwareHub hub,
        SingleWriterLock singleWriter,
        AppConfig config,
        ControlBinding binding,
        long configStamp,
        ulong configHash)
    {
        _configPath = configPath;
        _dataDirectory = dataDirectory;
        _events = events;
        _hub = hub;
        _lock = singleWriter;
        _config = config;
        _binding = binding;
        _configStamp = configStamp;
        _configHash = configHash;
    }

    public static FanService Start(string configPath, string dataDirectory, string logDirectory)
    {
        var bytes = ReadConfig(configPath);
        var config = AppConfig.Parse(bytes);
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new ConfigRejectedException(errors);
        }

        Directory.CreateDirectory(dataDirectory);
        var singleWriter = SingleWriterLock.Acquire(dataDirectory);
        var events = new EventLogWriter(logDirectory);
        var hub = new HardwareHub();
        try
        {
            var storage = (config.Metrics ?? []).Any(static id =>
                id.Contains("/nvme/", StringComparison.Ordinal)
                || id.Contains("/hdd/", StringComparison.Ordinal)
                || id.Contains("/storage/", StringComparison.Ordinal));
            hub.Open(storage);
            if (!hub.TryBind(config, out var binding, out var bindErrors) || binding is null)
            {
                throw new ConfigRejectedException(bindErrors);
            }

            if (config.EcoMode && !EcoQos.TryEnable())
            {
                events.Write("EcoQoS não ligou");
            }

            var service = new FanService(configPath, dataDirectory, events, hub, singleWriter, config, binding, Stamp(configPath), AppConfig.Hash(bytes));
            service.OpenStorage();
            events.Write("início");
            return service;
        }
        catch
        {
            hub.Dispose();
            events.Dispose();
            singleWriter.Dispose();
            throw;
        }
    }

    public void Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = DateTimeOffset.UtcNow;
        var gap = _lastWall == default ? 0 : (now - _lastWall).TotalSeconds;
        var resume = _lastWall != default && gap > _config.ResumeGapSeconds;
        float dt;
        if (resume || !_pace.IsRunning)
        {
            dt = 1f;
        }
        else
        {
            dt = Math.Clamp((float)_pace.Elapsed.TotalSeconds, 0.1f, _config.ResumeGapSeconds);
        }

        _pace.Restart();
        if (resume)
        {
            _resumeCount++;
            foreach (var rule in _binding.Rules)
            {
                rule.Runtime.Reset();
            }

            _events.Write($"suspensão, salto de {gap:0} s, retomada {_resumeCount}");
        }

        _lastWall = now;
        if ((now - _lastConfigPoll).TotalSeconds >= 5)
        {
            _lastConfigPoll = now;
            TryReload();
        }

        var slowDue = _lastSlow == default || (now - _lastSlow).TotalSeconds >= _config.Storage.SlowSensorsEverySec;
        try
        {
            _hub.Update(_binding, slowDue);
            if (slowDue)
            {
                _lastSlow = now;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _events.Write("falha ao ler sensores: " + ex.Message);
        }

        var forceWrite = resume || _config.ReassertSeconds <= 1 || SecondsSinceReassert(now) >= _config.ReassertSeconds;
        var mask = 0u;
        for (var i = 0; i < _binding.Rules.Length; i++)
        {
            var slot = _binding.Rules[i];
            var wasFailing = slot.Runtime.IsFailing;
            slot.Runtime.Step(slot.ReadTemperatures(), dt, resume);
            slot.LastAggregate = Aggregate(slot);
            if (slot.Runtime.IsFailing)
            {
                mask |= 1u << i;
                if (!wasFailing)
                {
                    _events.Write($"fail-safe {slot.Name}");
                }
            }
            else if (wasFailing)
            {
                _events.Write($"sensor de {slot.Name} voltou");
            }
        }

        _hub.WriteFans(_binding, forceWrite);
        if (forceWrite)
        {
            _reassertMark = now;
        }

        Record(now, mask);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Step();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (StopRequested())
                {
                    _events.Write("parada pedida");
                    break;
                }

                Step();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool StopRequested()
    {
        var path = Path.Combine(_dataDirectory, "stop.request");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _hub.ReleaseControls(_binding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _events.Write("falha ao devolver as fans: " + ex.Message);
        }

        _events.Write("fim");
        CloseStorage();
        _hub.Dispose();
        _events.Dispose();
        _lock.Dispose();
    }

    private DateTimeOffset _reassertMark;

    private double SecondsSinceReassert(DateTimeOffset now) =>
        _reassertMark == default ? double.PositiveInfinity : (now - _reassertMark).TotalSeconds;

    private void TryReload()
    {
        long stamp;
        try
        {
            stamp = Stamp(_configPath);
        }
        catch (IOException)
        {
            return;
        }

        if (stamp == _configStamp)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = ReadConfig(_configPath);
        }
        catch (IOException ex)
        {
            _events.Write("config ainda sendo gravada: " + ex.Message);
            return;
        }

        AppConfig config;
        try
        {
            config = AppConfig.Parse(bytes);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            _events.Write("config rejeitada: " + ex.Message);
            _configStamp = stamp;
            return;
        }

        var errors = ConfigValidator.Validate(config);
        List<string> bindErrors = [];
        ControlBinding? binding = null;
        if (errors.Count == 0)
        {
            _hub.TryBind(config, out binding, out bindErrors);
        }

        if (errors.Count > 0 || binding is null)
        {
            _events.Write("config rejeitada: " + string.Join("; ", errors.Concat(bindErrors)));
            _configStamp = stamp;
            return;
        }

        Remember(binding);
        var layoutChanged = binding.Channels.LayoutHash != _binding.Channels.LayoutHash;
        _binding = binding;
        _config = config;
        _configStamp = stamp;
        _configHash = AppConfig.Hash(bytes);
        var wantStorage = config.Storage.Enabled;
        if (!wantStorage)
        {
            CloseStorage();
        }
        else if (layoutChanged || _ring is null)
        {
            CloseStorage();
            OpenStorage();
        }

        _events.Write("config recarregada");
    }

    private void Remember(ControlBinding next)
    {
        foreach (var rule in next.Rules)
        {
            foreach (var previous in _binding.Rules)
            {
                if (!string.Equals(previous.Name, rule.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                rule.Runtime.Seed(previous.Runtime.Output, previous.LastAggregate);
                break;
            }
        }
    }

    private void Record(DateTimeOffset now, uint failsafeMask)
    {
        if (_aggregator is null || _sample.Length != _binding.Channels.Count)
        {
            return;
        }

        var rules = _binding.Rules.Length;
        if (_outputs.Length != rules)
        {
            _outputs = new Half[rules];
        }
        for (var i = 0; i < rules; i++)
        {
            var temperature = _binding.Rules[i].LastAggregate;
            _sample[i] = float.IsNaN(temperature) ? Half.NaN : (Half)temperature;
            _sample[rules + i] = (Half)_binding.Rules[i].Runtime.Output;
        }

        var index = rules * 2;
        var readSlow = _lastSlow != default && (now - _lastSlow).TotalSeconds < 1.5;
        for (var i = 0; i < _binding.Metrics.Length; i++)
        {
            if (!_binding.Metrics[i].Slow || readSlow || float.IsNaN(_binding.MetricHold[i]))
            {
                _binding.MetricHold[i] = _binding.Metrics[i].Sensor.Value ?? float.NaN;
            }

            _sample[index++] = float.IsNaN(_binding.MetricHold[i]) ? Half.NaN : (Half)_binding.MetricHold[i];
        }

        foreach (var fan in _binding.FanSpeeds)
        {
            var rpm = fan.Sensor.Value;
            _sample[index++] = rpm is null ? Half.NaN : (Half)rpm.Value;
        }

        UpdateSelf(now);
        _sample[_binding.CpuChannel] = float.IsNaN(_cpuPercent) ? Half.NaN : (Half)_cpuPercent;
        _sample[_binding.MemoryChannel] = float.IsNaN(_memoryGb) ? Half.NaN : (Half)_memoryGb;

        var unixMs = now.ToUnixTimeMilliseconds();
        var closed = _aggregator.Add(unixMs, _sample);
        if (closed is not null)
        {
            _history?.Append(closed);
        }

        _ring?.Write(unixMs, _sample);
        for (var i = 0; i < rules; i++)
        {
            _outputs[i] = (Half)_binding.Rules[i].Runtime.Output;
        }

        _ring?.SetControlState(failsafeMask, _resumeCount, _outputs);
        ApplyRetention(now);
    }

    private void UpdateSelf(DateTimeOffset now)
    {
        _memoryGb = PrivateBytes.Get() / (1024f * 1024f * 1024f);
        var usage = Environment.CpuUsage.TotalTime;
        if (!_selfReady)
        {
            _lastCpu = usage;
            _lastSelf = now;
            _selfReady = true;
            return;
        }

        if ((now - _lastSelf).TotalSeconds < 60)
        {
            return;
        }

        var cpuSeconds = (usage - _lastCpu).TotalSeconds;
        var wall = (now - _lastSelf).TotalSeconds;
        _cpuPercent = wall <= 0 ? 0 : (float)(cpuSeconds / wall * 100.0);
        _lastCpu = usage;
        _lastSelf = now;
    }

    private void OpenStorage()
    {
        if (!_config.Storage.Enabled)
        {
            return;
        }

        var options = RingOptions();
        _ring = RecentRingWriter.Open(_dataDirectory, _binding.Channels, options, ownLock: false);
        _history = new HistoryWriter(Path.Combine(_dataDirectory, "hist"), _binding.Channels, _config.Storage.RetentionMonths);
        _aggregator = new MinuteAggregator(_binding.Channels);
        _sample = new Half[_binding.Channels.Count];
        _lastRetentionDay = DateTimeOffset.UtcNow.Date;
        _history.ApplyRetention(DateTimeOffset.UtcNow);
    }

    private RecentRingOptions RingOptions() => new()
    {
        SlotCount = _config.Storage.RecentHours * 3600,
        IntervalMs = 1000,
        ConfigHash = _configHash,
    };

    private void CloseStorage()
    {
        if (_aggregator is not null)
        {
            var tail = _aggregator.Flush();
            if (tail is not null)
            {
                _history?.Append(tail);
            }
        }

        _history?.Dispose();
        _ring?.Dispose();
        _history = null;
        _ring = null;
        _aggregator = null;
    }

    private void ApplyRetention(DateTimeOffset now)
    {
        if (_history is null || now.Date == _lastRetentionDay)
        {
            return;
        }

        _lastRetentionDay = now.Date;
        _history.ApplyRetention(now);
    }

    private static float Aggregate(RuleSlot slot)
    {
        var max = float.NegativeInfinity;
        var any = false;
        foreach (var temperature in slot.ReadTemperatures())
        {
            if (float.IsNaN(temperature))
            {
                continue;
            }

            any = true;
            if (temperature > max)
            {
                max = temperature;
            }
        }

        return any ? max : float.NaN;
    }

    private static byte[] ReadConfig(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }

        throw new IOException("Não consegui ler a configuração.");
    }

    private static long Stamp(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.LastWriteTimeUtc.Ticks ^ info.Length;
    }
}

public sealed class ConfigRejectedException : Exception
{
    public ConfigRejectedException(IReadOnlyList<string> errors) : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
