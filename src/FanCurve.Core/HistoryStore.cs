using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

namespace FanCurve.Core;

public sealed class HistoryWriter : IDisposable
{
    private readonly string _directory;
    private readonly ChannelTable _channels;
    private readonly int _retentionMonths;
    private readonly int _headerSize;
    private readonly int _recordSize;
    private FileStream? _stream;
    private string? _openPath;
    private string? _month;
    private int _suffix;
    private int _count;
    private long _lastMinute = long.MinValue;
    private bool _disposed;

    public HistoryWriter(string directory, ChannelTable channels, int retentionMonths = 12)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (retentionMonths < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionMonths));
        }

        _directory = directory;
        _channels = channels;
        _retentionMonths = retentionMonths;
        _headerSize = HistoryFormat.HeaderSizeFor(channels.Count);
        _recordSize = StorageLayout.HistoryRecordSize(channels.Count);
        Directory.CreateDirectory(directory);
    }

    public void Append(MinuteRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        if (record.ChannelCount != _channels.Count)
        {
            throw new ArgumentException("O minuto não tem os canais deste arquivo.", nameof(record));
        }

        var minute = MinuteAggregator.MinuteOf(record.MinuteUnix);
        EnsureSegment(minute);
        if (_count > 0 && minute == _lastMinute)
        {
            return;
        }

        if (_count > 0 && minute < _lastMinute)
        {
            CloseSegment();
            EnsureSegment(minute);
        }

        WriteRecord(minute, record);
        if (_count == 0)
        {
            PatchFirstMinute(minute);
        }

        _count++;
        _lastMinute = minute;
        _stream!.Flush(flushToDisk: false);
    }

    public int ApplyRetention(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cursor = new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldestKept = cursor.AddMonths(-(_retentionMonths - 1));
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.fch"))
        {
            if (!HistoryFiles.TryParseMonth(Path.GetFileName(path), out var month))
            {
                continue;
            }

            if (month >= oldestKept)
            {
                continue;
            }

            if (_openPath is not null && string.Equals(path, _openPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(path);
            removed++;
        }

        return removed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseSegment(flushToDisk: true);
    }

    private enum HeaderState
    {
        Ok,
        Corrupt,
        DifferentLayout,
    }

    private void EnsureSegment(long minute)
    {
        var month = HistoryFiles.MonthKey(minute);
        if (_stream is not null && _month == month && (_count == 0 || minute >= _lastMinute))
        {
            return;
        }

        CloseSegment();
        OpenFor(month, minute);
    }

    private void OpenFor(string month, long minute)
    {
        var suffix = HistoryFiles.FindLatestSuffix(_directory, month);
        if (suffix > 0)
        {
            var path = HistoryFiles.PathFor(_directory, month, suffix);
            if (TryAttach(path, minute))
            {
                _month = month;
                _suffix = suffix;
                return;
            }
        }

        var created = suffix + 1;
        var createdPath = HistoryFiles.PathFor(_directory, month, created);
        _stream = OpenWrite(createdPath);
        WriteHeader(_stream);
        _openPath = createdPath;
        _month = month;
        _suffix = created;
        _count = 0;
        _lastMinute = long.MinValue;
    }

    private bool TryAttach(string path, long minute)
    {
        FileStream? stream = null;
        try
        {
            stream = OpenWrite(path);
            var state = Inspect(stream, out var count, out var lastMinute);
            if (state == HeaderState.Corrupt)
            {
                stream.Dispose();
                stream = null;
                HistoryFiles.Retire(path);
                return false;
            }

            if (state == HeaderState.DifferentLayout || (count > 0 && lastMinute > minute))
            {
                stream.Dispose();
                return false;
            }

            _stream = stream;
            _openPath = path;
            _count = count;
            _lastMinute = count == 0 ? long.MinValue : lastMinute;
            return true;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private HeaderState Inspect(FileStream stream, out int count, out long lastMinute)
    {
        count = 0;
        lastMinute = long.MinValue;
        if (stream.Length < HistoryFormat.FixedBytes)
        {
            return HeaderState.Corrupt;
        }

        Span<byte> magic = stackalloc byte[4];
        stream.Position = 0;
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(HistoryFormat.Magic))
        {
            return HeaderState.Corrupt;
        }

        if (ReadU16(stream, HistoryFormat.VersionOffset) != HistoryFormat.Version)
        {
            return HeaderState.Corrupt;
        }

        var sameLayout = ReadU16(stream, HistoryFormat.ChannelCount) == _channels.Count
            && ReadI32(stream, HistoryFormat.RecordSize) == _recordSize
            && ReadI32(stream, HistoryFormat.HeaderSize) == _headerSize
            && ReadHash(stream) == _channels.LayoutHash;
        if (!sameLayout)
        {
            return HeaderState.DifferentLayout;
        }

        var extra = (stream.Length - _headerSize) % _recordSize;
        if (extra != 0)
        {
            stream.SetLength(stream.Length - extra);
        }

        count = (int)((stream.Length - _headerSize) / _recordSize);
        if (count > 0)
        {
            lastMinute = ReadMinute(stream, count - 1);
        }

        return HeaderState.Ok;
    }

    private void WriteHeader(FileStream stream)
    {
        var header = new byte[_headerSize];
        HistoryFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HistoryFormat.VersionOffset), HistoryFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HistoryFormat.ChannelCount), (ushort)_channels.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(HistoryFormat.RecordSize), _recordSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(HistoryFormat.HeaderSize), _headerSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(HistoryFormat.LayoutHash), _channels.LayoutHash);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(HistoryFormat.FirstMinute), -1);
        _channels.Write(header.AsSpan(HistoryFormat.FixedBytes));
        stream.Position = 0;
        stream.Write(header);
        stream.SetLength(_headerSize);
    }

    private void WriteRecord(long minute, MinuteRecord record)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_recordSize);
        try
        {
            var span = buffer.AsSpan(0, _recordSize);
            span.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(span, minute);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], record.SampleCount);
            for (var i = 0; i < record.ChannelCount; i++)
            {
                var at = 16 + (i * 8);
                BinaryPrimitives.WriteUInt16LittleEndian(span[at..], BitConverter.HalfToUInt16Bits(record.Min[i]));
                BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 2)..], BitConverter.HalfToUInt16Bits(record.Avg[i]));
                BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 4)..], BitConverter.HalfToUInt16Bits(record.Max[i]));
                BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 6)..], record.SamplesAt100[i]);
            }

            _stream!.Position = _headerSize + ((long)_count * _recordSize);
            _stream.Write(span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void PatchFirstMinute(long minute)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, minute);
        _stream!.Position = HistoryFormat.FirstMinute;
        _stream.Write(bytes);
    }

    private ulong ReadHash(FileStream stream) => ReadU64(stream, HistoryFormat.LayoutHash);

    private long ReadMinute(FileStream stream, int index)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.Position = _headerSize + ((long)index * _recordSize);
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static ushort ReadU16(FileStream stream, int offset)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static int ReadI32(FileStream stream, int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static ulong ReadU64(FileStream stream, int offset)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private void CloseSegment(bool flushToDisk = false)
    {
        if (_stream is null)
        {
            return;
        }

        _stream.Flush(flushToDisk);
        _stream.Dispose();
        _stream = null;
        _openPath = null;
    }

    private static FileStream OpenWrite(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
}

public static class HistoryReader
{
    public static List<MinuteRecord> Read(string directory, long fromMinuteInclusive, long toMinuteExclusive)
    {
        var records = new List<MinuteRecord>();
        if (!Directory.Exists(directory))
        {
            return records;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.fch"))
        {
            ReadFile(path, fromMinuteInclusive, toMinuteExclusive, records);
        }

        records.Sort(static (a, b) => a.MinuteUnix.CompareTo(b.MinuteUnix));
        return records;
    }

    private static void ReadFile(string path, long fromMinute, long toMinute, List<MinuteRecord> sink)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> magic = stackalloc byte[4];
        if (stream.Length < HistoryFormat.FixedBytes || stream.Read(magic) < 4 || !magic.SequenceEqual(HistoryFormat.Magic))
        {
            throw new StorageFormatException($"Arquivo de histórico inválido: {Path.GetFileName(path)}.");
        }

        var version = ReadU16(stream, HistoryFormat.VersionOffset);
        if (version != HistoryFormat.Version)
        {
            throw new StorageFormatException($"Versão de histórico não suportada: {version}.");
        }

        var channels = ReadU16(stream, HistoryFormat.ChannelCount);
        var recordSize = ReadI32(stream, HistoryFormat.RecordSize);
        var headerSize = ReadI32(stream, HistoryFormat.HeaderSize);
        if (channels < 1 || recordSize != StorageLayout.HistoryRecordSize(channels) || headerSize < HistoryFormat.FixedBytes)
        {
            throw new StorageFormatException("Cabeçalho de histórico inválido.");
        }

        var usable = stream.Length - headerSize;
        if (usable < 0)
        {
            return;
        }

        var count = (int)(usable / recordSize);
        if (count == 0)
        {
            return;
        }

        var first = ReadMinute(stream, headerSize, recordSize, 0);
        var startGuess = (int)Math.Clamp((fromMinute - first) / 60_000, 0, count - 1);
        var start = ReadMinute(stream, headerSize, recordSize, startGuess) == fromMinute
            ? startGuess
            : LowerBound(stream, headerSize, recordSize, count, fromMinute);

        for (var i = start; i < count; i++)
        {
            var record = ReadRecord(stream, headerSize, recordSize, channels, i);
            if (record.MinuteUnix < fromMinute)
            {
                continue;
            }

            if (record.MinuteUnix >= toMinute)
            {
                break;
            }

            sink.Add(record);
        }
    }

    private static int LowerBound(FileStream stream, int headerSize, int recordSize, int count, long minute)
    {
        var lo = 0;
        var hi = count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (ReadMinute(stream, headerSize, recordSize, mid) < minute)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static MinuteRecord ReadRecord(FileStream stream, int headerSize, int recordSize, int channels, int index)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(recordSize);
        try
        {
            var span = buffer.AsSpan(0, recordSize);
            stream.Position = headerSize + ((long)index * recordSize);
            stream.ReadExactly(span);
            var minute = BinaryPrimitives.ReadInt64LittleEndian(span);
            var sampleCount = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            var min = new Half[channels];
            var avg = new Half[channels];
            var max = new Half[channels];
            var at100 = new ushort[channels];
            for (var i = 0; i < channels; i++)
            {
                var at = 16 + (i * 8);
                min[i] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span[at..]));
                avg[i] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]));
                max[i] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 4)..]));
                at100[i] = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 6)..]);
            }

            return new MinuteRecord(minute, sampleCount, min, avg, max, at100);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long ReadMinute(FileStream stream, int headerSize, int recordSize, int index)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.Position = headerSize + ((long)index * recordSize);
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static ushort ReadU16(FileStream stream, int offset)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private static int ReadI32(FileStream stream, int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public readonly record struct HistorySlice(ChannelTable Channels, MinuteRecord Record);

    public static List<HistorySlice> ReadSlices(string directory, long fromMinuteInclusive, long toMinuteExclusive)
    {
        var slices = new List<HistorySlice>();
        if (!Directory.Exists(directory))
        {
            return slices;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.fch"))
        {
            ReadSlicesFile(path, fromMinuteInclusive, toMinuteExclusive, slices);
        }

        slices.Sort(static (a, b) => a.Record.MinuteUnix.CompareTo(b.Record.MinuteUnix));
        return slices;
    }

    public static List<ChannelTable> ListChannelTables(string directory)
    {
        var tables = new List<ChannelTable>();
        if (!Directory.Exists(directory))
        {
            return tables;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.fch"))
        {
            if (TryReadChannelTable(path, out var table) && table is not null)
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    private static void ReadSlicesFile(string path, long fromMinute, long toMinute, List<HistorySlice> sink)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!TryReadChannelTable(stream, out var table) || table is null)
        {
            throw new StorageFormatException($"Arquivo de histórico inválido: {Path.GetFileName(path)}.");
        }

        var channels = table.Count;
        var recordSize = ReadI32(stream, HistoryFormat.RecordSize);
        var headerSize = ReadI32(stream, HistoryFormat.HeaderSize);
        if (recordSize != StorageLayout.HistoryRecordSize(channels) || headerSize < HistoryFormat.FixedBytes)
        {
            throw new StorageFormatException("Cabeçalho de histórico inválido.");
        }

        var usable = stream.Length - headerSize;
        if (usable < recordSize)
        {
            return;
        }

        var count = (int)(usable / recordSize);
        var first = ReadMinute(stream, headerSize, recordSize, 0);
        var startGuess = (int)Math.Clamp((fromMinute - first) / 60_000, 0, count - 1);
        var start = ReadMinute(stream, headerSize, recordSize, startGuess) == fromMinute
            ? startGuess
            : LowerBound(stream, headerSize, recordSize, count, fromMinute);

        for (var i = start; i < count; i++)
        {
            var record = ReadRecord(stream, headerSize, recordSize, channels, i);
            if (record.MinuteUnix < fromMinute)
            {
                continue;
            }

            if (record.MinuteUnix >= toMinute)
            {
                break;
            }

            sink.Add(new HistorySlice(table, record));
        }
    }

    private static bool TryReadChannelTable(string path, out ChannelTable? table)
    {
        table = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryReadChannelTable(stream, out table);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryReadChannelTable(FileStream stream, out ChannelTable? table)
    {
        table = null;
        if (stream.Length < HistoryFormat.FixedBytes)
        {
            return false;
        }

        Span<byte> magic = stackalloc byte[4];
        stream.Position = 0;
        if (stream.Read(magic) < 4 || !magic.SequenceEqual(HistoryFormat.Magic))
        {
            return false;
        }

        if (ReadU16(stream, HistoryFormat.VersionOffset) != HistoryFormat.Version)
        {
            return false;
        }

        var channels = ReadU16(stream, HistoryFormat.ChannelCount);
        var headerSize = ReadI32(stream, HistoryFormat.HeaderSize);
        var tableBytes = channels * ChannelTable.EntryBytes;
        if (channels < 1 || headerSize < HistoryFormat.FixedBytes + tableBytes || stream.Length < HistoryFormat.FixedBytes + tableBytes)
        {
            return false;
        }

        var buffer = new byte[tableBytes];
        stream.Position = HistoryFormat.FixedBytes;
        stream.ReadExactly(buffer);
        table = ChannelTable.Read(buffer, channels);
        return true;
    }
}

internal static class HistoryFormat
{
    public const ushort Version = 1;
    public const int VersionOffset = 4;
    public const int ChannelCount = 6;
    public const int RecordSize = 8;
    public const int HeaderSize = 12;
    public const int LayoutHash = 16;
    public const int FirstMinute = 24;
    public const int FixedBytes = 32;

    public static ReadOnlySpan<byte> Magic => "FCAG"u8;

    public static int HeaderSizeFor(int channelCount) =>
        StorageLayout.Align(FixedBytes + (channelCount * ChannelTable.EntryBytes), 8);
}

internal static class HistoryFiles
{
    public static string MonthKey(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string PathFor(string directory, string month, int suffix) =>
        Path.Combine(directory, suffix <= 1 ? $"{month}.fch" : $"{month}.{suffix}.fch");

    public static int FindLatestSuffix(string directory, string month)
    {
        var latest = 0;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        foreach (var path in Directory.EnumerateFiles(directory, month + "*.fch"))
        {
            if (TryParseSuffix(month, Path.GetFileName(path), out var suffix) && suffix > latest)
            {
                latest = suffix;
            }
        }

        return latest;
    }

    public static bool TryParseMonth(string fileName, out DateTime month)
    {
        month = default;
        if (fileName.Length < 11 || fileName[4] != '-' || fileName[7] != '.')
        {
            return false;
        }

        if (!int.TryParse(fileName.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(fileName.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var monthNumber)
            || monthNumber is < 1 or > 12)
        {
            return false;
        }

        month = new DateTime(year, monthNumber, 1, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    public static void Retire(string path)
    {
        var target = path + ".bad";
        if (File.Exists(target))
        {
            target = path + "." + DateTime.UtcNow.Ticks + ".bad";
        }

        File.Move(path, target, overwrite: false);
    }

    private static bool TryParseSuffix(string month, string fileName, out int suffix)
    {
        suffix = 0;
        var plain = month + ".fch";
        if (string.Equals(fileName, plain, StringComparison.OrdinalIgnoreCase))
        {
            suffix = 1;
            return true;
        }

        var prefix = month + ".";
        const string ending = ".fch";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var number = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - ending.Length);
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out suffix) && suffix > 1;
    }
}
