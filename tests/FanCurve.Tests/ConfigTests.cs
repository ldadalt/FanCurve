using System.Text;
using FanCurve.Core;

namespace FanCurve.Tests;

public class ConfigTests
{
    [Fact]
    public void CommentsAndAValidRulePass()
    {
        var json = """
            {
              // molde
              "rules": [
                {
                  "name": "cpu",
                  "sensors": [ "/intelcpu/0/temperature/0" ],
                  "fans": [ "/lpc/nct6798d/control/0" ],
                  "curve": [
                    { "temperatureC": 40, "percent": 30 },
                    { "temperatureC": 80, "percent": 100 }
                  ]
                }
              ],
            }
            """;
        var config = AppConfig.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Empty(ConfigValidator.Validate(config));
        Assert.Equal(30f, config.Rules[0].FloorPercent);
        Assert.Equal(15, config.ResumeGapSeconds);
    }

    [Fact]
    public void SeventeenRulesAndASharedFanAreRejected()
    {
        var rules = new List<FanRuleConfig>();
        for (var i = 0; i < 17; i++)
        {
            rules.Add(Rule("r" + i, "/lpc/it8689e/control/0"));
        }

        var errors = ConfigValidator.Validate(new AppConfig { Rules = rules });
        Assert.Contains(errors, error => error.Contains("16", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("mais de uma regra", StringComparison.Ordinal));
    }

    [Fact]
    public void CurveTemperaturesMustIncrease()
    {
        var rule = Rule("cpu", "/lpc/it8689e/control/0");
        rule.Curve =
        [
            new CurvePoint { TemperatureC = 80, Percent = 100 },
            new CurvePoint { TemperatureC = 40, Percent = 30 },
        ];
        var errors = ConfigValidator.Validate(new AppConfig { Rules = [rule] });
        Assert.Contains(errors, error => error.Contains("crescentes", StringComparison.Ordinal));
    }

    [Fact]
    public void AtomicWriteRoundTripsTheCurve()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "fancurve.json");
        var config = new AppConfig { Rules = [Rule("gpu", "/gpu-nvidia/0/control/0")] };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config, AppConfig.JsonOptions);
        ConfigFiles.WriteAtomic(path, bytes);
        Assert.False(File.Exists(path + ".tmp"));
        var loaded = AppConfig.Load(path);
        Assert.Empty(ConfigValidator.Validate(loaded));
        Assert.Equal(40f, loaded.Rules[0].Curve[0].TemperatureC);
    }

    private static FanRuleConfig Rule(string name, string fan) => new()
    {
        Name = name,
        Sensors = ["/intelcpu/0/temperature/0"],
        Fans = [fan],
        Curve =
        [
            new CurvePoint { TemperatureC = 40, Percent = 30 },
            new CurvePoint { TemperatureC = 80, Percent = 100 },
        ],
    };
}
