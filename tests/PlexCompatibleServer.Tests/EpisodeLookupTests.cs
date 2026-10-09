using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class EpisodeLookupTests
{
    private ExternalMetadata metadata = null!;

    [SetUp]
    public void SetUp() => metadata = new ExternalMetadata();

    private void resetSidecarCache()
    {
        typeof(SidecarStore)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(metadata.Store, null);
    }

    private void setCache(ConcurrentDictionary<string, SidecarItem> cache)
        => typeof(SidecarStore)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(metadata.Store, cache);

    private const string SHOW_SEARCH_JSON = """
    {
      "MediaContainer": {
        "SearchResults": [
          { "SearchResult": [ { "Metadata": [
            { "type": "show", "guid": "plex://show/REALSHOW", "title": "Slow Horses",
              "slug": "slow-horses", "ratingKey": "SHOWRK" } ] } ] },
          { "SearchResult": { "Metadata": {
            "type": "movie", "guid": "plex://movie/DECOY", "title": "Slow Horses",
            "ratingKey": "MOVIERK" } } }
        ]
      }
    }
    """;

    [Test]
    public void PickShow_picks_exact_show_and_skips_wrong_types()
    {
        var rec = PlexTvClient.PickShow(SHOW_SEARCH_JSON, "Slow Horses");
        Assert.That(rec, Is.Not.Null);
        Assert.That(rec!.RatingKey, Is.EqualTo("SHOWRK"));
        Assert.That(rec.Title, Is.EqualTo("Slow Horses"));
        Assert.That(rec.Guid, Is.EqualTo("plex://show/REALSHOW"));

        Assert.That(PlexTvClient.PickShow(SHOW_SEARCH_JSON, "A Completely Different Show"), Is.Null,
            "an unrelated name must not bind to the only show in the results");
    }

    [Test]
    public void PickShow_falls_back_to_clearly_leading_result_only()
    {
        const string LEADING = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [ { "score": 0.8, "Metadata": [
          { "type": "show", "guid": "plex://show/LEAD", "title": "Translated Name",
            "ratingKey": "RK1" } ] } ] } ] } }
        """;
        var rec = PlexTvClient.PickShow(LEADING, "Original Name");
        Assert.That(rec, Is.Not.Null);
        Assert.That(rec!.RatingKey, Is.EqualTo("RK1"));

        const string TIED = """
        { "MediaContainer": { "SearchResults": [ { "SearchResult": [
          { "score": 0.4, "Metadata": [ { "type": "show", "guid": "plex://show/A",
            "title": "One", "ratingKey": "RKA" } ] },
          { "score": 0.4, "Metadata": [ { "type": "show", "guid": "plex://show/B",
            "title": "Two", "ratingKey": "RKB" } ] } ] } ] } }
        """;
        Assert.That(PlexTvClient.PickShow(TIED, "Unknown Show"), Is.Null);
    }

    private const string SEASONS_JSON = """
    { "MediaContainer": { "size": 2, "Metadata": [
      { "index": 5, "title": "Season 5", "ratingKey": "S5", "key": "/library/metadata/S5/children" },
      { "index": 6, "title": "Season 6", "ratingKey": "S6", "key": "/library/metadata/S6/children" }
    ] } }
    """;

    [Test]
    public void PickSeasonKey_returns_children_key_for_the_parsed_season()
    {
        Assert.That(PlexTvClient.PickSeasonKey(SEASONS_JSON, 6), Is.EqualTo("/library/metadata/S6/children"));
        Assert.That(PlexTvClient.PickSeasonKey(SEASONS_JSON, 1), Is.Null);
    }

    private const string EPISODES_JSON = """
    { "MediaContainer": { "size": 2, "Metadata": [
      { "index": 1, "guid": "plex://episode/EP1", "ratingKey": "EP1RK",
        "title": "Come Home", "summary": "First episode summary.",
        "parentIndex": 6, "parentTitle": "Season 6", "parentKey": "/library/metadata/S6",
        "parentRatingKey": "S6", "parentGuid": "plex://season/S6",
        "parentThumb": "https://image.tmdb.org/t/p/original/season6-poster.jpg",
        "grandparentTitle": "Slow Horses", "grandparentKey": "/library/metadata/SHOWRK",
        "grandparentRatingKey": "SHOWRK", "grandparentGuid": "plex://show/REALSHOW",
        "grandparentThumb": "https://metadata-static.plex.tv/show-poster.jpg",
        "year": 2025, "originallyAvailableAt": "2025-09-24", "contentRating": "TV-MA",
        "audienceRating": 7.4 },
      { "index": 2, "guid": "plex://episode/EP2", "ratingKey": "EP2RK",
        "parentIndex": 6, "parentTitle": "Season 6", "grandparentTitle": "Slow Horses",
        "Role": [ { "id": "actor1", "tag": "Gary Oldman", "role": "Jackson Lamb" } ],
        "Director": [ { "id": "dir1", "tag": "Director One" } ],
        "Rating": [ { "image": "imdb://image.rating", "type": "imdb", "value": "7.6" } ],
        "Guid": [ { "id": "imdb://tt0000002" }, { "id": "tmdb://222" } ] }
    ] } }
    """;

    [Test]
    public void PickEpisodeRecord_maps_full_payload_including_hierarchy()
    {
        var parsed = new ParsedEpisodeName { ShowName = "Slow Horses", Season = 6, Episode = 1 };
        var rec = PlexTvClient.PickEpisodeRecord(EPISODES_JSON, 1, parsed);
        Assert.That(rec, Is.Not.Null);

        Assert.That(rec!.Guid, Is.EqualTo("plex://episode/EP1"));
        Assert.That(rec.RatingKey, Is.EqualTo("EP1RK"));
        Assert.That(rec.Title, Is.EqualTo("Come Home"));
        Assert.That(rec.TitleSort, Is.EqualTo("Come Home"));
        Assert.That(rec.Summary, Is.EqualTo("First episode summary."));
        Assert.That(rec.Year, Is.EqualTo("2025"));
        Assert.That(rec.OriginallyAvailableAt, Is.EqualTo("2025-09-24"));
        Assert.That(rec.ContentRating, Is.EqualTo("TV-MA"));
        Assert.That(rec.AudienceRating, Is.EqualTo(7.4));

        Assert.That(rec.Index, Is.EqualTo("1"));
        Assert.That(rec.ParentIndex, Is.EqualTo("6"));
        Assert.That(rec.ParentTitle, Is.EqualTo("Season 6"));
        Assert.That(rec.ParentKey, Is.EqualTo("/library/metadata/S6"));
        Assert.That(rec.ParentRatingKey, Is.EqualTo("S6"));
        Assert.That(rec.ParentGuid, Is.EqualTo("plex://season/S6"));
        Assert.That(rec.GrandparentTitle, Is.EqualTo("Slow Horses"));
        Assert.That(rec.GrandparentKey, Is.EqualTo("/library/metadata/SHOWRK"));
        Assert.That(rec.GrandparentRatingKey, Is.EqualTo("SHOWRK"));
        Assert.That(rec.GrandparentGuid, Is.EqualTo("plex://show/REALSHOW"));

        // Season/show posters ride on the episode payload; the artwork sync downloads them.
        Assert.That(rec.ParentThumbUrl, Is.EqualTo("https://image.tmdb.org/t/p/original/season6-poster.jpg"));
        Assert.That(rec.GrandparentThumbUrl, Is.EqualTo("https://metadata-static.plex.tv/show-poster.jpg"));
    }

    private const string SHOW_DETAIL_JSON = """
    { "MediaContainer": { "Metadata": [
      { "guid": "plex://show/REALSHOW", "title": "Slow Horses",
        "studio": "Apple TV+", "thumb": "https://metadata-static.plex.tv/show-poster.jpg",
        "Genre": [ { "id": "1", "tag": "Drama" } ] }
    ] } }
    """;

    [Test]
    public void MergeShowDetail_captures_show_poster_without_overwriting_the_episode_one()
    {
        var missing = new SidecarItem { Title = "Come Home" };
        PlexTvClient.MergeShowDetail(missing, SHOW_DETAIL_JSON);
        Assert.That(missing.GrandparentThumbUrl,
            Is.EqualTo("https://metadata-static.plex.tv/show-poster.jpg"));
        Assert.That(missing.Studio, Is.EqualTo("Apple TV+"));

        var captured = new SidecarItem { GrandparentThumbUrl = "https://example/existing.jpg" };
        PlexTvClient.MergeShowDetail(captured, SHOW_DETAIL_JSON);
        Assert.That(captured.GrandparentThumbUrl, Is.EqualTo("https://example/existing.jpg"),
            "the episode payload's grandparentThumb must win over the show detail fallback");
    }

    [Test]
    public void PickEpisodeRecord_maps_credits_and_falls_back_to_parsed_title()
    {
        var parsed = new ParsedEpisodeName { ShowName = "Slow Horses", Season = 6, Episode = 2 };
        var rec = PlexTvClient.PickEpisodeRecord(EPISODES_JSON, 2, parsed);
        Assert.That(rec, Is.Not.Null);

        Assert.That(rec!.Title, Is.EqualTo("Episode 2"),
            "a payload without a title must not fall back to the raw file stem");
        Assert.That(rec.Roles, Is.Not.Null);
        Assert.That(rec.Roles![0].Tag, Is.EqualTo("Gary Oldman"));
        Assert.That(rec.Roles[0].Role, Is.EqualTo("Jackson Lamb"));
        Assert.That(rec.Directors![0].Tag, Is.EqualTo("Director One"));
        Assert.That(rec.Ratings![0].Value, Is.EqualTo(7.6));
        Assert.That(rec.Guids, Is.EqualTo(new[] { "imdb://tt0000002", "tmdb://222" }));

        Assert.That(PlexTvClient.PickEpisodeRecord(EPISODES_JSON, 9, parsed), Is.Null);
    }

    [Test]
    public async Task FetchRecord_for_episodes_requires_a_parseable_filename_and_never_touches_network()
    {
        var showLib = new MediaLibrary { Id = 2, Type = LibraryType.Show, Name = "TV" };
        var unparsable = new MediaItem
        {
            Id = 1,
            LibraryId = 2,
            Library = showLib,
            FilePath = @"Z:\Ser\NoEpisodeMarker.1080p.mkv"
        };
        Assert.That(await metadata.Plex.FetchRecordAsync(unparsable, CancellationToken.None), Is.Null);

        var noLibrary = new MediaItem { Id = 2, FilePath = @"Z:\Ser\Some.Show.S01E01.mkv" };
        Assert.That(await metadata.Plex.FetchRecordAsync(noLibrary, CancellationToken.None), Is.Null);
    }

    [Test]
    public void TryGetRecord_never_fuzzy_binds_an_episode_to_a_movie_record()
    {
        resetSidecarCache();
        try
        {
            var cache = new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal);
            cache["carolinemovie2020"] = new SidecarItem { Title = "Caroline", FileStem = "Caroline.2020" };
            setCache(cache);

            var episode = new MediaItem
            {
                Id = 10,
                Library = new MediaLibrary { Id = 2, Type = LibraryType.Show },
                Title = "Caroline and Friends S01E01.mkv",
                FilePath = @"Z:\Ser\Caroline and Friends S01E01.mkv"
            };
            Assert.That(metadata.HasRecord(episode), Is.False,
                "the episode must not adopt a movie record via title-containment fuzzy matching");

            var movie = new MediaItem
            {
                Id = 11,
                Library = new MediaLibrary { Id = 1, Type = LibraryType.Movie },
                Title = "Caroline.2020.1080p.mkv",
                FilePath = @"G:\Movies\Caroline.2020.1080p.mkv"
            };
            Assert.That(metadata.HasRecord(movie), Is.True,
                "movie fuzzy matching must keep working");
        }
        finally
        {
            resetSidecarCache();
        }
    }

    [Test]
    public async Task Apply_maps_episode_hierarchy_onto_the_video()
    {
        resetSidecarCache();
        try
        {
            var cache = new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal);
            cache["slowhorsess06e02porkypyne1080p"] = new SidecarItem
            {
                Guid = "plex://episode/EP2",
                Title = "Porky pyne",
                TitleSort = "Porky pyne",
                Summary = "Episode summary.",
                Index = "2",
                ParentIndex = "6",
                ParentTitle = "Season 6",
                ParentKey = "/library/metadata/S6",
                ParentRatingKey = "S6",
                ParentGuid = "plex://season/S6",
                GrandparentTitle = "Slow Horses",
                GrandparentKey = "/library/metadata/SHOWRK",
                GrandparentRatingKey = "SHOWRK",
                GrandparentGuid = "plex://show/REALSHOW",
                DetailChecked = true
            };
            setCache(cache);

            var item = new MediaItem
            {
                Id = 45,
                Library = new MediaLibrary { Id = 2, Type = LibraryType.Show },
                FilePath = @"Z:\Ser\Slow Horses S06E02 Porky pyne 1080p.mkv"
            };
            var video = new XmlVideo { Title = "Slow Horses S06E02 Porky pyne 1080p" };

            await metadata.ApplyAsync(item, video);

            Assert.That(video.Guid, Is.EqualTo("plex://episode/EP2"));
            Assert.That(video.Title, Is.EqualTo("Porky pyne"));
            Assert.That(video.Summary, Is.EqualTo("Episode summary."));
            Assert.That(video.Index, Is.EqualTo("2"));
            Assert.That(video.ParentIndex, Is.EqualTo("6"));
            Assert.That(video.ParentTitle, Is.EqualTo("Season 6"));
            Assert.That(video.ParentType, Is.EqualTo("season"));
            Assert.That(video.GrandparentTitle, Is.EqualTo("Slow Horses"));
            Assert.That(video.GrandparentType, Is.EqualTo("show"));
        }
        finally
        {
            resetSidecarCache();
        }
    }

    private const string DARK_MATTER_SEARCH_JSON = """
    { "MediaContainer": { "SearchResults": [ { "SearchResult": [ { "Metadata": [
      { "type": "show", "guid": "plex://show/A2015", "title": "Dark Matter", "slug": "dark-matter", "ratingKey": "RK2015" },
      { "type": "show", "guid": "plex://show/A2024", "title": "Dark Matter (2024)", "slug": "dark-matter-2024", "ratingKey": "RK2024" },
      { "type": "show", "guid": "plex://show/DM2011", "title": "Dark Matters", "ratingKey": "RK2011" },
      { "type": "show", "guid": "plex://show/NM", "title": "No Matter How Much the Night is Dark", "ratingKey": "RKNM" },
      { "type": "movie", "guid": "plex://movie/DECOY", "title": "Dark Matter", "ratingKey": "MOVERK" }
    ] } ] } ] } }
    """;

    [Test]
    public void ShowBindingKey_strips_the_year_so_dated_and_titleless_files_agree()
    {
        Assert.That(MetadataMatcher.ShowBindingKey("Dark Matter 2024"),
            Is.EqualTo(MetadataMatcher.ShowBindingKey("Dark Matter")));
        Assert.That(MetadataMatcher.ShowBindingKey("Dark Matter"), Is.EqualTo("darkmatter"));
        Assert.That(MetadataMatcher.ShowBindingKey("Slow Horses"),
            Is.Not.EqualTo(MetadataMatcher.ShowBindingKey("Dark Matter")));
        Assert.That(MetadataMatcher.ContainsYearToken("Dark Matter 2024"), Is.True);
        Assert.That(MetadataMatcher.ContainsYearToken("Dark Matter"), Is.False);
    }

    [Test]
    public void GetShowCandidates_keeps_exact_and_year_suffixed_rivals_and_drops_decoys()
    {
        var forPlain = PlexTvClient.GetShowCandidates(DARK_MATTER_SEARCH_JSON, "Dark Matter");
        Assert.That(forPlain.Select(x => x.RatingKey),
            Is.EqualTo(new[] { "RK2015", "RK2024", "RK2011" }),
            "both series sharing the name must reach the disambiguation; the movie decoy must not");

        var forDated = PlexTvClient.GetShowCandidates(DARK_MATTER_SEARCH_JSON, "Dark Matter 2024");
        Assert.That(forDated.Select(x => x.RatingKey),
            Is.EqualTo(new[] { "RK2015", "RK2024" }));
    }

    [Test]
    public void PickShowByYear_picks_the_single_year_suffixed_title()
    {
        var candidates = PlexTvClient.GetShowCandidates(DARK_MATTER_SEARCH_JSON, "Dark Matter 2024");
        var pick = PlexTvClient.PickShowByYear(candidates, "Dark Matter 2024");
        Assert.That(pick?.RatingKey, Is.EqualTo("RK2024"));

        Assert.That(PlexTvClient.PickShowByYear(candidates, "Dark Matter"), Is.Null,
            "a filename without a year must not guess by year");
        Assert.That(PlexTvClient.PickShowByYear(candidates, "Dark Matter 1999"), Is.Null,
            "no candidate carries the year -> the caller keeps the plain pick");
    }

    [Test]
    public void PickEpisodeTitle_reads_the_episode_and_absent_titles_stay_null()
    {
        Assert.That(PlexTvClient.PickEpisodeTitle(EPISODES_JSON, 1), Is.EqualTo("Come Home"));
        Assert.That(PlexTvClient.PickEpisodeTitle(EPISODES_JSON, 2), Is.Null,
            "a payload without a title must not invent one");
        Assert.That(PlexTvClient.PickEpisodeTitle(EPISODES_JSON, 9), Is.Null);
    }

    [Test]
    public void EpisodeTitleMatches_requires_a_distinctive_overlap()
    {
        Assert.That(PlexTvClient.EpisodeTitleMatches("Love and Be Loved", "Love and Be Loved"), Is.True);
        Assert.That(PlexTvClient.EpisodeTitleMatches("Love and Be Loved 1080p", "Love and Be Loved"), Is.True);
        Assert.That(PlexTvClient.EpisodeTitleMatches("The", "The Pyramid"), Is.False,
            "a fragment too short to be distinctive must not match by prefix");
        Assert.That(PlexTvClient.EpisodeTitleMatches("", "Love and Be Loved"), Is.False);
        Assert.That(PlexTvClient.EpisodeTitleMatches("We Voted Not to Space You", "Love and Be Loved"), Is.False);
    }

    private static string? fakeShowFetch(string url)
    {
        if (url.Contains("/library/metadata/SHOWA/children"))
            return """{ "MediaContainer": { "Metadata": [ { "index": 2, "key": "/library/metadata/SEASONA/children" } ] } }""";
        if (url.Contains("/library/metadata/SHOWB/children"))
            return """{ "MediaContainer": { "Metadata": [ { "index": 2, "key": "/library/metadata/SEASONB/children" } ] } }""";
        if (url.Contains("/library/metadata/SEASONA/children"))
            return """{ "MediaContainer": { "Metadata": [ { "index": 5, "title": "We Voted Not to Space You" } ] } }""";
        if (url.Contains("/library/metadata/SEASONB/children"))
            return """{ "MediaContainer": { "Metadata": [ { "index": 5, "title": "Love and Be Loved" } ] } }""";
        return null;
    }

    [Test]
    public async Task VerifyByEpisodeTitle_picks_the_show_whose_real_episode_matches_the_filename()
    {
        var candidates = new List<SidecarItem>
        {
            new() { Guid = "plex://show/A", Title = "Dark Matter", RatingKey = "SHOWA" },
            new() { Guid = "plex://show/B", Title = "Dark Matter (2024)", RatingKey = "SHOWB" }
        };
        var parsed = new ParsedEpisodeName
        {
            ShowName = "Dark Matter",
            Season = 2,
            Episode = 5,
            EpisodeTitle = "Love and Be Loved"
        };

        Task<string?> Fetch(string url) => Task.FromResult(fakeShowFetch(url));

        var pick = await PlexTvClient.VerifyByEpisodeTitleAsync(candidates, parsed, (url, _) => Fetch(url), CancellationToken.None);
        Assert.That(pick?.RatingKey, Is.EqualTo("SHOWB"),
            "the candidate whose S02E05 carries the filename's title must win");

        parsed.EpisodeTitle = "A Title Neither Show Has";
        Assert.That(await PlexTvClient.VerifyByEpisodeTitleAsync(candidates, parsed, (url, _) => Fetch(url), CancellationToken.None), Is.Null,
            "no match -> the caller keeps the plain pick and binds nothing");

        Assert.That(await PlexTvClient.VerifyByEpisodeTitleAsync(
            candidates, parsed, (_, _) => Task.FromResult<string?>(null), CancellationToken.None), Is.Null,
            "failed fetches must not count as a match");
    }

    [Test]
    public void OrderLookupPasses_runs_disambiguable_episodes_before_titleless_siblings()
    {
        var showLib = new MediaLibrary { Id = 2, Type = LibraryType.Show };
        var movieLib = new MediaLibrary { Id = 1, Type = LibraryType.Movie };
        var titled = new MediaItem { Library = showLib, FilePath = @"Z:\Ser\Dark.Matter.S02E03.Love.and.Be.Loved.mkv" };
        var titleless = new MediaItem { Library = showLib, FilePath = @"Z:\Ser\Dark.Matter.S02E01.1080p.mkv" };
        var dated = new MediaItem { Library = showLib, FilePath = @"Z:\Ser\Dark.Matter.2024.S02E06.mkv" };
        var movie = new MediaItem { Library = movieLib, FilePath = @"G:\Movies\Some.Movie.2024.mkv" };

        var ordered = MetadataMatcher.OrderLookupPasses(new[] { titleless, titled, movie, dated });

        Assert.That(ordered, Is.EqualTo(new[] { titled, dated, titleless, movie }),
            "year- and title-bearing episodes must establish the show binding first");
    }

    [Test]
    public void ShowBinding_round_trips_through_its_own_file()
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var path = Path.Combine(wwwroot, "plex-show-bindings.json");
        var key = "zzbindingtest" + Guid.NewGuid().ToString("N")[..8];
        var bindings = typeof(SidecarStore).GetField("_showBindings", BindingFlags.NonPublic | BindingFlags.Instance);
        var previous = bindings!.GetValue(metadata.Store);
        bindings.SetValue(metadata.Store, null);
        try
        {
            metadata.Store.SaveShowBinding(key, new SidecarItem
            {
                Title = "Dark Matter (2024)",
                RatingKey = "5fd2a1b82de5fd002dd4c7b1"
            });

            Assert.That(File.Exists(path), Is.True, "binding file must be written");
            bindings.SetValue(metadata.Store, null);
            var reloaded = metadata.Store.GetShowBinding(key);
            Assert.That(reloaded?.RatingKey, Is.EqualTo("5fd2a1b82de5fd002dd4c7b1"),
                "a fresh process must read the binding back from disk");
        }
        finally
        {
            bindings.SetValue(metadata.Store, previous);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}

