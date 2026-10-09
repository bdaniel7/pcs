using System.Text.RegularExpressions;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Shared film-name token tables. Centralised so the scanner, the plex.tv search-title builder and
/// the episode-title parser cannot drift apart on what counts as a video file or as release junk.
/// </summary>
public static class MediaTokens
{
    /// <summary>Extensions the scanner indexes: playable containers placed on disk.</summary>
    public static readonly IReadOnlySet<string> ScanExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm"
        };

    /// <summary>
    /// Extensions stripped while turning a filename into a title. Wider than <see cref="ScanExtensions"/>:
    /// disc images and legacy containers still appear in release names even when not scanned.
    /// </summary>
    public static readonly string[] FilenameExtensions =
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv", ".iso", ".webm", ".flv" };

    // Search titles are cut at the first quality token, so this set may include generic web/source
    // words ("web", "dl", "yts") that the stricter episode-title set below must not treat as junk.
    private static readonly Regex SearchQualityTokens = new(
        "^(1080p|2160p|720p|480p|webrip|webdl|web|dl|bluray|brrip|bdremux|x264|x265|h264|h265|hevc|remux|aac|ac3|eac3|dts|ddp|atmos|truehd|hdr|sdr|proper|repack|limited|unrated|multi|dual|subbed|dubbed|imax|10bit|8bit|yts|yify|gg|bz)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Deliberately strict (resolutions, codecs, containers only) so an episode legitimately titled
    // "A Web of Lies" survives while "Daddy Issues 1080p ATVP WEB-DL ..." yields "Daddy Issues".
    private static readonly Regex ReleaseTokens = new(
        "^(1080p|2160p|720p|480p|576p|webrip|webdl|bluray|brrip|bdremux|remux|x264|x265|h264|h265|hevc|avc|aac|ac3|eac3|dts|ddp|atmos|truehd|hdr|hdr10|sdr|dv|proper|repack|internal|limited|extended|unrated|hdtv|dvdrip|10bit|8bit|imax|multi|dual|subbed|dubbed|amzn|atvp|netflix|hulu)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSearchQualityToken(string token) => SearchQualityTokens.IsMatch(token);

    public static bool IsReleaseToken(string token) => ReleaseTokens.IsMatch(token);

    /// <summary>
    /// Looser matcher for normalising a filename into a sidecar key: matches release tokens anywhere
    /// in the string and tolerates separators, e.g. "web-dl", "dd+", "aac5.1", "dts-hd", "hdr10+".
    /// </summary>
    public static readonly Regex QualityTokens = new(
        @"\b(1080p|2160p|720p|480p|webrip|web|web-dl|webdl|bluray|brrip|bdremux|x264|x265|h264|h265|hevc|aac\d?(?:\.\d)?|ac3|eac3|dts(-hd)?|dd\+?|ddp?|atmos|truehd|hdr10\+?|hdr|sdr|remux|proper|repack|internal|limited|extended|unrated|multi|dual|subbed|dubbed|imax|10bit|8bit|yify|yts|retail|dksubs|gg|bz|lt)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
