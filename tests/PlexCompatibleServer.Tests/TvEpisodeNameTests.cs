using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class TvEpisodeNameTests
{
    [TestCase(@"G:\Series\Slow Horses S06E02 Daddy Issues 1080p ATVP WEB-DL DDP5 1 H 264-NTb.mkv",
        "Slow Horses", 6, 2, "Daddy Issues")]
    [TestCase(@"G:\Series\dark.matter.s02e01.1080p.web.h264-cakes[EZTVx.to].mkv",
        "dark matter", 2, 1, "")]
    [TestCase(@"G:\Series\dark.matter.S02E05 Love and Be Loved 1080p ATVP WEB-DL DDP5 1 Atmos H 264-FLUX[eztv].mkv",
        "dark matter", 2, 5, "Love and Be Loved")]
    [TestCase(@"G:\Series\The.War.Between.the.Land.and.the.Sea.S01E02.Plastic.Apocalypse.1080p.iP.WEB-DL.AAC5.1.H.264-NTb.mkv",
        "The War Between the Land and the Sea", 1, 2, "Plastic Apocalypse")]
    [TestCase(@"G:\Series\Some.Show.1x02.The.Pilot.720p.mkv", "Some Show", 1, 2, "The Pilot")]
    [TestCase(@"G:\Series\The.Show.S01E01E02.Twin.720p.mkv", "The Show", 1, 1, "Twin")]
    [TestCase(@"G:\Series\A.Web.of.Lies.S01E03.A.Web.of.Lies.1080p.mkv",
        "A Web of Lies", 1, 3, "A Web of Lies")]
    public void Parse_extracts_show_season_episode_and_title(string path, string show, int season,
        int episode, string title)
    {
        var parsed = TvEpisodeName.Parse(path);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.ShowName, Is.EqualTo(show));
        Assert.That(parsed.Season, Is.EqualTo(season));
        Assert.That(parsed.Episode, Is.EqualTo(episode));
        Assert.That(parsed.EpisodeTitle, Is.EqualTo(title));
    }

    [TestCase(@"G:\Movies\Some.Movie.2024.1080p.mkv")]
    [TestCase(@"G:\Series\S01E01.Pilot.mkv")]
    [TestCase("")]
    public void Parse_returns_null_without_show_or_episode_marker(string path) =>
        Assert.That(TvEpisodeName.Parse(path), Is.Null);
}
