using System.Runtime.InteropServices;

namespace PlexCompatibleServer.Infrastructure.Media;

public static class MediaDurationProbe
{
    private const ulong EbmlHeader = 0x1A45DFA3;
    private const ulong EbmlSegment = 0x18538067;
    private const ulong EbmlInfo = 0x1549A966;
    private const ulong EbmlTimecodeScale = 0x2AD7B1;
    private const ulong EbmlDuration = 0x4489;
    private const ulong EbmlUnknownSize = ulong.MaxValue;

    private static readonly uint BoxMoov = FourCc("moov");
    private static readonly uint BoxMvhd = FourCc("mvhd");

    public static int? GetDurationMs(string path)
    {
        try
        {
            // No SequentialScan hint: the probe seeks between the start of the
            // file and a trailing 'moov'/'Info', which that hint penalises.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024);

            if (stream.Length < 8) return null;

            var duration = TryReadMatroska(stream);
            if (duration != 0) return duration;

            stream.Position = 0;
            var isoDuration = TryReadIsoBaseMedia(stream);
            return isoDuration != 0 ? isoDuration : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// Same probe, but the file seek/read runs on the thread pool so a scan never blocks the
    /// caller's thread. Cancellation is only observed before the work is scheduled: the probe
    /// itself is short and does not poll the token.
    /// </summary>
    public static Task<int?> GetDurationMsAsync(string path, CancellationToken ct)
        => Task.Run(() => GetDurationMs(path), ct);


    private static int TryReadMatroska(Stream stream)
    {
        if (!TryReadEbmlId(stream, out var id) || id != EbmlHeader) return 0;
        if (!TryReadEbmlSize(stream, out var size)) return 0;

        var headerEnd = EndOf(stream.Position, size);
        if (headerEnd <= stream.Position) return 0;
        stream.Position = headerEnd;

        while (stream.Position + 2 <= stream.Length)
        {
            if (!TryReadEbmlId(stream, out id) || !TryReadEbmlSize(stream, out size)) return 0;

            var bodyStart = stream.Position;
            var bodyEnd = EndOf(bodyStart, size);
            if (bodyEnd <= bodyStart) return 0;

            if (id == EbmlSegment) return TryReadSegment(stream, bodyEnd);

            stream.Position = bodyEnd;
        }

        return 0;
    }

    private static int TryReadSegment(Stream stream, long segmentEnd)
    {
        while (stream.Position + 2 <= segmentEnd)
        {
            if (!TryReadEbmlId(stream, out var childId) || !TryReadEbmlSize(stream, out var childSize)) return 0;

            var bodyStart = stream.Position;
            var bodyEnd = EndOf(bodyStart, childSize, segmentEnd);
            if (bodyEnd <= bodyStart) return 0;

            if (childId == EbmlInfo) return TryReadMatroskaInfo(stream, bodyEnd);

            stream.Position = bodyEnd;
        }

        return 0;
    }

    private static int TryReadMatroskaInfo(Stream stream, long infoEnd)
    {
        var timecodeScale = 1_000_000d;
        double ticks = -1;

        while (stream.Position + 2 <= infoEnd)
        {
            if (!TryReadEbmlId(stream, out var id) || !TryReadEbmlSize(stream, out var size)) return 0;

            var bodyStart = stream.Position;
            var bodyEnd = EndOf(bodyStart, size, infoEnd);
            if (bodyEnd <= bodyStart) return 0;

            if (id == EbmlTimecodeScale && size <= 8)
            {
                if (!TryReadUIntBE(stream, (int)size, out var scale)) return 0;
                if (scale > 0) timecodeScale = scale;
            }
            else if (id == EbmlDuration && size == 4)
            {
                if (!TryReadSingle(stream, out var value)) return 0;
                ticks = value;
            }
            else if (id == EbmlDuration && size == 8)
            {
                if (!TryReadDouble(stream, out var value)) return 0;
                ticks = value;
            }

            stream.Position = bodyEnd;
        }

        if (ticks < 0) return 0;

        return ToMilliseconds(ticks * timecodeScale / 1_000_000d);
    }

    private static int TryReadIsoBaseMedia(Stream stream)
    {
        // 'moov' is often written after 'mdat' by streaming-friendly muxers, which
        // places it far past any small header window in large files. Walking
        // top-level boxes only seeks, so scanning to the end stays cheap; the
        // box counter guards against pathological files.
        const int maxTopLevelBoxes = 4096;

        for (var i = 0; i < maxTopLevelBoxes && stream.Position + 8 <= stream.Length; i++)
        {
            if (!TryReadBox(stream, stream.Length, out var box)) return 0;
            if (box.Type == BoxMoov) return TryReadMoov(stream, box.BodyEnd);

            // A zero-length payload is legal (for example an 8-byte 'free'
            // box), so only stop when the box failed to advance at all.
            if (box.BodyEnd < box.BodyStart) return 0;
            stream.Position = box.BodyEnd;
        }

        return 0;
    }

    private static int TryReadMoov(Stream stream, long moovEnd)
    {
        while (stream.Position + 8 <= moovEnd)
        {
            if (!TryReadBox(stream, moovEnd, out var box)) return 0;
            if (box.Type == BoxMvhd) return TryReadMvhd(stream, box);

            // Zero-length children are legal; only bail if nothing was consumed.
            if (box.BodyEnd < box.BodyStart) return 0;
            stream.Position = box.BodyEnd;
        }

        return 0;
    }

    private static int TryReadMvhd(Stream stream, Box box)
    {
        stream.Position = box.BodyStart;

        var version = stream.ReadByte();
        if (version < 0) return 0;
        stream.Position += 3;

        var wide = version == 1;
        var creationSize = wide ? 8 : 4;

        stream.Position += creationSize * 2;
        if (stream.Position + (wide ? 12 : 8) > box.BodyEnd) return 0;

        if (!TryReadUInt32(stream, out var timescale)) return 0;

        ulong duration;
        if (wide)
        {
            if (!TryReadUInt64(stream, out duration)) return 0;
        }
        else
        {
            if (!TryReadUInt32(stream, out var narrow)) return 0;
            duration = narrow;
        }

        if (timescale == 0 || duration == 0) return 0;

        return ToMilliseconds(duration * 1000d / timescale);
    }

    private static bool TryReadBox(Stream stream, long limit, out Box box)
    {
        box = default;

        var start = stream.Position;
        if (start + 8 > limit) return false;

        if (!TryReadUInt32(stream, out var size)) return false;
        if (!TryReadUInt32(stream, out var type)) return false;

        var headerSize = 8;
        long boxSize;

        if (size == 1)
        {
            if (!TryReadUInt64(stream, out var large) || large > long.MaxValue) return false;
            boxSize = (long)large;
            headerSize = 16;
        }
        else if (size == 0)
        {
            boxSize = limit - start;
        }
        else
        {
            boxSize = size;
        }

        if (boxSize < headerSize) return false;

        var end = start + boxSize;
        if (end < start) end = limit;
        end = Math.Min(end, limit);
        if (end < start + headerSize) return false;

        box = new Box(type, start + headerSize, end);
        return true;
    }

    private static bool TryReadEbmlId(Stream stream, out ulong value) => TryReadEbmlVint(stream, stripMarker: false, out value, out _);

    private static bool TryReadEbmlSize(Stream stream, out ulong value) => TryReadEbmlVint(stream, stripMarker: true, out value, out _);

    private static bool TryReadEbmlVint(Stream stream, bool stripMarker, out ulong value, out int length)
    {
        value = 0;
        length = 0;

        var first = stream.ReadByte();
        if (first < 0) return false;

        var width = 1;
        var mask = 0x80;
        while (width <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            width++;
        }

        if (width > 8) return false;

        value = (ulong)(stripMarker ? first & (mask - 1) : first);
        for (var i = 1; i < width; i++)
        {
            var next = stream.ReadByte();
            if (next < 0) return false;
            value = (value << 8) | (byte)next;
        }

        if (stripMarker)
        {
            // A vint's payload is 7 bits per byte, so the all-ones value is the
            // unknown-size marker. For width 8 that is 0x00FFFFFFFFFFFFFF.
            var payloadBits = width * 7;
            var maxSize = (1UL << payloadBits) - 1;
            if (value >= maxSize) value = EbmlUnknownSize;
        }

        length = width;
        return true;
    }

    private static long EndOf(long bodyStart, ulong size, long limit = long.MaxValue)
    {
        if (size == EbmlUnknownSize) return Math.Min(limit, long.MaxValue);
        if (size > (ulong)(long.MaxValue - bodyStart)) return Math.Min(limit, long.MaxValue);
        return Math.Min(limit, bodyStart + (long)size);
    }

    private static bool TryReadUInt32(Stream stream, out uint value)
    {
        value = 0;
        Span<byte> buffer = stackalloc byte[4];
        if (!ReadExactly(stream, buffer)) return false;
        value = (uint)((buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3]);
        return true;
    }

    private static bool TryReadUInt64(Stream stream, out ulong value)
    {
        value = 0;
        Span<byte> buffer = stackalloc byte[8];
        if (!ReadExactly(stream, buffer)) return false;
        for (var i = 0; i < 8; i++) value = (value << 8) | buffer[i];
        return true;
    }

    private static bool TryReadUIntBE(Stream stream, int byteCount, out ulong value)
    {
        value = 0;
        if (byteCount is <= 0 or > 8) return false;

        Span<byte> buffer = stackalloc byte[8];
        if (!ReadExactly(stream, buffer[..byteCount])) return false;
        for (var i = 0; i < byteCount; i++) value = (value << 8) | buffer[i];
        return true;
    }

    private static bool TryReadSingle(Stream stream, out double value)
    {
        value = 0;
        Span<byte> buffer = stackalloc byte[4];
        if (!ReadExactly(stream, buffer)) return false;

        // EBML floats are big-endian, which is .NET's default binary layout.
        if (BitConverter.IsLittleEndian) buffer.Reverse();
        value = BitConverter.Int32BitsToSingle(MemoryMarshal.Read<int>(buffer));
        return true;
    }

    private static bool TryReadDouble(Stream stream, out double value)
    {
        value = 0;
        Span<byte> buffer = stackalloc byte[8];
        if (!ReadExactly(stream, buffer)) return false;

        // EBML floats are big-endian, which is .NET's default binary layout.
        if (BitConverter.IsLittleEndian) buffer.Reverse();
        value = BitConverter.Int64BitsToDouble(MemoryMarshal.Read<long>(buffer));
        return true;
    }

    // Stream.Read may return fewer bytes than requested; seeking past a large
    // box makes short reads likely, so every fixed-width read must loop.
    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0) return false;
            total += read;
        }

        return true;
    }

    private static uint FourCc(string value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

    private static int ToMilliseconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        if (value <= 0 || value >= int.MaxValue) return 0;
        return (int)Math.Round(value);
    }

    private readonly record struct Box(uint Type, long BodyStart, long BodyEnd);
}
