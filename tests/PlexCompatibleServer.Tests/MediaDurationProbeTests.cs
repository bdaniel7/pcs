using System.Text;
using NUnit.Framework;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Tests;

public sealed class MediaDurationProbeTests
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "plex-duration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void ReadsMatroskaDuration()
    {
        var path = Path.Combine(_directory, "movie.mkv");
        WriteMatroska(path, timecodeScale: 1_000_000, durationTicks: 3_661_000);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsMatroskaDurationWithNonDefaultTimecodeScale()
    {
        var path = Path.Combine(_directory, "movie.mkv");
        WriteMatroska(path, timecodeScale: 100_000, durationTicks: 73_220_000);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(7_322_000));
    }

    [Test]
    public void ReadsMatroskaFloat32Duration()
    {
        var path = Path.Combine(_directory, "movie.mkv");
        WriteMatroska(path, timecodeScale: 1_000_000, durationTicks: 3_661_000, payloadSize: 4);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReturnsNullForMatroskaWithoutDuration()
    {
        var path = Path.Combine(_directory, "live.mkv");
        WriteMatroskaWithoutDuration(path);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    [Test]
    public void ReadsMatroskaDurationAfterSeekHeadWithMidRangeSize()
    {
        // Real-world files start with a SeekHead whose size lands in 64..126,
        // which is close to the unknown-size marker and must not be mistaken
        // for one, otherwise the walk skips Info and returns null.
        var path = Path.Combine(_directory, "seekhead.mkv");
        WriteMatroskaWithSeekHead(path, timecodeScale: 1_000_000, durationTicks: 3_661_000, seekHeadSize: 79);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsIsoBaseMediaWhenMoovFollowsLargeMdat()
    {
        // Streaming-friendly muxers place moov after mdat, far past any small
        // header window, so the walk must not stop at a size cap.
        var path = Path.Combine(_directory, "moov-last.mp4");
        WriteIsoBaseMedia(path, version: 0, timescale: 1000, duration: 2_842_176, moovLast: true);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(2_842_176));
    }

    [Test]
    public void ReadsMatroskaBigEndianDurationBytes()
    {
        // EBML floats are big-endian; a little-endian interpretation of the
        // same bytes yields a wildly wrong value.
        var path = Path.Combine(_directory, "endian.mkv");
        WriteMatroska(path, timecodeScale: 1_000_000, durationTicks: 3_611.3125, payloadSize: 4);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_611));
    }

    [Test]
    public void ReadsIsoBaseMediaWithEmptyFreeBoxBeforeMdat()
    {
        // An 8-byte 'free' box has no payload; treating a zero-length body as a
        // parse failure aborts the walk before 'moov' is reached.
        var path = Path.Combine(_directory, "free-box.mp4");
        var header = new byte[20];
        header[0] = 0;
        WriteUInt32(header, 4, 0);
        WriteUInt32(header, 8, 0);
        WriteUInt32(header, 12, 1000);
        WriteUInt32(header, 16, 2_842_176);

        var file = new MemoryStream();
        file.Write(Box("ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0, 0, 2, 0 }));
        file.Write(Box("free", Array.Empty<byte>()));
        file.Write(Box("mdat", new byte[256]));
        file.Write(Box("moov", Box("mvhd", header)));

        File.WriteAllBytes(path, file.ToArray());

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(2_842_176));
    }

    [Test]
    public void ReadsIsoBaseMediaVersionZeroDuration()
    {
        var path = Path.Combine(_directory, "movie.mp4");
        WriteIsoBaseMedia(path, version: 0, timescale: 1000, duration: 3_661_000);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsIsoBaseMediaVersionOneDuration()
    {
        var path = Path.Combine(_directory, "movie.mp4");
        WriteIsoBaseMedia(path, version: 1, timescale: 600, duration: 2_196_600);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsIsoBaseMediaWithMoovAfterMdat()
    {
        var path = Path.Combine(_directory, "movie.m4v");
        WriteIsoBaseMedia(path, version: 0, timescale: 90000, duration: 329_490_000, moovLast: true);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReturnsNullForUnknownContainer()
    {
        var path = Path.Combine(_directory, "video.avi");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 4096)));

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    [Test]
    public void ReturnsNullForTruncatedFile()
    {
        var path = Path.Combine(_directory, "truncated.mp4");
        var full = BuildIsoBaseMedia(version: 0, timescale: 1000, duration: 3_661_000, moovLast: false);
        File.WriteAllBytes(path, full.Take(20).ToArray());

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    [Test]
    public void ReturnsNullForMissingFile()
    {
        Assert.That(MediaDurationProbe.GetDurationMs(Path.Combine(_directory, "nope.mp4")), Is.Null);
    }

    [Test]
    public void ReturnsNullForZeroDuration()
    {
        var path = Path.Combine(_directory, "empty.mp4");
        WriteIsoBaseMedia(path, version: 0, timescale: 1000, duration: 0);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    private static void WriteIsoBaseMedia(string path, byte version, uint timescale, ulong duration, bool moovLast = false)
    {
        File.WriteAllBytes(path, BuildIsoBaseMedia(version, timescale, duration, moovLast));
    }

    private static byte[] BuildIsoBaseMedia(byte version, uint timescale, ulong duration, bool moovLast)
    {
        var fileType = Box("ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00 });

        var wide = version == 1;
        var movieHeader = new byte[wide ? 32 : 20];
        movieHeader[0] = version;

        if (wide)
        {
            WriteUInt64(movieHeader, 4, 0);
            WriteUInt64(movieHeader, 12, 0);
            WriteUInt32(movieHeader, 20, timescale);
            WriteUInt64(movieHeader, 24, duration);
        }
        else
        {
            WriteUInt32(movieHeader, 4, 0);
            WriteUInt32(movieHeader, 8, 0);
            WriteUInt32(movieHeader, 12, timescale);
            WriteUInt32(movieHeader, 16, (uint)duration);
        }

        var movie = Box("moov", Box("mvhd", movieHeader));
        var media = Box("mdat", new byte[512]);

        return moovLast
            ? [.. fileType, .. media, .. movie]
            : [.. fileType, .. movie, .. media];
    }

    private static void WriteMatroska(string path, ulong timecodeScale, double durationTicks, int payloadSize = 8)
    {
        var scale = new byte[8];
        WriteUInt64(scale, 0, timecodeScale);

        var duration = payloadSize == 4
            ? ToBigEndian(BitConverter.GetBytes((float)durationTicks))
            : ToBigEndian(BitConverter.GetBytes(durationTicks));

        var info = new MemoryStream();
        WriteEbmlElement(info, [0x2A, 0xD7, 0xB1], scale);
        WriteEbmlElement(info, [0x44, 0x89], duration);

        var segment = new MemoryStream();
        WriteEbmlElement(segment, [0x15, 0x49, 0xA9, 0x66], info.ToArray());

        var file = new MemoryStream();
        WriteEbmlElement(file, [0x1A, 0x45, 0xDF, 0xA3], [0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D]);
        WriteEbmlElement(file, [0x18, 0x53, 0x80, 0x67], segment.ToArray());

        File.WriteAllBytes(path, file.ToArray());
    }

    private static void WriteMatroskaWithSeekHead(string path, ulong timecodeScale, double durationTicks, int seekHeadSize)
    {
        var scale = new byte[8];
        WriteUInt64(scale, 0, timecodeScale);

        var info = new MemoryStream();
        WriteEbmlElement(info, [0x2A, 0xD7, 0xB1], scale);
        WriteEbmlElement(info, [0x44, 0x89], ToBigEndian(BitConverter.GetBytes(durationTicks)));

        var segment = new MemoryStream();
        // SeekHead first, sized in the range that must not read as unknown-size.
        WriteEbmlElement(segment, [0x11, 0x4D, 0x9B, 0x74], new byte[seekHeadSize]);
        WriteEbmlElement(segment, [0x15, 0x49, 0xA9, 0x66], info.ToArray());

        var file = new MemoryStream();
        WriteEbmlElement(file, [0x1A, 0x45, 0xDF, 0xA3], [0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D]);
        WriteEbmlElement(file, [0x18, 0x53, 0x80, 0x67], segment.ToArray());

        File.WriteAllBytes(path, file.ToArray());
    }

    private static void WriteMatroskaWithoutDuration(string path)
    {
        var scale = new byte[8];
        WriteUInt64(scale, 0, 1_000_000);

        var info = new MemoryStream();
        WriteEbmlElement(info, [0x2A, 0xD7, 0xB1], scale);

        var segment = new MemoryStream();
        WriteEbmlElement(segment, [0x15, 0x49, 0xA9, 0x66], info.ToArray());

        var file = new MemoryStream();
        WriteEbmlElement(file, [0x1A, 0x45, 0xDF, 0xA3], [0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D]);
        WriteEbmlElement(file, [0x18, 0x53, 0x80, 0x67], segment.ToArray());

        File.WriteAllBytes(path, file.ToArray());
    }

    private static byte[] ToBigEndian(byte[] bytes)
    {
        if (!BitConverter.IsLittleEndian) return bytes;
        var copy = (byte[])bytes.Clone();
        Array.Reverse(copy);
        return copy;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var size = 8 + payload.Length;
        var buffer = new byte[size];
        WriteUInt32(buffer, 0, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
        payload.CopyTo(buffer, 8);
        return buffer;
    }

    private static void WriteEbmlElement(Stream stream, byte[] id, byte[] payload)
    {
        stream.Write(id);
        WriteEbmlSize(stream, (ulong)payload.Length);
        stream.Write(payload);
    }

    private static void WriteEbmlSize(Stream stream, ulong value)
    {
        if (value < 0x7F)
        {
            stream.WriteByte((byte)(0x80 | value));
        }
        else if (value < 0x3FFF)
        {
            stream.WriteByte((byte)(0x40 | (value >> 8)));
            stream.WriteByte((byte)(value & 0xFF));
        }
        else
        {
            stream.WriteByte((byte)(0x20 | (value >> 16)));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
            buffer[offset + i] = (byte)(value >> (56 - i * 8));
    }
}
