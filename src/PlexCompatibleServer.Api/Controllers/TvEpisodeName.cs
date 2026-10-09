using System.Text.RegularExpressions;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Show/season/episode identity parsed from an episode filename.
/// </summary>
internal sealed class ParsedEpisodeName
{
    public string ShowName { get; set; } = "";
    public int Season { get; set; }
    public int Episode { get; set; }
    public string EpisodeTitle { get; set; } = "";
}

/// <summary>
/// Parses the scene naming conventions TV libraries live on: "Show S02E05 Title 1080p ..." and
/// "show.name.1x05.title.720p". plex.tv show search needs the name without the release junk, so
/// the show part is normalised to plain words while the episode title stops at the first release
/// token. When the filename carries no episode title (the common scene case of
/// "s02e01.1080p.web..."), <see cref="ParsedEpisodeName.EpisodeTitle"/> stays empty and the caller
/// falls back to the title from plex.tv or "Episode N".
/// </summary>
internal static class TvEpisodeName
{
    private static readonly Regex SeasonEpisodePattern = new(
        @"(?<![A-Za-z0-9])S(\d{1,2})[\s._-]*E(\d{1,3})(?:[\s._-]*E\d{1,3})*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NxPattern = new(
        @"(?<![A-Za-z0-9])(\d{1,2})x(\d{2,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] Extensions = MediaTokens.FilenameExtensions;

    public static ParsedEpisodeName? Parse(string filePath)
    {
        var file = filePath;
        try { file = Path.GetFileName(file); } catch { }
        for (var i = 0; i < 3; i++)
        {
            var lower = file.ToLowerInvariant();
            var stripped = false;
            foreach (var e in Extensions)
            {
                if (lower.EndsWith(e))
                {
                    file = Path.GetFileNameWithoutExtension(file);
                    stripped = true;
                    break;
                }
            }
            if (!stripped) break;
        }

        var match = SeasonEpisodePattern.Match(file);
        if (!match.Success) match = NxPattern.Match(file);
        if (!match.Success) return null;

        var showPart = file[..match.Index].TrimEnd(' ', '.', '_', '-');
        showPart = Regex.Replace(showPart, @"\[[^\]]*\]", " ");
        showPart = Regex.Replace(showPart, @"[._+]+", " ");
        showPart = Regex.Replace(showPart, @"\s+", " ").Trim();
        if (showPart.Length == 0) return null;

        var rest = file[(match.Index + match.Length)..];
        return new ParsedEpisodeName
        {
            ShowName = showPart,
            Season = int.Parse(match.Groups[1].Value),
            Episode = int.Parse(match.Groups[2].Value),
            EpisodeTitle = CleanEpisodeTitle(rest),
        };
    }

    /// <summary>
    /// Takes the text after the SxxExx marker up to the first release token. The cut set is kept
    /// deliberately strict (resolutions, codecs, containers) so an episode legitimately titled
    /// "A Web of Lies" survives while "Daddy Issues 1080p ATVP WEB-DL ..." yields "Daddy Issues".
    /// </summary>
    private static string CleanEpisodeTitle(string rest)
    {
        var keep = new List<string>();
        foreach (var token in Regex.Split(rest, "[^A-Za-z0-9]+"))
        {
            if (token.Length == 0) continue;
            if (MediaTokens.IsReleaseToken(token)) break;
            if (keep.Count > 0 && IsYearToken(token)) break;
            keep.Add(token);
        }
        return string.Join(' ', keep);
    }

    private static bool IsYearToken(string t) =>
        t.Length == 4 && int.TryParse(t, out var y) && y >= 1900 && y <= 2100;
}
