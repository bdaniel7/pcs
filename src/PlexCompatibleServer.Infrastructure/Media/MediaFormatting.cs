namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Plex-shaped formatting of raw media dimensions and rates. Kept in Infrastructure so the
/// repository/probes and the API mapper derive identical values from the same fields.
/// </summary>
public static class MediaFormatting
{
    /// <summary>
    /// H.264 level from the picture size, using the level table's MaxFS/MBPS limits rather than
    /// raw height: a 1920x1080 frame is 8160 macroblocks and is level 4.0, not 5.1.
    /// </summary>
    public static int H264Level(int width, int height, double frameRate)
    {
        var w = width > 0 ? width : 1920;
        var h = height > 0 ? height : 1080;
        var fps = frameRate > 0 ? frameRate : 24d;

        var frameSize = (long)Math.Ceiling(w / 16d) * (long)Math.Ceiling(h / 16d);
        var macroblocksPerSecond = frameSize * (long)Math.Ceiling(fps);

        if (frameSize <= 1620 && macroblocksPerSecond <= 40500) return 30;
        if (frameSize <= 3600 && macroblocksPerSecond <= 108000) return 31;
        if (frameSize <= 8192 && macroblocksPerSecond <= 245760) return 40;
        if (frameSize <= 8192) return 41;
        if (frameSize <= 22080) return 50;
        return 51;
    }

    public static string AspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "";
        var ratio = Math.Round((double)width / height, 2);

        return ratio switch
        {
            >= 2.20 => "2.35",
            >= 1.75 => "1.85",
            >= 1.60 => "1.78",
            >= 1.45 => "1.5",
            _ => ((decimal)ratio).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Plex labels a video by its horizontal resolution, not the vertical one. A 1920x800
    /// letterboxed feature is 1080p: matching the 1920-sample reference against the official server
    /// shows videoResolution="1080" and displayTitle="1080p (H.264)". Deriving it from height
    /// labels the same file 720p, which is what the detail screen then shows.
    /// </summary>
    public static string VideoResolution(int width) => width switch
    {
        >= 3000 => "2160",
        >= 2200 => "1440",
        >= 1800 => "1080",
        >= 1200 => "720",
        >= 900 => "576",
        > 0 => "480",
        _ => ""
    };

    public static string FrameRate(double rate)
    {
        if (rate <= 0) return "";

        // Plex labels 23.976 as "24p", rounding up rather than emitting a decimal.
        var rounded = Math.Round(rate, MidpointRounding.AwayFromZero);
        return rounded >= 1 ? $"{rounded:0}p" : "";
    }

    /// <summary>Raw frame rate for the per-stream frameRate attribute, e.g. 23.976.</summary>
    public static string StreamFrameRate(double rate)
        => rate <= 0
            ? ""
            : rate.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Plex publishes a bandwidth ladder the client picks from; 8 descending points ending at the
    /// stream's own bitrate is the shape the real server emits.
    /// </summary>
    public static string Bandwidths(int bitrateKbps)
    {
        if (bitrateKbps <= 0) return "";
        var ladder = new List<string>();
        var value = bitrateKbps * 1.25;

        for (var i = 0; i < 4; i++)
        {
            ladder.Add(((int)Math.Round(value)).ToString());
            value *= 0.75;
        }

        var floor = Math.Max(1, bitrateKbps / 5);
        for (var i = 0; i < 4; i++) ladder.Add(floor.ToString());

        return string.Join(",", ladder);
    }
}
