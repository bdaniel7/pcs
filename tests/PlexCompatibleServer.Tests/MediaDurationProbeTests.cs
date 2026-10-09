using System.Text;
using NUnit.Framework;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Tests;

public sealed class MediaDurationProbeTests
{
    private string directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "plex-duration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Test]
    public void ReadsMatroskaDuration()
    {
        var path = Path.Combine(directory, "movie.mkv");
        writeMatroska(path, timecodeScale: 1_000_000, durationTicks: 3_661_000);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsMatroskaDurationWithNonDefaultTimecodeScale()
    {
        var path = Path.Combine(directory, "movie.mkv");
        writeMatroska(path, timecodeScale: 100_000, durationTicks: 73_220_000);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(7_322_000));
    }

    [Test]
    public void ReadsMatroskaFloat32Duration()
    {
        var path = Path.Combine(directory, "movie.mkv");
        writeMatroska(path, timecodeScale: 1_000_000, durationTicks: 3_661_000, payloadSize: 4);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReturnsNullForMatroskaWithoutDuration()
    {
        var path = Path.Combine(directory, "live.mkv");
        writeMatroskaWithoutDuration(path);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    [Test]
    public void ReadsMatroskaDurationAfterSeekHeadWithMidRangeSize()
    {
        // Real-world files start with a SeekHead whose size lands in 64..126,
        // which is close to the unknown-size marker and must not be mistaken
        // for one, otherwise the walk skips Info and returns null.
        var path = Path.Combine(directory, "seekhead.mkv");
        writeMatroskaWithSeekHead(path, timecodeScale: 1_000_000, durationTicks: 3_661_000, seekHeadSize: 79);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsIsoBaseMediaWhenMoovFollowsLargeMdat()
    {
        // Streaming-friendly muxers place moov after mdat, far past any small
        // header window, so the walk must not stop at a size cap.
        var path = Path.Combine(directory, "moov-last.mp4");
        writeIsoBaseMedia(path, version: 0, timescale: 1000, duration: 2_842_176, moovLast: true);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(2_842_176));
    }

    [Test]
    public void ReadsMatroskaBigEndianDurationBytes()
    {
        // EBML floats are big-endian; a little-endian interpretation of the
        // same bytes yields a wildly wrong value.
        var path = Path.Combine(directory, "endian.mkv");
        writeMatroska(path, timecodeScale: 1_000_000, durationTicks: 3_611.3125, payloadSize: 4);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_611));
    }

    [Test]
    public void ReadsIsoBaseMediaWithEmptyFreeBoxBeforeMdat()
    {
        // An 8-byte 'free' box has no payload; treating a zero-length body as a
        // parse failure aborts the walk before 'moov' is reached.
        var path = Path.Combine(directory, "free-box.mp4");
        var header = new byte[20];
        header[0] = 0;
        writeUInt32(header, 4, 0);
        writeUInt32(header, 8, 0);
        writeUInt32(header, 12, 1000);
        writeUInt32(header, 16, 2_842_176);

        var file = new MemoryStream();
        file.Write(box("ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0, 0, 2, 0 }));
        file.Write(box("free", Array.Empty<byte>()));
        file.Write(box("mdat", new byte[256]));
        file.Write(box("moov", box("mvhd", header)));

        File.WriteAllBytes(path, file.ToArray());

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(2_842_176));
    }

    [Test]
    public void ReadsIsoBaseMediaVersionZeroDuration()
    {
        var path = Path.Combine(directory, "movie.mp4");
        writeIsoBaseMedia(path, version: 0, timescale: 1000, duration: 3_661_000);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsIsoBaseMediaVersionOneDuration()
    {
        var path = Path.Combine(directory, "movie.mp4");
        writeIsoBaseMedia(path, version: 1, timescale: 600, duration: 2_196_600);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public void ReadsIsoBaseMediaWithMoovAfterMdat()
    {
        var path = Path.Combine(directory, "movie.m4v");
        writeIsoBaseMedia(path, version: 0, timescale: 90000, duration: 329_490_000, moovLast: true);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.EqualTo(3_661_000));
    }

    [Test]
    public async Task Async_probe_matches_sync_probe()
    {
        var path = Path.Combine(directory, "async.mp4");
        writeIsoBaseMedia(path, version: 0, timescale: 1000, duration: 3_661_000);

        Assert.That(await MediaDurationProbe.GetDurationMsAsync(path, CancellationToken.None), Is.EqualTo(3_661_000));
        Assert.That(await MediaDurationProbe.GetDurationMsAsync(
            Path.Combine(directory, "missing.mp4"), CancellationToken.None), Is.Null);
    }

    [Test]
    public void ReturnsNullForUnknownContainer()
    {
        var path = Path.Combine(directory, "video.avi");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 4096)));

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    [Test]
    public void ReturnsNullForTruncatedFile()
    {
        var path = Path.Combine(directory, "truncated.mp4");
        var full = buildIsoBaseMedia(version: 0, timescale: 1000, duration: 3_661_000, moovLast: false);
        File.WriteAllBytes(path, full.Take(20).ToArray());

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    [Test]
    public void ReturnsNullForMissingFile()
    {
        Assert.That(MediaDurationProbe.GetDurationMs(Path.Combine(directory, "nope.mp4")), Is.Null);
    }

    [Test]
    public void ReturnsNullForZeroDuration()
    {
        var path = Path.Combine(directory, "empty.mp4");
        writeIsoBaseMedia(path, version: 0, timescale: 1000, duration: 0);

        Assert.That(MediaDurationProbe.GetDurationMs(path), Is.Null);
    }

    private static void writeIsoBaseMedia(string path, byte version, uint timescale, ulong duration, bool moovLast = false)
    {
        File.WriteAllBytes(path, buildIsoBaseMedia(version, timescale, duration, moovLast));
    }

    private static byte[] buildIsoBaseMedia(byte version, uint timescale, ulong duration, bool moovLast)
    {
        var fileType = box("ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00 });

        var wide = version == 1;
        var movieHeader = new byte[wide ? 32 : 20];
        movieHeader[0] = version;

        if (wide)
        {
            writeUInt64(movieHeader, 4, 0);
            writeUInt64(movieHeader, 12, 0);
            writeUInt32(movieHeader, 20, timescale);
            writeUInt64(movieHeader, 24, duration);
        }
        else
        {
            writeUInt32(movieHeader, 4, 0);
            writeUInt32(movieHeader, 8, 0);
            writeUInt32(movieHeader, 12, timescale);
            writeUInt32(movieHeader, 16, (uint)duration);
        }

        var movie = box("moov", box("mvhd", movieHeader));
        var media = box("mdat", new byte[512]);

        return moovLast
            ? [.. fileType, .. media, .. movie]
            : [.. fileType, .. movie, .. media];
    }

    private static void writeMatroska(string path, ulong timecodeScale, double durationTicks, int payloadSize = 8)
    {
        var scale = new byte[8];
        writeUInt64(scale, 0, timecodeScale);

        var duration = payloadSize == 4
            ? toBigEndian(BitConverter.GetBytes((float)durationTicks))
            : toBigEndian(BitConverter.GetBytes(durationTicks));

        var info = new MemoryStream();
        writeEbmlElement(info, [0x2A, 0xD7, 0xB1], scale);
        writeEbmlElement(info, [0x44, 0x89], duration);

        var segment = new MemoryStream();
        writeEbmlElement(segment, [0x15, 0x49, 0xA9, 0x66], info.ToArray());

        var file = new MemoryStream();
        writeEbmlElement(file, [0x1A, 0x45, 0xDF, 0xA3], [0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D]);
        writeEbmlElement(file, [0x18, 0x53, 0x80, 0x67], segment.ToArray());

        File.WriteAllBytes(path, file.ToArray());
    }

    private static void writeMatroskaWithSeekHead(string path, ulong timecodeScale, double durationTicks, int seekHeadSize)
    {
        var scale = new byte[8];
        writeUInt64(scale, 0, timecodeScale);

        var info = new MemoryStream();
        writeEbmlElement(info, [0x2A, 0xD7, 0xB1], scale);
        writeEbmlElement(info, [0x44, 0x89], toBigEndian(BitConverter.GetBytes(durationTicks)));

        var segment = new MemoryStream();
        // SeekHead first, sized in the range that must not read as unknown-size.
        writeEbmlElement(segment, [0x11, 0x4D, 0x9B, 0x74], new byte[seekHeadSize]);
        writeEbmlElement(segment, [0x15, 0x49, 0xA9, 0x66], info.ToArray());

        var file = new MemoryStream();
        writeEbmlElement(file, [0x1A, 0x45, 0xDF, 0xA3], [0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D]);
        writeEbmlElement(file, [0x18, 0x53, 0x80, 0x67], segment.ToArray());

        File.WriteAllBytes(path, file.ToArray());
    }

    private static void writeMatroskaWithoutDuration(string path)
    {
        var scale = new byte[8];
        writeUInt64(scale, 0, 1_000_000);

        var info = new MemoryStream();
        writeEbmlElement(info, [0x2A, 0xD7, 0xB1], scale);

        var segment = new MemoryStream();
        writeEbmlElement(segment, [0x15, 0x49, 0xA9, 0x66], info.ToArray());

        var file = new MemoryStream();
        writeEbmlElement(file, [0x1A, 0x45, 0xDF, 0xA3], [0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D]);
        writeEbmlElement(file, [0x18, 0x53, 0x80, 0x67], segment.ToArray());

        File.WriteAllBytes(path, file.ToArray());
    }

    private static byte[] toBigEndian(byte[] bytes)
    {
        if (!BitConverter.IsLittleEndian) return bytes;
        var copy = (byte[])bytes.Clone();
        Array.Reverse(copy);
        return copy;
    }

    private static byte[] box(string type, byte[] payload)
    {
        var size = 8 + payload.Length;
        var buffer = new byte[size];
        writeUInt32(buffer, 0, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
        payload.CopyTo(buffer, 8);
        return buffer;
    }

    private static void writeEbmlElement(Stream stream, byte[] id, byte[] payload)
    {
        stream.Write(id);
        writeEbmlSize(stream, (ulong)payload.Length);
        stream.Write(payload);
    }

    private static void writeEbmlSize(Stream stream, ulong value)
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

    private static void writeUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void writeUInt64(byte[] buffer, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
            buffer[offset + i] = (byte)(value >> (56 - i * 8));
    }
}
