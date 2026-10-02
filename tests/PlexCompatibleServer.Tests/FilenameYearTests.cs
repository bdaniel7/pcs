using NUnit.Framework;
using PlexCompatibleServer.Infrastructure.Data;

namespace PlexCompatibleServer.Tests;

public sealed class FilenameYearTests
{
    [TestCase("Zwei Staatsanwalte (2025) [1080p] [WEBRip]", 2025)]
    [TestCase("Matilda.1996.1080p.BluRay.x264", 1996)]
    [TestCase("The End of Oak Street [2026]", 2026)]
    [TestCase("Some Movie 2019", 2019)]
    public void Parse_FindsTheReleaseYear(string filename, int expected) =>
        Assert.That(FilenameYear.Parse(filename), Is.EqualTo(expected));

    [TestCase("Some.Movie.2160p.UHD.BluRay")]
    [TestCase("Movie.[YTS.BZ].Group")]
    [TestCase("")]
    public void Parse_ReturnsNullWhenNoPlausibleYear(string filename) =>
        Assert.That(FilenameYear.Parse(filename), Is.Null);

    [Test]
    public void Parse_PrefersParentheticalYearOverResolutionDigits()
    {
        // "2160" is a resolution and "1999" is bracketed; the parenthesised year must win.
        Assert.That(FilenameYear.Parse("Movie (2025) [2160p] [1999]"), Is.EqualTo(2025));
    }
}
