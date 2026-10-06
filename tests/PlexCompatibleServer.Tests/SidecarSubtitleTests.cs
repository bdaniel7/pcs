using NUnit.Framework;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// Sidecar discovery, i.e. which .srt files in a folder belong to a given video. The tagged
/// forms ("Movie.en.srt") are what real libraries use, so a movie whose only subtitle is named
/// that way has to come back with a track instead of none.
/// </summary>
[TestFixture]
public class SidecarSubtitleTests
{
    private const string VideoName = "Emily.The.Criminal.2022.1080p.WEBRip.x264.AAC5.1-[YTS.MX].mp4";

    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plex-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Video(string name = VideoName)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    private string Sidecar(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nhello\n");
        return path;
    }

    private static string BaseName(string videoName) => Path.GetFileNameWithoutExtension(videoName);

    [Test]
    public void VideoWithoutSidecarHasNoTracks()
    {
        var video = Video();

        Assert.That(SidecarSubtitles.Find(video), Is.Empty);
    }

    [Test]
    public void UntaggedSidecarIsFound()
    {
        var video = Video();
        var sub = Sidecar(BaseName(VideoName) + ".srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].FilePath, Is.EqualTo(sub));
        Assert.That(found[0].Language, Is.EqualTo("English"));
    }

    [Test]
    public void ContainerQualifiedSidecarIsFound()
    {
        var video = Video();
        var sub = Sidecar(VideoName + ".srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].FilePath, Is.EqualTo(sub));
    }

    [Test]
    public void LanguageTaggedSidecarIsFound()
    {
        var video = Video();
        var sub = Sidecar(BaseName(VideoName) + ".en.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].FilePath, Is.EqualTo(sub));
        Assert.That(found[0].LanguageTag, Is.EqualTo("en"));
        Assert.That(found[0].LanguageCode, Is.EqualTo("eng"));
        Assert.That(found[0].Language, Is.EqualTo("English"));
    }

    [Test]
    public void ThreeLetterCodeIsMappedToItsTag()
    {
        var video = Video();
        Sidecar(BaseName(VideoName) + ".ger.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].LanguageTag, Is.EqualTo("de"));
        Assert.That(found[0].LanguageCode, Is.EqualTo("ger"));
        Assert.That(found[0].Language, Is.EqualTo("German"));
    }

    [Test]
    public void RegionTagKeepsTheRegion()
    {
        var video = Video();
        Sidecar(BaseName(VideoName) + ".pt-BR.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].LanguageTag, Is.EqualTo("pt-BR"));
        Assert.That(found[0].LanguageCode, Is.EqualTo("por"));
    }

    [Test]
    public void UnknownIsoLookingCodeIsPassedThrough()
    {
        var video = Video();
        Sidecar(BaseName(VideoName) + ".xyz.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].LanguageCode, Is.EqualTo("xyz"));
    }

    [Test]
    public void ForcedTrackIsFlaggedAndOrderedLast()
    {
        var video = Video();
        var plain = Sidecar(BaseName(VideoName) + ".srt");
        var forced = Sidecar(BaseName(VideoName) + ".en.forced.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(2));
        Assert.That(found[0].FilePath, Is.EqualTo(plain));
        Assert.That(found[0].Forced, Is.False);
        Assert.That(found[1].FilePath, Is.EqualTo(forced));
        Assert.That(found[1].Forced, Is.True);
        Assert.That(found[1].LanguageTag, Is.EqualTo("en"));
    }

    [Test]
    public void ContainerQualifiedTaggedSidecarIsFound()
    {
        var video = Video();
        var sub = Sidecar(VideoName + ".en.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].FilePath, Is.EqualTo(sub));
        Assert.That(found[0].LanguageTag, Is.EqualTo("en"));
    }

    [Test]
    public void SdhTrackIsFlagged()
    {
        var video = Video();
        Sidecar(BaseName(VideoName) + ".en.sdh.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found.Count, Is.EqualTo(1));
        Assert.That(found[0].Caption, Is.True);
        Assert.That(found[0].LanguageTag, Is.EqualTo("en"));
    }

    [Test]
    public void FilesThatDoNotBelongToTheVideoAreIgnored()
    {
        var video = Video();
        // Right extension, wrong stem.
        Sidecar("Some.Other.Movie.2019.en.srt");
        // Right stem, but the trailing token is not a language or a qualifier.
        Sidecar(BaseName(VideoName) + ".extras.srt");
        // A second video in the same folder keeps its own sidecar.
        Video("Other.Movie.2020.mkv");
        Sidecar("Other.Movie.2020.srt");

        var found = SidecarSubtitles.Find(video);

        Assert.That(found, Is.Empty);
    }

    [Test]
    public void EachVideoGetsItsOwnSidecarFromASharedFolder()
    {
        var first = Video(VideoName);
        var second = Video("Other.Movie.2020.mkv");
        var secondSub = Sidecar("Other.Movie.2020.srt");

        Assert.That(SidecarSubtitles.Find(first), Is.Empty);
        Assert.That(SidecarSubtitles.Find(second).Select(x => x.FilePath), Is.EqualTo(new[] { secondSub }));
    }

    [Test]
    public void AProbedLanguageCodeIsTurnedIntoAReadableName()
    {
        Assert.That(SidecarSubtitles.DisplayName("eng", "eng"), Is.EqualTo("English"));
        Assert.That(SidecarSubtitles.DisplayName(null, "ger"), Is.EqualTo("German"));
        Assert.That(SidecarSubtitles.DisplayName("", ""), Is.EqualTo(""));
        // An unknown but real language name is not a code, so it survives untouched.
        Assert.That(SidecarSubtitles.DisplayName("Français canadien", "fra"), Is.EqualTo("Français canadien"));
    }

    [Test]
    public void TheThreeLetterProbeCodeBecomesTheTwoLetterTagClientsGroupBy()
    {
        Assert.That(SidecarSubtitles.LanguageTag("eng"), Is.EqualTo("en"));
        Assert.That(SidecarSubtitles.LanguageTag("chi"), Is.EqualTo("zh"));
        Assert.That(SidecarSubtitles.LanguageTag("und"), Is.EqualTo("und"));
        Assert.That(SidecarSubtitles.LanguageTag(""), Is.EqualTo(""));
    }
}
