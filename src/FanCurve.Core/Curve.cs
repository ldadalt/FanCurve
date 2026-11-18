namespace FanCurve.Core;

public sealed class CurvePoint
{
    public float TemperatureC { get; set; }

    public float Percent { get; set; }
}

/// <summary>
/// Suavização, histerese, curva, piso e rampa de uma regra. Sem hardware.
/// </summary>
public sealed class RuleRuntime
{
    private readonly float[] _temperatures;
    private readonly float[] _percents;
    private readonly float _floor;
    private readonly float _failsafe;
    private readonly float _smoothingSeconds;
    private readonly float _hysteresisC;
    private readonly float _rampUp;
    private readonly float _rampDown;
    private bool _hasSample;
    private bool _failing;
    private float _smoothed;
    private float _anchor;
    private float _output;

    public RuleRuntime(FanRuleConfig rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Curve is null || rule.Curve.Count < 2)
        {
            throw new ArgumentException("A curva precisa de pelo menos dois pontos.", nameof(rule));
        }

        _temperatures = new float[rule.Curve.Count];
        _percents = new float[rule.Curve.Count];
        for (var i = 0; i < rule.Curve.Count; i++)
        {
            if (i > 0 && rule.Curve[i].TemperatureC <= rule.Curve[i - 1].TemperatureC)
            {
                throw new ArgumentException("As temperaturas da curva precisam ser crescentes.", nameof(rule));
            }

            _temperatures[i] = rule.Curve[i].TemperatureC;
            _percents[i] = rule.Curve[i].Percent;
        }

        _floor = rule.FloorPercent;
        _failsafe = rule.FailsafePercent;
        _smoothingSeconds = rule.SmoothingSeconds;
        _hysteresisC = rule.HysteresisC;
        _rampUp = rule.RampUpPercentPerSec;
        _rampDown = rule.RampDownPercentPerSec;
    }

    public float Output => _output;

    public bool IsFailing => _failing;

    public void Reset()
    {
        _hasSample = false;
        _failing = false;
    }

    public void Seed(float output, float temperature)
    {
        _output = Math.Clamp(output, 0f, 100f);
        _smoothed = temperature;
        _anchor = temperature;
        _hasSample = !float.IsNaN(temperature);
        _failing = false;
    }

    /// <summary>
    /// <paramref name="force"/> zera a rampa (volta de suspensão). Sensor ausente vai direto ao fail-safe.
    /// Vários sensores viram a temperatura máxima.
    /// </summary>
    public float Step(ReadOnlySpan<float> temperatures, float dtSeconds, bool force)
    {
        if (!Aggregate(temperatures, out var raw))
        {
            _failing = true;
            _hasSample = false;
            _output = ClampPercent(_failsafe);
            return _output;
        }

        var recovered = _failing;
        _failing = false;
        if (!_hasSample || force || recovered)
        {
            _smoothed = raw;
            _anchor = raw;
            _output = Target(raw);
            _hasSample = true;
            return _output;
        }

        if (_smoothingSeconds <= 0f || dtSeconds <= 0f)
        {
            _smoothed = raw;
        }
        else
        {
            var alpha = 1f - MathF.Exp(-dtSeconds / _smoothingSeconds);
            _smoothed += (raw - _smoothed) * alpha;
        }

        if (_hysteresisC <= 0f || MathF.Abs(_smoothed - _anchor) >= _hysteresisC)
        {
            _anchor = _smoothed;
        }

        var target = Target(_anchor);
        if (dtSeconds <= 0f)
        {
            _output = target;
            return _output;
        }

        var delta = target - _output;
        var rate = delta >= 0f ? _rampUp : _rampDown;
        if (rate <= 0f)
        {
            _output = target;
            return _output;
        }

        var limit = rate * dtSeconds;
        _output = MathF.Abs(delta) <= limit ? target : _output + MathF.CopySign(limit, delta);
        return _output;
    }

    private float Target(float temperature) => ClampPercent(MathF.Max(Evaluate(temperature), _floor));

    private float Evaluate(float temperature)
    {
        if (temperature <= _temperatures[0])
        {
            return _percents[0];
        }

        var last = _temperatures.Length - 1;
        if (temperature >= _temperatures[last])
        {
            return _percents[last];
        }

        for (var i = 1; i <= last; i++)
        {
            if (temperature > _temperatures[i])
            {
                continue;
            }

            var span = _temperatures[i] - _temperatures[i - 1];
            var t = span <= 0f ? 0f : (temperature - _temperatures[i - 1]) / span;
            return _percents[i - 1] + ((_percents[i] - _percents[i - 1]) * t);
        }

        return _percents[last];
    }

    private static bool Aggregate(ReadOnlySpan<float> temperatures, out float max)
    {
        max = float.NegativeInfinity;
        var any = false;
        foreach (var temperature in temperatures)
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

        return any;
    }

    private static float ClampPercent(float value) => Math.Clamp(value, 0f, 100f);
}
