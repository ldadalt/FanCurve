using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FanCurve.Core;

public sealed record RecentRingOptions
{
    public int SlotCount { get; init; } = FanCurveLimits.DefaultSlotCount;

    public int IntervalMs { get; init; } = 1000;

    public ulong ConfigHash { get; init; }
}

public readonly record struct RingStatus(
    long LastTickUnixMs,
    long ProcessStartUnixMs,
    uint Pid,
    uint FailsafeMask,
    uint ResumeCount,
    ulong WriteCount,
    ulong ConfigHash,
    uint Generation);

public sealed class RecentRingWriter : IDisposable
{
    private readonly SingleWriterLock? _lock;
    private readonly MappedFile _map;
    private readonly string _directory;
    private readonly int _slotSize;
    private readonly uint _slotCount;
    private readonly int _channelCount;
    private ulong _published;
    private bool _disposed;

    private RecentRingWriter(
        SingleWriterLock? singleWriter,
        MappedFile map,
        string directory,
        int slotSize,
        uint slotCount,
        int channelCount,
        ulong published)
    {
        _lock = singleWriter;
        _map = map;
        _directory = directory;
        _slotSize = slotSize;
        _slotCount = slotCount;
        _channelCount = channelCount;
        _published = published;
    }

    public int ChannelCount => _channelCount;

    public uint SlotCount => _slotCount;

    public ulong WriteCount => _published;

    public static RecentRingWriter Open(string directory, ChannelTable channels, RecentRingOptions? options = null, bool ownLock = true)
    {
        ArgumentNullException.ThrowIfNull(channels);
        options ??= new RecentRingOptions();
        if (options.SlotCount is < 2 or > FanCurveLimits.MaxRecentHours * 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlotCount fica entre 2 e 168 horas.");
        }

        if (options.IntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "IntervalMs precisa ser positivo.");
        }

        // O serviço segura o lock o tempo todo e abre o anel com ownLock: false,
        // para uma troca de layout não soltar a exclusividade.
        SingleWriterLock? gate = ownLock ? SingleWriterLock.Acquire(directory) : null;
        try
        {
            var slotCount = (uint)options.SlotCount;
            var slotSize = StorageLayout.RingSlotSize(channels.Count);
            var headerSize = StorageLayout.RingHeaderSize(channels.Count);
            var existing = RecentIndex.TryRead(directory);
            uint generation = 1;
            string? resumePath = null;
            if (existing is { } index)
            {
                var candidate = Path.Combine(directory, index.FileName);
                if (CanResume(candidate, channels, slotCount, slotSize, headerSize))
                {
                    generation = index.Generation;
                    resumePath = candidate;
                }
                else
                {
                    generation = index.Generation == uint.MaxValue ? 1 : index.Generation + 1;
                }
            }

            var fileName = resumePath is null ? RingFiles.BinName(generation) : Path.GetFileName(resumePath);
            var path = resumePath ?? Path.Combine(directory, fileName);
            var total = headerSize + ((long)slotCount * slotSize);
            var stream = OpenStream(path);
            try
            {
                if (resumePath is null)
                {
                    stream.SetLength(total);
                }
                else if (stream.Length != total)
                {
                    throw new StorageFormatException("Tamanho do anel não confere com o cabeçalho.");
                }

                var map = MappedFile.Open(stream, total, MemoryMappedFileAccess.ReadWrite);
                try
                {
                    unsafe
                    {
                        var writer = new RecentRingWriter(gate, map, directory, slotSize, slotCount, channels.Count, 0);
                        if (resumePath is null)
                        {
                            writer.WriteNewHeader(channels, generation, slotCount, slotSize, headerSize, options);
                            RecentIndex.WriteAtomic(directory, generation, channels.LayoutHash, fileName);
                        }
                        else
                        {
                            writer._published = MapIo.ReadU64(map.Base, RecentFormat.WriteCount);
                            MapIo.WriteI64(map.Base, RecentFormat.ProcessStart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            MapIo.WriteU32(map.Base, RecentFormat.Pid, (uint)Environment.ProcessId);
                            MapIo.WriteI64(map.Base, RecentFormat.LastTick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            MapIo.WriteU64(map.Base, RecentFormat.ConfigHash, options.ConfigHash);
                        }

                        writer.DeleteRetired(path);
                        return writer;
                    }
                }
                catch
                {
                    map.Dispose();
                    throw;
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch
        {
            gate?.Dispose();
            throw;
        }
    }

    public unsafe void Write(long unixMs, ReadOnlySpan<Half> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (values.Length != _channelCount)
        {
            throw new ArgumentException("A amostra não tem o número de canais da tabela.", nameof(values));
        }

        var physical = (uint)(_published % _slotCount);
        var slot = Slot(physical);
        Unsafe.WriteUnaligned(slot, unixMs);
        for (var i = 0; i < values.Length; i++)
        {
            Unsafe.WriteUnaligned(slot + 8 + (i * 2), values[i]);
        }

        var next = _published + 1;
        MapIo.VolatileWriteI64(_map.Base, RecentFormat.LastTick, unixMs);
        MapIo.VolatileWriteU64(_map.Base, RecentFormat.WriteCount, next);
        _published = next;
    }

    public unsafe void SetControlState(uint failsafeMask, uint resumeCount, ReadOnlySpan<Half> ruleOutputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ruleOutputs.Length > FanCurveLimits.MaxRules)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleOutputs), "O cabeçalho guarda no máximo 16 regras.");
        }

        MapIo.WriteU32(_map.Base, RecentFormat.FailsafeMask, failsafeMask);
        MapIo.WriteU32(_map.Base, RecentFormat.ResumeCount, resumeCount);
        MapIo.WriteU16(_map.Base, RecentFormat.RuleCount, (ushort)ruleOutputs.Length);
        for (var i = 0; i < FanCurveLimits.MaxRules; i++)
        {
            var value = i < ruleOutputs.Length ? ruleOutputs[i] : default;
            Unsafe.WriteUnaligned(_map.Base + RecentFormat.RuleOutput + (i * 2), value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _map.FlushToDisk();
        _map.Dispose();
        _lock?.Dispose();
    }

    private unsafe byte* Slot(uint physical) =>
        _map.Base + MapIo.ReadI32(_map.Base, RecentFormat.HeaderSize) + ((long)physical * _slotSize);

    private unsafe void WriteNewHeader(
        ChannelTable channels,
        uint generation,
        uint slotCount,
        int slotSize,
        int headerSize,
        RecentRingOptions options)
    {
        new Span<byte>(_map.Base, headerSize).Clear();
        RecentFormat.MagicBytes.CopyTo(new Span<byte>(_map.Base, 4));
        MapIo.WriteU16(_map.Base, RecentFormat.VersionOffset, RecentFormat.Version);
        MapIo.WriteU16(_map.Base, RecentFormat.ChannelCount, (ushort)channels.Count);
        MapIo.WriteU32(_map.Base, RecentFormat.SlotCount, slotCount);
        MapIo.WriteU32(_map.Base, RecentFormat.SlotSize, (uint)slotSize);
        MapIo.WriteU32(_map.Base, RecentFormat.IntervalMs, (uint)options.IntervalMs);
        MapIo.WriteU32(_map.Base, RecentFormat.HeaderSize, (uint)headerSize);
        MapIo.WriteU64(_map.Base, RecentFormat.LayoutHash, channels.LayoutHash);
        MapIo.WriteI64(_map.Base, RecentFormat.ProcessStart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        MapIo.WriteI64(_map.Base, RecentFormat.LastTick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        MapIo.WriteU32(_map.Base, RecentFormat.Pid, (uint)Environment.ProcessId);
        MapIo.WriteU64(_map.Base, RecentFormat.ConfigHash, options.ConfigHash);
        MapIo.WriteU32(_map.Base, RecentFormat.Generation, generation);
        channels.Write(new Span<byte>(_map.Base + RecentFormat.ChannelTable, channels.Count * ChannelTable.EntryBytes));
        MapIo.VolatileWriteU64(_map.Base, RecentFormat.WriteCount, 0);
    }

    private void DeleteRetired(string currentPath)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "recent-*.bin"))
        {
            if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
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
        }
    }

    private static bool CanResume(string path, ChannelTable channels, uint slotCount, int slotSize, int headerSize)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = OpenStream(path);
            if (stream.Length != headerSize + ((long)slotCount * slotSize))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[RecentFormat.ChannelTable];
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            if (!header[..4].SequenceEqual(RecentFormat.MagicBytes))
            {
                return false;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(header[RecentFormat.VersionOffset..]) == RecentFormat.Version
                && BinaryPrimitives.ReadUInt16LittleEndian(header[RecentFormat.ChannelCount..]) == channels.Count
                && BinaryPrimitives.ReadUInt32LittleEndian(header[RecentFormat.SlotCount..]) == slotCount
                && BinaryPrimitives.ReadUInt32LittleEndian(header[RecentFormat.SlotSize..]) == (uint)slotSize
                && BinaryPrimitives.ReadUInt32LittleEndian(header[RecentFormat.HeaderSize..]) == (uint)headerSize
                && BinaryPrimitives.ReadUInt64LittleEndian(header[RecentFormat.LayoutHash..]) == channels.LayoutHash;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static FileStream OpenStream(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
}

public sealed class RecentRingReader : IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private MappedFile? _map;
    private ChannelTable? _channels;
    private int _headerSize;
    private int _slotSize;
    private uint _slotCount;
    private uint _generation;
    private ulong _layoutHash;
    private bool _disposed;

    private RecentRingReader(string directory)
    {
        _directory = directory;
    }

    public int ChannelCount => _channels?.Count ?? 0;

    public IReadOnlyList<ChannelDesc> Channels => _channels?.Channels ?? [];

    public uint Generation => _generation;

    public ulong LayoutHash => _layoutHash;

    public uint SlotCount => _slotCount;

    public static RecentRingReader Open(string directory)
    {
        var reader = new RecentRingReader(directory);
        if (!reader.TryReload())
        {
            reader.Dispose();
            throw new FileNotFoundException("O FanCurve ainda não gravou o anel.", Path.Combine(directory, RingFiles.IndexName));
        }

        return reader;
    }

    public bool TryReload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            var index = RecentIndex.TryRead(_directory);
            if (index is null)
            {
                return false;
            }

            if (_map is not null && index.Value.Generation == _generation && index.Value.LayoutHash == _layoutHash)
            {
                return false;
            }

            var path = Path.Combine(_directory, index.Value.FileName);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            MappedFile map;
            try
            {
                map = MappedFile.Open(stream, stream.Length, MemoryMappedFileAccess.Read);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            try
            {
                Bind(map, index.Value.Generation);
            }
            catch
            {
                map.Dispose();
                throw;
            }

            _map?.Dispose();
            _map = map;
            return true;
        }
    }

    public unsafe int CopyNew(ulong fromIndex, Span<long> unixMs, Span<Half> values, out ulong nextIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_map is null)
            {
                nextIndex = fromIndex;
                return 0;
            }

            var channels = ChannelCount;
            var capacity = unixMs.Length;
            if ((long)values.Length < (long)capacity * channels)
            {
                throw new ArgumentException("Buffer de valores curto.", nameof(values));
            }
            var slotCount = (ulong)_slotCount;
            var w1 = MapIo.VolatileReadU64(_map.Base, RecentFormat.WriteCount);
            if (w1 == 0 || fromIndex >= w1 || capacity == 0)
            {
                nextIndex = fromIndex;
                return 0;
            }

            var start = fromIndex;
            if (w1 >= slotCount && start < w1 - slotCount + 1)
            {
                start = w1 - slotCount + 1;
            }

            var copied = 0;
            var index = start;
            while (index < w1 && copied < capacity)
            {
                CopySlot(index, out unixMs[copied], values.Slice(copied * channels, channels));
                copied++;
                index++;
            }

            Interlocked.MemoryBarrier();
            var w2 = MapIo.VolatileReadU64(_map.Base, RecentFormat.WriteCount);
            var kept = 0;
            for (var n = 0; n < copied; n++)
            {
                var sample = start + (ulong)n;
                if (w2 >= slotCount && sample <= w2 - slotCount)
                {
                    continue;
                }

                if (kept != n)
                {
                    unixMs[kept] = unixMs[n];
                    for (var c = 0; c < channels; c++)
                    {
                        values[(kept * channels) + c] = values[(n * channels) + c];
                    }
                }

                kept++;
            }

            nextIndex = start + (ulong)copied;
            return kept;
        }
    }

    public unsafe RingStatus ReadStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_map is null)
            {
                throw new InvalidOperationException("Anel não está aberto.");
            }

            var writeCount = MapIo.VolatileReadU64(_map.Base, RecentFormat.WriteCount);
            return new RingStatus(
                MapIo.VolatileReadI64(_map.Base, RecentFormat.LastTick),
                MapIo.ReadI64(_map.Base, RecentFormat.ProcessStart),
                MapIo.ReadU32(_map.Base, RecentFormat.Pid),
                MapIo.ReadU32(_map.Base, RecentFormat.FailsafeMask),
                MapIo.ReadU32(_map.Base, RecentFormat.ResumeCount),
                writeCount,
                MapIo.ReadU64(_map.Base, RecentFormat.ConfigHash),
                _generation);
        }
    }

    public unsafe int ReadRuleOutputs(Span<Half> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_map is null)
            {
                return 0;
            }

            var count = Math.Min(MapIo.ReadU16(_map.Base, RecentFormat.RuleCount), (ushort)FanCurveLimits.MaxRules);
            if (destination.Length < count)
            {
                throw new ArgumentException("Buffer curto para as regras.", nameof(destination));
            }

            for (var i = 0; i < count; i++)
            {
                destination[i] = Unsafe.ReadUnaligned<Half>(_map.Base + RecentFormat.RuleOutput + (i * 2));
            }

            return count;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _map?.Dispose();
            _map = null;
        }
    }

    private unsafe void Bind(MappedFile map, uint generation)
    {
        if (map.Length < RecentFormat.ChannelTable)
        {
            throw new StorageFormatException("Anel curto demais.");
        }

        if (!new ReadOnlySpan<byte>(map.Base, 4).SequenceEqual(RecentFormat.MagicBytes))
        {
            throw new StorageFormatException("Magic do anel inválido.");
        }

        var version = MapIo.ReadU16(map.Base, RecentFormat.VersionOffset);
        if (version != RecentFormat.Version)
        {
            throw new StorageFormatException($"Versão de anel não suportada: {version}.");
        }

        var channelCount = MapIo.ReadU16(map.Base, RecentFormat.ChannelCount);
        var headerSize = checked((int)MapIo.ReadU32(map.Base, RecentFormat.HeaderSize));
        var slotSize = checked((int)MapIo.ReadU32(map.Base, RecentFormat.SlotSize));
        var slotCount = MapIo.ReadU32(map.Base, RecentFormat.SlotCount);
        if (slotSize != StorageLayout.RingSlotSize(channelCount) || slotSize % 8 != 0)
        {
            throw new StorageFormatException("Slot do anel não está alinhado.");
        }

        var tableBytes = channelCount * ChannelTable.EntryBytes;
        if (headerSize < RecentFormat.ChannelTable + tableBytes || headerSize % 4096 != 0)
        {
            throw new StorageFormatException("Cabeçalho do anel inválido.");
        }

        var expected = headerSize + ((long)slotCount * slotSize);
        if (map.Length != expected)
        {
            throw new StorageFormatException("Tamanho do anel não confere.");
        }

        var table = ChannelTable.Read(new ReadOnlySpan<byte>(map.Base + RecentFormat.ChannelTable, tableBytes), channelCount);
        var hash = MapIo.ReadU64(map.Base, RecentFormat.LayoutHash);
        if (hash != table.LayoutHash)
        {
            throw new StorageFormatException("Hash da tabela de canais não confere.");
        }

        _channels = table;
        _headerSize = headerSize;
        _slotSize = slotSize;
        _slotCount = slotCount;
        _generation = generation;
        _layoutHash = hash;
    }

    private unsafe void CopySlot(ulong index, out long unixMs, Span<Half> destination)
    {
        var physical = (uint)(index % _slotCount);
        var slot = _map!.Base + _headerSize + ((long)physical * _slotSize);
        unixMs = Unsafe.ReadUnaligned<long>(slot);
        new ReadOnlySpan<Half>(slot + 8, destination.Length).CopyTo(destination);
    }
}

internal readonly record struct RingIndex(uint Generation, ulong LayoutHash, string FileName);

internal static class RecentIndex
{
    public static RingIndex? TryRead(string directory)
    {
        var path = Path.Combine(directory, RingFiles.IndexName);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new StorageFormatException("Não foi possível ler recent.idx.", ex);
        }

        if (bytes.Length < RecentIndexFormat.Bytes
            || !bytes.AsSpan(0, 4).SequenceEqual(RecentIndexFormat.MagicBytes)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != RecentIndexFormat.Version)
        {
            throw new StorageFormatException("recent.idx inválido.");
        }

        var generation = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(RecentIndexFormat.Generation));
        var hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(RecentIndexFormat.LayoutHash));
        var nameBytes = bytes.AsSpan(RecentIndexFormat.Name, RecentIndexFormat.NameBytes);
        var end = nameBytes.Length;
        while (end > 0 && nameBytes[end - 1] == 0)
        {
            end--;
        }

        var name = Encoding.UTF8.GetString(nameBytes[..end]);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name)
        {
            throw new StorageFormatException("Nome de anel inválido no índice.");
        }

        return new RingIndex(generation, hash, name);
    }

    public static void WriteAtomic(string directory, uint generation, ulong layoutHash, string fileName)
    {
        var bytes = new byte[RecentIndexFormat.Bytes];
        RecentIndexFormat.MagicBytes.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), RecentIndexFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RecentIndexFormat.Generation), generation);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(RecentIndexFormat.LayoutHash), layoutHash);
        var name = Encoding.UTF8.GetBytes(fileName);
        if (name.Length > RecentIndexFormat.NameBytes)
        {
            throw new StorageFormatException("Nome do anel não cabe no índice.");
        }

        name.CopyTo(bytes.AsSpan(RecentIndexFormat.Name));
        var finalPath = Path.Combine(directory, RingFiles.IndexName);
        var tempPath = finalPath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }
}

internal static class MapIo
{
    public static unsafe ushort ReadU16(byte* p, int offset) => Unsafe.ReadUnaligned<ushort>(p + offset);

    public static unsafe uint ReadU32(byte* p, int offset) => Unsafe.ReadUnaligned<uint>(p + offset);

    public static unsafe int ReadI32(byte* p, int offset) => Unsafe.ReadUnaligned<int>(p + offset);

    public static unsafe ulong ReadU64(byte* p, int offset) => Unsafe.ReadUnaligned<ulong>(p + offset);

    public static unsafe long ReadI64(byte* p, int offset) => Unsafe.ReadUnaligned<long>(p + offset);

    public static unsafe ulong VolatileReadU64(byte* p, int offset) =>
        Volatile.Read(ref Unsafe.AsRef<ulong>((void*)(p + offset)));

    public static unsafe long VolatileReadI64(byte* p, int offset) =>
        Volatile.Read(ref Unsafe.AsRef<long>((void*)(p + offset)));

    public static unsafe void WriteU16(byte* p, int offset, ushort value) => Unsafe.WriteUnaligned(p + offset, value);

    public static unsafe void WriteU32(byte* p, int offset, uint value) => Unsafe.WriteUnaligned(p + offset, value);

    public static unsafe void WriteU64(byte* p, int offset, ulong value) => Unsafe.WriteUnaligned(p + offset, value);

    public static unsafe void WriteI64(byte* p, int offset, long value) => Unsafe.WriteUnaligned(p + offset, value);

    public static unsafe void VolatileWriteU64(byte* p, int offset, ulong value) =>
        Volatile.Write(ref Unsafe.AsRef<ulong>((void*)(p + offset)), value);

    public static unsafe void VolatileWriteI64(byte* p, int offset, long value) =>
        Volatile.Write(ref Unsafe.AsRef<long>((void*)(p + offset)), value);
}

internal sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly FileStream _stream;
    private readonly byte* _base;
    private bool _disposed;

    private MappedFile(MemoryMappedFile mmf, MemoryMappedViewAccessor view, FileStream stream, byte* pointer)
    {
        _mmf = mmf;
        _view = view;
        _stream = stream;
        _base = pointer;
    }

    public byte* Base => _base;

    public long Length => _stream.Length;

    public static MappedFile Open(FileStream stream, long size, MemoryMappedFileAccess access)
    {
        var mmf = MemoryMappedFile.CreateFromFile(stream, mapName: null, size, access, HandleInheritability.None, leaveOpen: true);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = mmf.CreateViewAccessor(0, size, access);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            if (pointer == null)
            {
                throw new IOException("Não foi possível mapear o arquivo.");
            }

            pointer += view.PointerOffset;
            return new MappedFile(mmf, view, stream, pointer);
        }
        catch
        {
            view?.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _view.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
        _stream.Dispose();
    }
}
