namespace FanCurve.Core;

public static class RingFiles
{
    public const string IndexName = "recent.idx";
    public const string LockName = "writer.lock";

    public static string BinName(uint generation) => $"recent-{generation:D8}.bin";
}

public static class StorageLayout
{
    public static int Align(int value, int boundary)
    {
        if (boundary <= 0 || (boundary & (boundary - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundary));
        }

        return (value + boundary - 1) & ~(boundary - 1);
    }

    public static int RingSlotSize(int channelCount)
    {
        if (channelCount is < 1 or > FanCurveLimits.MaxChannels)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        return Align(8 + (2 * channelCount), 8);
    }

    public static int RingHeaderSize(int channelCount)
    {
        if (channelCount is < 1 or > FanCurveLimits.MaxChannels)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        return Align(RecentFormat.ChannelTable + (channelCount * ChannelTable.EntryBytes), 4096);
    }

    public static int HistoryRecordSize(int channelCount)
    {
        if (channelCount is < 1 or > FanCurveLimits.MaxChannels)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        return 16 + (8 * channelCount);
    }

    public static long HistoryBytesPerMonth(int channelCount, int minutesInMonth = 31 * 24 * 60) =>
        (long)HistoryRecordSize(channelCount) * minutesInMonth;
}

internal static class RecentFormat
{
    public const ushort Version = 1;
    public const int Magic = 0;
    public const int VersionOffset = 4;
    public const int ChannelCount = 6;
    public const int SlotCount = 8;
    public const int SlotSize = 12;
    public const int IntervalMs = 16;
    public const int HeaderSize = 20;
    public const int LayoutHash = 24;
    public const int WriteCount = 32;
    public const int ProcessStart = 40;
    public const int LastTick = 48;
    public const int Pid = 56;
    public const int ResumeCount = 60;
    public const int ConfigHash = 64;
    public const int FailsafeMask = 72;
    public const int Generation = 76;
    public const int RuleCount = 80;
    public const int RuleOutput = 82;
    public const int ChannelTable = 114;

    public static ReadOnlySpan<byte> MagicBytes => "FCRB"u8;
}

internal static class RecentIndexFormat
{
    public const ushort Version = 1;
    public const int Bytes = 64;
    public const int Generation = 8;
    public const int LayoutHash = 16;
    public const int Name = 24;
    public const int NameBytes = 40;

    public static ReadOnlySpan<byte> MagicBytes => "FCRI"u8;
}
