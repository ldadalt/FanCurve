using FanCurve.Core;

namespace FanCurve.Tests;

public class RuleRuntimeTests
{
    [Fact]
    public void RampLimitsRiseAndFallAfterTheFirstSample()
    {
        var runtime = Runtime(rampUp: 10, rampDown: 4);
        Assert.Equal(0f, runtime.Step([0f], 1f, force: false));
        Assert.Equal(10f, runtime.Step([100f], 1f, force: false));
        Assert.Equal(20f, runtime.Step([100f], 1f, force: false));
        Assert.Equal(16f, runtime.Step([0f], 1f, force: false));
    }

    [Fact]
    public void SeedKeepsTheOutputWhenTheNextCurveWantsMore()
    {
        var running = Runtime(rampUp: 20);
        running.Step([40f], 1f, force: true);
        var output = running.Output;

        var edited = new RuleRuntime(new FanRuleConfig
        {
            Name = "cpu",
            FloorPercent = 0,
            SmoothingSeconds = 0,
            HysteresisC = 0,
            RampUpPercentPerSec = 5,
            FailsafePercent = 100,
            Curve =
            [
                new CurvePoint { TemperatureC = 0, Percent = 100 },
                new CurvePoint { TemperatureC = 100, Percent = 100 },
            ],
        });
        edited.Seed(output, 40f);
        var after = edited.Step([40f], 1f, force: false);

        Assert.Equal(output + 5f, after);
    }

    [Fact]
    public void HysteresisHoldsUntilTheTemperatureLeavesTheBand()
    {
        var runtime = Runtime(hysteresis: 2f);
        Assert.Equal(50f, runtime.Step([50f], 1f, force: false));
        Assert.Equal(50f, runtime.Step([51f], 1f, force: false));
        Assert.Equal(52f, runtime.Step([52f], 1f, force: false));
    }

    [Fact]
    public void FloorRaisesTheCurveAndAMissingSensorSnapsToFailsafe()
    {
        var runtime = Runtime(floor: 30);
        Assert.Equal(30f, runtime.Step([0f], 1f, force: false));
        Assert.Equal(100f, runtime.Step([float.NaN], 1f, force: false));
        Assert.True(runtime.IsFailing);
        Assert.Equal(40f, runtime.Step([40f], 1f, force: false));
        Assert.False(runtime.IsFailing);
    }

    [Fact]
    public void ResetAndForceSkipTheRamp()
    {
        var runtime = Runtime(rampUp: 10);
        runtime.Step([0f], 1f, force: false);
        Assert.Equal(10f, runtime.Step([100f], 1f, force: false));
        Assert.Equal(100f, runtime.Step([100f], 1f, force: true));

        runtime.Step([0f], 1f, force: true);
        runtime.Reset();
        Assert.Equal(80f, runtime.Step([80f], 1f, force: false));
    }

    [Fact]
    public void SeveralSensorsUseTheHottest()
    {
        var runtime = Runtime();
        Assert.Equal(70f, runtime.Step([40f, float.NaN, 70f], 1f, force: false));
    }

    private static RuleRuntime Runtime(float floor = 0, float hysteresis = 0, float rampUp = 0, float rampDown = 0) =>
        new(new FanRuleConfig
        {
            Name = "cpu",
            FloorPercent = floor,
            HysteresisC = hysteresis,
            SmoothingSeconds = 0,
            RampUpPercentPerSec = rampUp,
            RampDownPercentPerSec = rampDown,
            FailsafePercent = 100,
            Curve =
            [
                new CurvePoint { TemperatureC = 0, Percent = 0 },
                new CurvePoint { TemperatureC = 100, Percent = 100 },
            ],
        });
}
