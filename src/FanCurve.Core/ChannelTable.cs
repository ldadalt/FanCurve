using System.Text;

namespace FanCurve.Core;

public enum ChannelKind : byte
{
    TemperatureC = 1,
    Rpm = 2,
    Percent = 3,
    LoadPercent = 4,
    PowerW = 5,
    MemoryGb = 6,
    ClockMhz = 7,
    Internal = 99,
}

public sealed record ChannelDesc(ChannelKind Kind, string Name, string SourceId);

public sealed class ChannelTable
{
    public const int NameBytes = 48;
    public const int SourceIdBytes = 96;
    public const int EntryBytes = 1 + NameBytes + SourceIdBytes;

    private readonly ChannelDesc[] _channels;

    private ChannelTable(ChannelDesc[] channels, ulong layoutHash)
    {
        _channels = channels;
        LayoutHash = layoutHash;
    }

    public IReadOnlyList<ChannelDesc> Channels => _channels;

    public int Count => _channels.Length;

    public ulong LayoutHash { get; }

    public static ChannelTable Create(params ChannelDesc[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length is < 1 or > FanCurveLimits.MaxChannels)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "A tabela precisa de 1 a 256 canais.");
        }

        var copy = new ChannelDesc[channels.Length];
        for (var i = 0; i < channels.Length; i++)
        {
            var channel = channels[i] ?? throw new ArgumentException("Canal nulo.", nameof(channels));
            copy[i] = channel with
            {
                Name = channel.Name ?? "",
                SourceId = channel.SourceId ?? "",
            };
        }

        var bytes = new byte[copy.Length * EntryBytes];
        WriteEntries(copy, bytes);
        return new ChannelTable(copy, Fnv1a64(bytes));
    }

    public static ChannelTable Read(ReadOnlySpan<byte> source, int count)
    {
        if (count is < 1 or > FanCurveLimits.MaxChannels)
        {
            throw new StorageFormatException($"Quantidade de canais inválida: {count}.");
        }

        var need = count * EntryBytes;
        if (source.Length < need)
        {
            throw new StorageFormatException("Tabela de canais truncada.");
        }

        var channels = new ChannelDesc[count];
        for (var i = 0; i < count; i++)
        {
            var entry = source.Slice(i * EntryBytes, EntryBytes);
            var kind = (ChannelKind)entry[0];
            var name = Decode(entry.Slice(1, NameBytes));
            var sourceId = Decode(entry.Slice(1 + NameBytes, SourceIdBytes));
            channels[i] = new ChannelDesc(kind, name, sourceId);
        }

        return new ChannelTable(channels, Fnv1a64(source[..need]));
    }

    public int Write(Span<byte> destination)
    {
        var need = Count * EntryBytes;
        if (destination.Length < need)
        {
            throw new ArgumentException("Buffer curto para a tabela de canais.", nameof(destination));
        }

        WriteEntries(_channels, destination[..need]);
        return need;
    }

    public bool CountsTowardFullScale(int index) =>
        _channels[index].Kind is ChannelKind.Percent or ChannelKind.LoadPercent;

    private static void WriteEntries(ChannelDesc[] channels, Span<byte> destination)
    {
        for (var i = 0; i < channels.Length; i++)
        {
            var entry = destination.Slice(i * EntryBytes, EntryBytes);
            entry.Clear();
            entry[0] = (byte)channels[i].Kind;
            WriteUtf8(channels[i].Name, entry.Slice(1, NameBytes));
            WriteUtf8(channels[i].SourceId, entry.Slice(1 + NameBytes, SourceIdBytes));
        }
    }

    internal static void WriteUtf8(string value, Span<byte> destination)
    {
        destination.Clear();
        var written = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (written + bytes > destination.Length)
            {
                break;
            }

            rune.EncodeToUtf8(destination[written..]);
            written += bytes;
        }
    }

    private static string Decode(ReadOnlySpan<byte> utf8)
    {
        var end = utf8.Length;
        while (end > 0 && utf8[end - 1] == 0)
        {
            end--;
        }

        return Encoding.UTF8.GetString(utf8[..end]);
    }

    internal static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
