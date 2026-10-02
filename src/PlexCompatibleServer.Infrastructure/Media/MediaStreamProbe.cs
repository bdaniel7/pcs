using System.Diagnostics;
using System.Text.Json;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>A single elementary stream inside a media file.</summary>
public sealed class MediaStreamInfo
{
    /// <summary>1 = video, 2 = audio, 3 = subtitle, matching Plex's streamType values.</summary>
    public int StreamType { get; set; }
    public string? Codec { get; set; }

    // ffprobe omits these per stream kind (an audio row has no width, a subtitle row has no
    // frame rate), so they must survive a JSON null rather than throw during deserialization.
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Channels { get; set; }
    public int? Bitrate { get; set; }
    public double? FrameRate { get; set; }
    public int? SamplingRate { get; set; }
    public string? LanguageCode { get; set; }
    public string? Language { get; set; }
    /// <summary>"direct" for streams muxed into the file, "sidecar-subs" for external subtitle files.</summary>
    public string? Location { get; set; }
}

/// <summary>Container and stream details read out of a media file with ffprobe.</summary>
public sealed class MediaFileInfo
{
    public int DurationMs { get; set; }
    public int Bitrate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? VideoCodec { get; set; }
    public string? VideoProfile { get; set; }
    public string? AudioCodec { get; set; }
    public int AudioChannels { get; set; }
    public double FrameRate { get; set; }
    public string? Container { get; set; }
    public List<MediaStreamInfo> Streams { get; set; } = new();
}

/// <summary>
/// Reads container and stream metadata with ffprobe. The pure-managed duration probe stays as a
/// fallback so scanning still works when ffprobe is unavailable, but ffprobe is what supplies the
/// codec/resolution detail the player needs to decide on direct play.
/// </summary>
public static class MediaStreamProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ResolveFfprobe()
    {
        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return "";
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("PLEX_FFPROBE") ?? "";

        var dir = Path.GetDirectoryName(FfmpegLocator.Executable);
        if (dir is not null) yield return Path.Combine(dir, "ffprobe.exe");

        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(d)) yield return Path.Combine(d, "ffprobe.exe");

        yield return Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "ffprobe.exe");
    }

    public static async Task<MediaFileInfo?> ProbeAsync(string path, CancellationToken ct = default)
    {
        var ffprobe = ResolveFfprobe();
        if (string.IsNullOrEmpty(ffprobe)) return null;

        var info = new ProcessStartInfo(ffprobe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            path
        })
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            await process.WaitForExitAsync(timeout.Token);
            var json = await stdout;
            await errors;

            return Parse(json, path);
        }
        catch (OperationCanceledException)
        {
            return null!;
        }
        catch (Exception ex)
        {
            ProbeFailure = ex.ToString();
            return null!;
        }
    }

    /// <summary>Last probe failure, for diagnostics only.</summary>
    public static string ProbeFailure { get; private set; } = "";

    private static MediaFileInfo Parse(string json, string path)
    {
        if (string.IsNullOrWhiteSpace(json)) return null!;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new MediaFileInfo();

        // ffprobe reports mp4 files as "mov,mp4,m4a,3gp,3g2,mj2"; the first name is not what Plex
        // calls the container, so prefer the actual file extension when the format is a family.
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        if (root.TryGetProperty("format", out var format))
        {
            if (Double(format, "duration") is double seconds)
                result.DurationMs = (int)Math.Round(seconds * 1000);

            result.Bitrate = Int(format, "bit_rate");

            if (format.TryGetProperty("format_name", out var fn) && fn.GetString() is { Length: > 0 } name)
                result.Container = name.Split(',')[0];
        }

        if (extension is "mp4" or "m4v" or "mkv" or "avi" or "ts" or "wmv" or "mov")
            result.Container = extension;

        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var stream in streams.EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
            var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;

            var entry = new MediaStreamInfo
            {
                Codec = codec ?? "",
                Location = "direct"
            };

            switch (type)
            {
                case "video":
                    // Cover art is stored as a video stream by some containers; skip it.
                    if (stream.TryGetProperty("disposition", out var disp) &&
                        disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1)
                    {
                        continue;
                    }

                    entry.StreamType = 1;
                    result.VideoCodec = codec ?? "";
                    result.Width = Int(stream, "width");
                    result.Height = Int(stream, "height");
                    result.FrameRate = FrameRate(stream);
                    entry.Width = result.Width;
                    entry.Height = result.Height;
                    entry.FrameRate = result.FrameRate;
                    entry.Bitrate = Int(stream, "bit_rate");
                    entry.SamplingRate = Int(stream, "sample_rate");

                    var profile = stream.TryGetProperty("profile", out var pr) ? pr.GetString() : null;
                    if (!string.IsNullOrEmpty(profile)) result.VideoProfile = profile;

                    result.Streams.Add(entry);
                    break;

                case "audio":
                    entry.StreamType = 2;
                    entry.Channels = Int(stream, "channels");
                    entry.Bitrate = Int(stream, "bit_rate");
                    entry.SamplingRate = Int(stream, "sample_rate");
                    result.AudioCodec = string.IsNullOrEmpty(result.AudioCodec) ? (codec ?? "") : result.AudioCodec;
                    result.AudioChannels = result.AudioChannels == 0 ? entry.Channels ?? 0 : result.AudioChannels;
                    entry.LanguageCode = Lang(stream, "language");
                    entry.Language = stream.TryGetProperty("tags", out var at) &&
                                     at.TryGetProperty("language", out var al) ? (al.GetString() ?? "") : "";
                    result.Streams.Add(entry);
                    break;

                case "subtitle":
                    entry.StreamType = 3;
                    entry.LanguageCode = Lang(stream, "language");
                    entry.Language = stream.TryGetProperty("tags", out var st) &&
                                     st.TryGetProperty("language", out var sl) ? (sl.GetString() ?? "") : "";
                    result.Streams.Add(entry);
                    break;
            }
        }

        return result;
    }

    private static double Double(JsonElement source, string key)
    {
        if (!source.TryGetProperty(key, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var direct) => direct,
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string Lang(JsonElement stream, string key) =>
        stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty(key, out var v) ? (v.GetString() ?? "") : "";

    /// <summary>ffprobe emits some numeric fields as JSON strings and some as numbers.</summary>
    private static int Int(JsonElement stream, string key)
    {
        if (!stream.TryGetProperty(key, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var direct) => direct,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static double FrameRate(JsonElement stream)
    {
        if (Double(stream, "r_frame_rate") is double value && value > 0)
            return Math.Round(value, 3);

        if (!stream.TryGetProperty("r_frame_rate", out var rf) || rf.GetString() is not { Length: > 0 } text)
            return 0;

        var parts = text.Split('/');
        if (parts.Length != 2) return 0;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var num) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var den) || den == 0)
        {
            return 0;
        }

        return Math.Round(num / den, 3);
    }
}
