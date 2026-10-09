using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class SidecarLookupTests
{
    private ExternalMetadata metadata = null!;

    [SetUp]
    public void SetUp() => metadata = new ExternalMetadata();

    [TestCase(@"Z:\Ser\Little.Lorraine.2025.1080p.WEBRip.x264.AAC5.1-[YTS.GG - YTS.BZ].mp4",
        "Little Lorraine")]
    [TestCase(@"Z:\Ser\The.Batman.2022.2160p.BluRay.x265.mkv", "The Batman")]
    [TestCase(@"Z:\Ser\2001.A.Space.Odyssey.1080p.BluRay.x264.mkv", "2001 A Space Odyssey")]
    [TestCase(@"Z:\Ser\Confess.Fletch.2022.1080p.WEB-DL.mkv", "Confess Fletch")]
    [TestCase(@"Z:\Ser\Caroline.1080p.WEBRip.mkv", "Caroline")]
    [TestCase(@"Z:\Ser\And.Life.Goes.On.1992.1080p.BluRay.x264-[YTS.LT].mp4", "And Life Goes On")]
    [TestCase("", "")]
    public void CleanSearchTitle_strips_release_junk(string path, string expected) =>
        Assert.That(PlexTvClient.CleanSearchTitle(path), Is.EqualTo(expected));

    private const string SEARCH_JSON = """
    {
      "MediaContainer": {
        "size": 4,
        "SearchResults": [
          { "SearchResult": [
            { "Metadata": [
              { "type": "movie", "guid": "plex://movie/WRONGTITLE", "title": "Sweet Lorraine",
                "year": 2025, "ratingKey": "11", "originallyAvailableAt": "2025-01-01" },
              { "type": "movie", "guid": "plex://movie/OLDYEAR", "title": "Little Lorraine",
                "year": 1999, "ratingKey": "12" },
              { "type": "movie", "guid": "plex://movie/NEARYEAR", "title": "Little Lorraine",
                "year": 2026, "ratingKey": "13" },
              { "type": "movie", "guid": "plex://movie/EXACTYEAR", "title": "Little Lorraine",
                "year": 2025, "ratingKey": "14", "originallyAvailableAt": "2025-09-19",
                "thumb": "https://image.tmdb.org/t/p/original/search-poster.jpg",
                "art": "https://image.tmdb.org/t/p/original/search-art.jpg" }
            ] }
          ] },
          { "SearchResult": { "Metadata": { "type": "episode", "guid": "plex://episode/x",
              "title": "Little Lorraine", "year": 2025, "ratingKey": "15" } } }
        ]
      }
    }
    """;

    [Test]
    public void PickCandidate_prefers_exact_normalized_title_and_closest_year()
    {
        var rec = PlexTvClient.PickCandidate(SEARCH_JSON, "Little.Lorraine", 2025);

        Assert.That(rec, Is.Not.Null);
        Assert.That(rec!.Guid, Is.EqualTo("plex://movie/EXACTYEAR"));
        Assert.That(rec.RatingKey, Is.EqualTo("14"));
        Assert.That(rec.Title, Is.EqualTo("Little Lorraine"));
        Assert.That(rec.Year, Is.EqualTo("2025"));
        Assert.That(rec.OriginallyAvailableAt, Is.EqualTo("2025-09-19"));

        // The search payload already carries the remote poster/backdrop, so a record that never
        // gets its detail fetched (no token) can still download artwork.
        Assert.That(rec.ThumbUrl, Is.EqualTo("https://image.tmdb.org/t/p/original/search-poster.jpg"));
        Assert.That(rec.ArtUrl, Is.EqualTo("https://image.tmdb.org/t/p/original/search-art.jpg"));
    }

    [Test]
    public void PickCandidate_rejects_year_mismatch_and_unknown_titles()
    {
        Assert.That(PlexTvClient.PickCandidate(SEARCH_JSON, "Totally Different Film", 2025),
            Is.Null);

        const string ONLY_FAR_YEAR = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [ { "Metadata": [
          { "type": "movie", "guid": "plex://movie/FAR", "title": "Little Lorraine",
            "year": 1999, "ratingKey": "99" } ] } ] } ] } }
        """;
        Assert.That(PlexTvClient.PickCandidate(ONLY_FAR_YEAR, "Little Lorraine", 2025), Is.Null);
    }

    [Test]
    public void PickCandidate_accepts_missing_year_on_either_side()
    {
        const string JSON = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [ { "Metadata": [
          { "type": "movie", "guid": "plex://movie/NY", "title": "Carolina Caroline",
            "ratingKey": "77" } ] } ] } ] } }
        """;
        Assert.That(PlexTvClient.PickCandidate(JSON, "Carolina Caroline", 2025), Is.Not.Null);
        Assert.That(PlexTvClient.PickCandidate(JSON, "Carolina Caroline", null), Is.Not.Null);
    }

    [Test]
    public void PickCandidate_matches_renamed_film_via_slug_and_year_gate_blocks_decoy()
    {
        // Real case: "And Life Goes On" (1992) is titled "Life, and Nothing More…" on plex.tv,
        // while a 2019 film carries the exact original title.
        const string JSON = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [
          { "score": 0.78, "Metadata": [
            { "type": "movie", "guid": "plex://movie/RENAMED1992", "title": "Life, and Nothing More…",
              "slug": "and-life-goes-on", "year": 1992, "ratingKey": "91",
              "originallyAvailableAt": "1992-10-21" } ] },
          { "score": 0.39, "Metadata": [
            { "type": "movie", "guid": "plex://movie/DECOY2019", "title": "And Life Goes On",
              "slug": "soshite-ikiru", "year": 2019, "ratingKey": "92" } ] }
        ] } ] } }
        """;

        var rec = PlexTvClient.PickCandidate(JSON, "And.Life.Goes.On", 1992);
        Assert.That(rec, Is.Not.Null);
        Assert.That(rec!.Guid, Is.EqualTo("plex://movie/RENAMED1992"));
        Assert.That(rec.OriginallyAvailableAt, Is.EqualTo("1992-10-21"));

        var rec2019 = PlexTvClient.PickCandidate(JSON, "And.Life.Goes.On", 2019);
        Assert.That(rec2019?.Guid, Is.EqualTo("plex://movie/DECOY2019"),
            "a 2019 file must match the 2019 exact-title film, not the renamed 1992 one");
    }

    private const string DETAIL_JSON = """
    {
      "MediaContainer": {
        "Metadata": [
          {
            "guid": "plex://movie/657d04943fedcb6d9c4d23c0",
            "title": "Little Lorraine",
            "year": 2025,
            "summary": "A heist summary.",
            "tagline": "Crime pays.",
            "studio": "Some Studio",
            "contentRating": "R",
            "originallyAvailableAt": "2025-09-19",
            "audienceRating": "",
            "thumb": "https://metadata-static.plex.tv/detail-poster.jpg",
            "art": "https://image.tmdb.org/t/p/original/detail-art.jpg",
            "Rating": [
              { "image": "imdb://image.rating", "type": "imdb", "value": "6.8" },
              { "image": "rottentomatoes://image.rating.ripe", "type": "rt", "value": "91" }
            ],
            "Role": [
              { "id": "5a1b2c3d4e5f6a7b8c9d0e1f", "tag": "Performer One",
                "role": "Character One", "thumb": "https://metadata-static.plex.tv/x.jpg" }
            ],
            "Director": [ { "id": "5d776825961905001eb90a21", "tag": "Director One" } ],
            "Writer": [ { "id": "5d776825961905001eb90a22", "tag": "Writer One" } ],
            "Producer": [ { "id": "5d776825961905001eb90a23", "tag": "Producer One" } ],
            "Country": [ { "id": "1", "tag": "United States" } ],
            "Genre": [
              { "id": "2", "tag": "Crime" },
              { "id": "3", "tag": "Comedy" }
            ],
            "Guid": [ { "id": "imdb://tt0000001" }, { "id": "tmdb://12345" } ]
          }
        ]
      }
    }
    """;

    [Test]
    public void PickCandidate_fallback_trusts_leading_same_year_result_for_translated_titles()
    {
        // Real case: "Zwei Staatsanwalte" (2025) is listed as "Two Prosecutors" - no name overlap
        // at all, but plex.tv ranks it first with a clear score lead and the year checks out.
        const string JSON = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [
          { "score": 0.38, "Metadata": [
            { "type": "movie", "guid": "plex://movie/TWOPROSECUTORS", "title": "Two Prosecutors",
              "slug": "two-prosecutors", "year": 2025, "ratingKey": "81" } ] },
          { "score": 0.30, "Metadata": [
            { "type": "movie", "guid": "plex://movie/OTHER", "title": "Zwei Weihnachtsmänner",
              "slug": "zwei-weihnachtsmanner", "year": 2008, "ratingKey": "82" } ] }
        ] } ] } }
        """;

        var rec = PlexTvClient.PickCandidate(JSON, "Zwei Staatsanwalte", 2025);
        Assert.That(rec, Is.Not.Null);
        Assert.That(rec!.Guid, Is.EqualTo("plex://movie/TWOPROSECUTORS"));
        Assert.That(rec.Title, Is.EqualTo("Two Prosecutors"));
    }

    [Test]
    public void PickCandidate_fallback_rejects_wrong_year_and_weak_lead()
    {
        const string WRONG_YEAR = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [
          { "score": 0.30, "Metadata": [
            { "type": "movie", "guid": "plex://movie/W2017", "title": "Person to Person",
              "slug": "person-to-person-2017", "year": 2017, "ratingKey": "83" } ] },
          { "score": 0.30, "Metadata": [
            { "type": "movie", "guid": "plex://movie/W2014", "title": "Person to Person",
              "slug": "person-to-person", "year": 2014, "ratingKey": "84" } ] }
        ] } ] } }
        """;
        Assert.That(PlexTvClient.PickCandidate(WRONG_YEAR, "Person To Bunny", 1960), Is.Null,
            "a top result from another year must not be matched");

        const string TIED_SCORES = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [
          { "score": 0.30, "Metadata": [
            { "type": "movie", "guid": "plex://movie/TIE1", "title": "Peak Season",
              "slug": "peak-season-2023", "year": 2023, "ratingKey": "85" } ] },
          { "score": 0.30, "Metadata": [
            { "type": "movie", "guid": "plex://movie/TIE2", "title": "Druid Peak",
              "slug": "druid-peak", "year": 2023, "ratingKey": "86" } ] }
        ] } ] } }
        """;
        Assert.That(PlexTvClient.PickCandidate(TIED_SCORES, "Pikers Peak", 2023), Is.Null,
            "a tied, barely-relevant top result is too risky to trust");

        const string WEAK_SCORE = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [
          { "score": 0.31, "Metadata": [
            { "type": "movie", "guid": "plex://movie/WEAK", "title": "Something Else",
              "slug": "something-else", "year": 2025, "ratingKey": "87" } ] }
        ] } ] } }
        """;
        Assert.That(PlexTvClient.PickCandidate(WEAK_SCORE, "Unknown Film", 2025), Is.Null,
            "score below the 0.35 relevance floor must not be matched");
    }

    [Test]
    public void EnrichFromDetail_maps_all_fields_and_tolerates_empty_ratings()
    {
        var rec = new SidecarItem
        {
            Guid = "plex://movie/OLD",
            Title = "old title",
            Year = "2020",
            ThumbUrl = "https://stale.example/poster.jpg",
            ArtUrl = "https://stale.example/art.jpg"
        };
        PlexTvClient.EnrichFromDetail(rec, DETAIL_JSON);

        Assert.That(rec.Guid, Is.EqualTo("plex://movie/657d04943fedcb6d9c4d23c0"));
        Assert.That(rec.Title, Is.EqualTo("Little Lorraine"));
        Assert.That(rec.TitleSort, Is.EqualTo("Little Lorraine"));
        Assert.That(rec.Year, Is.EqualTo("2025"));
        Assert.That(rec.Summary, Is.EqualTo("A heist summary."));
        Assert.That(rec.Tagline, Is.EqualTo("Crime pays."));
        Assert.That(rec.Studio, Is.EqualTo("Some Studio"));
        Assert.That(rec.ContentRating, Is.EqualTo("R"));
        Assert.That(rec.OriginallyAvailableAt, Is.EqualTo("2025-09-19"));

        Assert.That(rec.AudienceRating, Is.Null);

        Assert.That(rec.Ratings, Is.Not.Null);
        Assert.That(rec.Ratings!.Count, Is.EqualTo(2));
        Assert.That(rec.Ratings[0].Value, Is.EqualTo(6.8));
        Assert.That(rec.Ratings[1].Type, Is.EqualTo("rt"));

        Assert.That(rec.Roles, Is.Not.Null);
        Assert.That(rec.Roles![0].Tag, Is.EqualTo("Performer One"));
        Assert.That(rec.Roles[0].TagKey, Is.EqualTo("5a1b2c3d4e5f6a7b8c9d0e1f"));
        Assert.That(rec.Roles[0].Role, Is.EqualTo("Character One"));
        Assert.That(rec.Roles[0].Thumb, Does.StartWith("https://"));

        Assert.That(rec.Directors![0].Tag, Is.EqualTo("Director One"));
        Assert.That(rec.Writers![0].Tag, Is.EqualTo("Writer One"));
        Assert.That(rec.Producers![0].Tag, Is.EqualTo("Producer One"));

        Assert.That(rec.Genres, Is.EqualTo(new[] { "Crime", "Comedy" }));
        Assert.That(rec.Countries, Is.EqualTo(new[] { "United States" }));
        Assert.That(rec.Guids, Is.EqualTo(new[] { "imdb://tt0000001", "tmdb://12345" }));

        // Detail is authoritative: it overwrites whatever the search payload captured earlier.
        Assert.That(rec.ThumbUrl, Is.EqualTo("https://metadata-static.plex.tv/detail-poster.jpg"));
        Assert.That(rec.ArtUrl, Is.EqualTo("https://image.tmdb.org/t/p/original/detail-art.jpg"));
    }

    [Test]
    public void EnrichFromDetail_survives_garbage_input()
    {
        var rec = new SidecarItem { Guid = "plex://movie/KEEP" };
        PlexTvClient.EnrichFromDetail(rec, "not json at all");
        Assert.That(rec.Guid, Is.EqualTo("plex://movie/KEEP"));
    }

    [Test]
    public async Task LookupOnline_is_skipped_outside_movie_libraries()
    {
        var item = new MediaItem { Id = 1, LibraryId = 1, FilePath = @"Z:\Ser\Nope.2025.mp4", Year = 2025 };
        Assert.That(
            await metadata.Plex.LookupOnlineAsync(item, CancellationToken.None), Is.Null);

        var noTitle = new MediaItem { Id = 2, LibraryId = 1, FilePath = "", Year = 2025 };
        Assert.That(await metadata.Plex.LookupOnlineAsync(noTitle, CancellationToken.None), Is.Null);
    }
}
