namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Controls how posters and background art are produced for media that has no sidecar image.
/// </summary>
public sealed class MediaArtOptions
{
    /// <summary>
    /// Path to ffmpeg. When empty the generator probes <c>PLEX_FFMPEG</c>, the current directory,
    /// the usual download folders and then PATH. Artwork generation is skipped when it finds none.
    /// </summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Where generated posters and art are cached. Defaults to a folder next to the database.</summary>
    public string CacheDirectory { get; set; } = "";

    /// <summary>Set to false to serve media without any generated artwork.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How far into each file to grab the still from. Plex skips past the black frames that
    /// open most encodes, so sampling at 10% of the runtime beats a fixed offset.
    /// </summary>
    public double FrameSeekFraction { get; set; } = 0.1;

    /// <summary>Hard ceiling on how many posters may be extracted concurrently.</summary>
    public int MaxConcurrency { get; set; } = 2;

    public static string DefaultCacheDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlexCompatibleServer",
            "thumbs");
}
