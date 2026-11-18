namespace FanCurve.Core;

public static class FanCurveLimits
{
    public const int MaxRules = 16;
    public const int MaxChannels = 256;
    public const int MaxRecentHours = 168;
    public const long GapLimitMs = 2_000;
    public const int DefaultSlotCount = 24 * 60 * 60;
}
