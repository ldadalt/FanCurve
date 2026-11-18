using FanCurve.Core;
using LibreHardwareMonitor.Hardware;

namespace FanCurve;

internal sealed record SensorRow(string Id, string Type, string Name, float? Value);

internal sealed class SensorRef
{
    public required IHardware Hardware { get; init; }

    public required ISensor Sensor { get; init; }

    public required bool Slow { get; init; }
}

internal sealed class RuleSlot
{
    private readonly float[] _temperatures;

    public RuleSlot(string name, SensorRef[] temperatures, SensorRef[] fans, RuleRuntime runtime)
    {
        Name = name;
        Temperatures = temperatures;
        Fans = fans;
        Runtime = runtime;
        _temperatures = new float[temperatures.Length];
    }

    public string Name { get; }

    public SensorRef[] Temperatures { get; }

    public SensorRef[] Fans { get; }

    public RuleRuntime Runtime { get; }

    public float LastAggregate { get; set; } = float.NaN;

    public ReadOnlySpan<float> ReadTemperatures()
    {
        for (var i = 0; i < Temperatures.Length; i++)
        {
            _temperatures[i] = Temperatures[i].Sensor.Value ?? float.NaN;
        }

        return _temperatures;
    }
}

internal sealed class ControlBinding
{
    public required RuleSlot[] Rules { get; init; }

    public required SensorRef[] Metrics { get; init; }

    public required SensorRef[] FanSpeeds { get; init; }

    public required IHardware[] FastHardware { get; init; }

    public required IHardware[] SlowHardware { get; init; }

    public required ChannelTable Channels { get; init; }

    public required int CpuChannel { get; init; }

    public required int MemoryChannel { get; init; }

    public float[] MetricHold { get; init; } = [];

    public static ControlBinding Create(
        RuleSlot[] rules,
        SensorRef[] metrics,
        SensorRef[] fanSpeeds,
        IHardware[] fastHardware,
        IHardware[] slowHardware,
        ChannelTable channels,
        int cpuChannel,
        int memoryChannel,
        float[] metricHold)
    {
        return new ControlBinding
        {
            Rules = rules,
            Metrics = metrics,
            FanSpeeds = fanSpeeds,
            FastHardware = fastHardware,
            SlowHardware = slowHardware,
            Channels = channels,
            CpuChannel = cpuChannel,
            MemoryChannel = memoryChannel,
            MetricHold = metricHold,
        };
    }
}

internal sealed class HardwareHub : IDisposable
{
    private readonly Dictionary<string, SensorRef> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<IHardware, IHardware?> _parent = new();
    private Computer? _computer;
    private bool _disposed;

    public void Open(bool storage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_computer is not null)
        {
            return;
        }

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = storage,
        };
        computer.HardwareAdded += OnHardwareAdded;
        computer.Open();
        _computer = computer;
        IndexAll();
        Prime();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public IReadOnlyList<SensorRow> List()
    {
        var rows = new List<SensorRow>(_byId.Count);
        foreach (var pair in _byId)
        {
            rows.Add(new SensorRow(pair.Key, pair.Value.Sensor.SensorType.ToString(), pair.Value.Sensor.Name, pair.Value.Sensor.Value));
        }

        rows.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return rows;
    }

    public bool TryBind(AppConfig config, out ControlBinding? binding, out List<string> errors)
    {
        binding = null;
        errors = [];
        config.Metrics ??= [];
        var rules = new RuleSlot[config.Rules.Count];
        var fast = new HashSet<IHardware>();
        var slow = new HashSet<IHardware>();
        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            var temps = new SensorRef[rule.Sensors.Count];
            for (var s = 0; s < rule.Sensors.Count; s++)
            {
                if (!TryTake(rule.Sensors[s], SensorType.Temperature, errors, out var sensor))
                {
                    return false;
                }

                temps[s] = sensor;
                Mark(sensor.Hardware, sensor.Slow, fast, slow);
            }

            var fans = new SensorRef[rule.Fans.Count];
            for (var f = 0; f < rule.Fans.Count; f++)
            {
                if (!TryTake(rule.Fans[f], SensorType.Control, errors, out var fan))
                {
                    return false;
                }

                if (fan.Sensor.Control is null)
                {
                    errors.Add($"A fan {rule.Fans[f]} não aceita controle por software.");
                    return false;
                }

                fans[f] = fan;
                Mark(fan.Hardware, slowSensor: false, fast, slow);
            }

            rules[i] = new RuleSlot(rule.Name, temps, fans, new RuleRuntime(rule));
        }

        var metrics = new List<SensorRef>();
        foreach (var id in config.Metrics)
        {
            if (!_byId.TryGetValue(id, out var sensor))
            {
                errors.Add($"Métrica não encontrada: {id}.");
                return false;
            }

            metrics.Add(sensor);
            Mark(sensor.Hardware, sensor.Slow, fast, slow);
        }

        var fanSpeeds = new List<SensorRef>();
        if (config.RecordAllFans)
        {
            foreach (var pair in _byId)
            {
                if (pair.Value.Sensor.SensorType != SensorType.Fan)
                {
                    continue;
                }

                fanSpeeds.Add(pair.Value);
                Mark(pair.Value.Hardware, slowSensor: false, fast, slow);
            }

            fanSpeeds.Sort(static (a, b) => string.CompareOrdinal(a.Sensor.Identifier.ToString(), b.Sensor.Identifier.ToString()));
        }

        var descriptions = new List<ChannelDesc>();
        foreach (var rule in rules)
        {
            descriptions.Add(new ChannelDesc(ChannelKind.TemperatureC, rule.Name + " entrada", "rule:" + rule.Name + ":in"));
            descriptions.Add(new ChannelDesc(ChannelKind.Percent, rule.Name + " saída", "rule:" + rule.Name + ":out"));
        }

        foreach (var metric in metrics)
        {
            descriptions.Add(new ChannelDesc(KindOf(metric.Sensor.SensorType), metric.Sensor.Name, metric.Sensor.Identifier.ToString()));
        }

        foreach (var fan in fanSpeeds)
        {
            descriptions.Add(new ChannelDesc(ChannelKind.Rpm, fan.Sensor.Name, fan.Sensor.Identifier.ToString()));
        }

        var cpuChannel = descriptions.Count;
        descriptions.Add(new ChannelDesc(ChannelKind.LoadPercent, "FanCurve CPU", "fancurve:cpu"));
        var memoryChannel = descriptions.Count;
        descriptions.Add(new ChannelDesc(ChannelKind.MemoryGb, "FanCurve memória", "fancurve:mem"));

        var hold = new float[metrics.Count];
        Array.Fill(hold, float.NaN);
        slow.ExceptWith(fast);
        binding = ControlBinding.Create(
            rules,
            metrics.ToArray(),
            fanSpeeds.ToArray(),
            Order(fast),
            Order(slow),
            ChannelTable.Create(descriptions.ToArray()),
            cpuChannel,
            memoryChannel,
            hold);
        return true;
    }

    public void Update(ControlBinding binding, bool includeSlow)
    {
        foreach (var hardware in binding.FastHardware)
        {
            hardware.Update();
            Silence(hardware);
        }

        if (!includeSlow)
        {
            return;
        }

        foreach (var hardware in binding.SlowHardware)
        {
            hardware.Update();
            Silence(hardware);
        }
    }

    public void WriteFans(ControlBinding binding, bool force)
    {
        foreach (var rule in binding.Rules)
        {
            foreach (var fan in rule.Fans)
            {
                Write(fan.Sensor.Control!, rule.Runtime.Output, force);
            }
        }
    }

    public void ReleaseControls(ControlBinding binding)
    {
        foreach (var rule in binding.Rules)
        {
            foreach (var fan in rule.Fans)
            {
                try
                {
                    fan.Sensor.Control?.SetDefault();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"Não devolvi a fan {fan.Sensor.Identifier} para a BIOS: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_computer is not null)
        {
            _computer.HardwareAdded -= OnHardwareAdded;
            _computer.Close();
            _computer = null;
        }
    }

    private bool TryTake(string id, SensorType type, List<string> errors, out SensorRef sensor)
    {
        if (!_byId.TryGetValue(id, out sensor!))
        {
            errors.Add($"Sensor não encontrado: {id}. Use FanCurve list.");
            return false;
        }

        if (sensor.Sensor.SensorType != type)
        {
            errors.Add($"O sensor {id} é {sensor.Sensor.SensorType}, e a regra espera {type}.");
            return false;
        }

        return true;
    }

    private void Mark(IHardware hardware, bool slowSensor, HashSet<IHardware> fast, HashSet<IHardware> slow)
    {
        var cursor = hardware;
        var first = true;
        while (true)
        {
            if (first && slowSensor)
            {
                slow.Add(cursor);
            }
            else
            {
                fast.Add(cursor);
            }

            first = false;
            if (!_parent.TryGetValue(cursor, out var parent) || parent is null)
            {
                return;
            }

            cursor = parent;
        }
    }

    private IHardware[] Order(HashSet<IHardware> wanted)
    {
        var order = new List<IHardware>();
        if (_computer is null)
        {
            return [];
        }

        foreach (var hardware in _computer.Hardware)
        {
            Emit(hardware, wanted, order);
        }

        return order.ToArray();
    }

    private static void Emit(IHardware hardware, HashSet<IHardware> wanted, List<IHardware> order)
    {
        if (wanted.Contains(hardware))
        {
            order.Add(hardware);
        }

        foreach (var sub in hardware.SubHardware)
        {
            Emit(sub, wanted, order);
        }
    }

    private void OnHardwareAdded(IHardware hardware)
    {
        Index(hardware, parent: null, slow: false);
        SilenceTree(hardware);
    }

    private void IndexAll()
    {
        _byId.Clear();
        _parent.Clear();
        if (_computer is null)
        {
            return;
        }

        foreach (var hardware in _computer.Hardware)
        {
            Index(hardware, parent: null, slow: false);
        }
    }

    private void Index(IHardware hardware, IHardware? parent, bool slow)
    {
        _parent[hardware] = parent;
        slow |= hardware.HardwareType == HardwareType.Storage;
        foreach (var sensor in hardware.Sensors)
        {
            _byId[sensor.Identifier.ToString()] = new SensorRef
            {
                Hardware = hardware,
                Sensor = sensor,
                Slow = slow,
            };
        }

        foreach (var sub in hardware.SubHardware)
        {
            Index(sub, hardware, slow);
        }
    }

    private void Prime()
    {
        if (_computer is null)
        {
            return;
        }

        foreach (var hardware in _computer.Hardware)
        {
            Prime(hardware);
        }

        IndexAll();
    }

    private void Prime(IHardware hardware)
    {
        hardware.Update();
        Silence(hardware);
        foreach (var sub in hardware.SubHardware)
        {
            Prime(sub);
        }
    }

    private static void SilenceTree(IHardware hardware)
    {
        Silence(hardware);
        foreach (var sub in hardware.SubHardware)
        {
            SilenceTree(sub);
        }
    }

    private static void Silence(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.ValuesTimeWindow != TimeSpan.Zero)
            {
                sensor.ValuesTimeWindow = TimeSpan.Zero;
            }
        }
    }

    private static void Write(IControl control, float percent, bool force)
    {
        var span = control.MaxSoftwareValue - control.MinSoftwareValue;
        var value = control.MinSoftwareValue + (Math.Clamp(percent, 0f, 100f) / 100f * span);
        if (force && MathF.Abs(control.SoftwareValue - value) < 0.05f)
        {
            // SetSoftware ignora o mesmo número e a LHM então não regrava o chip.
            var nudge = value >= control.MaxSoftwareValue ? value - 0.1f : value + 0.1f;
            control.SetSoftware(nudge);
        }

        control.SetSoftware(value);
    }

    private static ChannelKind KindOf(SensorType type) => type switch
    {
        SensorType.Temperature => ChannelKind.TemperatureC,
        SensorType.Fan => ChannelKind.Rpm,
        SensorType.Load => ChannelKind.LoadPercent,
        SensorType.Control or SensorType.Level or SensorType.Factor => ChannelKind.Percent,
        SensorType.Power => ChannelKind.PowerW,
        SensorType.Data or SensorType.SmallData => ChannelKind.MemoryGb,
        SensorType.Clock => ChannelKind.ClockMhz,
        _ => ChannelKind.Internal,
    };
}
