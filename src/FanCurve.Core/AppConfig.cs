using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanCurve.Core;

public sealed class FanRuleConfig
{
    public string Name { get; set; } = "";

    public List<string> Sensors { get; set; } = [];

    public List<string> Fans { get; set; } = [];

    public List<CurvePoint> Curve { get; set; } = [];

    public float FloorPercent { get; set; } = 30;

    public float SmoothingSeconds { get; set; } = 3;

    public float HysteresisC { get; set; } = 1.5f;

    public float RampUpPercentPerSec { get; set; } = 20;

    public float RampDownPercentPerSec { get; set; } = 5;

    public float FailsafePercent { get; set; } = 100;
}

public sealed class StorageConfig
{
    public bool Enabled { get; set; } = true;

    public int RecentHours { get; set; } = 24;

    public int RetentionMonths { get; set; } = 12;

    public int SlowSensorsEverySec { get; set; } = 30;
}

public sealed class AppConfig
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public int ResumeGapSeconds { get; set; } = 15;

    public int ReassertSeconds { get; set; } = 30;

    public List<FanRuleConfig> Rules { get; set; } = [];

    public StorageConfig Storage { get; set; } = new();

    public List<string> Metrics { get; set; } = [];

    public bool RecordAllFans { get; set; } = true;

    public bool EcoMode { get; set; }

    public static AppConfig Parse(ReadOnlySpan<byte> utf8)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(utf8, JsonOptions);
        return config ?? throw new JsonException("Configuração vazia.");
    }

    public static AppConfig Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static ulong Hash(ReadOnlySpan<byte> utf8)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in utf8)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}

public static class ConfigValidator
{
    public static List<string> Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();
        if (config.Rules is null || config.Rules.Count == 0)
        {
            errors.Add("É preciso ao menos uma regra.");
            return errors;
        }

        if (config.Rules.Count > FanCurveLimits.MaxRules)
        {
            errors.Add($"No máximo {FanCurveLimits.MaxRules} regras.");
        }

        if (config.ResumeGapSeconds is < 1 or > 3600)
        {
            errors.Add("resumeGapSeconds fica entre 1 e 3600.");
        }

        if (config.ReassertSeconds is < 1 or > 3600)
        {
            errors.Add("reassertSeconds fica entre 1 e 3600.");
        }

        config.Metrics ??= [];
        config.Storage ??= new StorageConfig();
        if (config.Storage.RecentHours is < 1 or > FanCurveLimits.MaxRecentHours)
        {
            errors.Add($"recentHours fica entre 1 e {FanCurveLimits.MaxRecentHours}.");
        }

        if (config.Storage.RetentionMonths is < 1 or > 120)
        {
            errors.Add("retentionMonths fica entre 1 e 120.");
        }

        if (config.Storage.SlowSensorsEverySec is < 1 or > 3600)
        {
            errors.Add("slowSensorsEverySec fica entre 1 e 3600.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var fans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in config.Rules)
        {
            if (rule is null)
            {
                errors.Add("Regra nula.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Name) || !IsName(rule.Name))
            {
                errors.Add($"Nome de regra inválido: '{rule.Name}'. Use letras, números, _ e -.");
            }
            else if (!names.Add(rule.Name))
            {
                errors.Add($"Nome de regra repetido: {rule.Name}.");
            }

            if (rule.Sensors is null || rule.Sensors.Count == 0)
            {
                errors.Add($"A regra {rule.Name} não tem sensor de temperatura.");
            }
            else
            {
                foreach (var sensor in rule.Sensors)
                {
                    if (!IsId(sensor))
                    {
                        errors.Add($"Sensor inválido na regra {rule.Name}: {sensor}.");
                    }
                }
            }

            if (rule.Fans is null || rule.Fans.Count == 0)
            {
                errors.Add($"A regra {rule.Name} não tem fan.");
            }
            else
            {
                foreach (var fan in rule.Fans)
                {
                    if (!IsId(fan))
                    {
                        errors.Add($"Fan inválida na regra {rule.Name}: {fan}.");
                    }
                    else if (!fans.Add(fan))
                    {
                        errors.Add($"A fan {fan} está em mais de uma regra.");
                    }
                }
            }

            if (rule.Curve is null || rule.Curve.Count < 2)
            {
                errors.Add($"A curva da regra {rule.Name} precisa de pelo menos dois pontos.");
            }
            else
            {
                for (var i = 0; i < rule.Curve.Count; i++)
                {
                    var point = rule.Curve[i];
                    if (point is null || !float.IsFinite(point.TemperatureC) || !IsPercent(point.Percent))
                    {
                        errors.Add($"Ponto inválido na curva da regra {rule.Name}.");
                        break;
                    }

                    if (i > 0 && point.TemperatureC <= rule.Curve[i - 1].TemperatureC)
                    {
                        errors.Add($"Temperaturas da regra {rule.Name} precisam ser crescentes.");
                        break;
                    }
                }
            }

            if (!IsPercent(rule.FloorPercent) || !IsPercent(rule.FailsafePercent))
            {
                errors.Add($"Piso ou fail-safe da regra {rule.Name} está fora de 0 a 100.");
            }

            if (rule.SmoothingSeconds < 0 || rule.HysteresisC < 0 || rule.RampUpPercentPerSec < 0 || rule.RampDownPercentPerSec < 0)
            {
                errors.Add($"Tempos da regra {rule.Name} não podem ser negativos.");
            }
        }

        if (config.Metrics is not null)
        {
            foreach (var metric in config.Metrics)
            {
                if (!IsId(metric))
                {
                    errors.Add($"Métrica inválida: {metric}.");
                }
            }
        }

        return errors;
    }

    public static int EstimatedChannels(AppConfig config, int fanSensors = 0)
    {
        var rules = config.Rules?.Count ?? 0;
        var metrics = config.Metrics?.Count ?? 0;
        var fans = config.RecordAllFans ? fanSensors : 0;
        return (rules * 2) + metrics + fans + 2;
    }

    private static bool IsPercent(float value) => float.IsFinite(value) && value is >= 0f and <= 100f;

    private static bool IsId(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/');

    private static bool IsName(string value)
    {
        foreach (var ch in value)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
