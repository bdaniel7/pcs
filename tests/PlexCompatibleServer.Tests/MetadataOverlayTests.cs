using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// Characterization tests for the metadata overlay applied to every item. They pin the field
/// mapping and the cache-only contract of the list/grid path so the Phase 2 refactor (async,
/// DI, god-class split) can move the implementation without changing behaviour.
/// </summary>
[TestFixture]
public class MetadataOverlayTests
{
    private static readonly FieldInfo cacheField = typeof(SidecarStore)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private ExternalMetadata metadata = null!;

    [SetUp]
    public void SetUp() => metadata = new ExternalMetadata();

    private static MediaItem movie(string fileName) => new()
    {
        Id = 1,
        LibraryId = 1,
        Library = new MediaLibrary { Id = 1, Name = "Movies", Type = LibraryType.Movie },
        FilePath = "/media/" + fileName,
        Title = "Movie.One.2020"
    };

    private void setCache(ConcurrentDictionary<string, SidecarItem> cache)
        => cacheField.SetValue(metadata.Store, cache);

    private void resetCache() => cacheField.SetValue(metadata.Store, null);

    [Test]
    public void Apply_overlays_a_cached_movie_record_onto_the_video()
    {
        setCache(new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
        {
            // The key is GetKey(item): filename stem lower-cased with non-alphanumerics removed.
            ["movieone2020"] = new SidecarItem
            {
                Title = "Movie One",
                TitleSort = "Movie One",
                Year = "2020",
                Studio = "Indie Films",
                Summary = "A summary.",
                Tagline = "A tagline.",
                ContentRating = "R",
                ContentRatingAge = 17,
                AudienceRating = 7.3,
                Guid = "plex://movie/abc",
                DetailChecked = true,
                Guids = ["imdb://tt1234567", "tmdb://99"],
                Genres = ["Drama", "Crime"],
                Countries = ["United States"],
                Directors = [new TagRef { Tag = "Dir One", TagKey = "dk1" }],
                Writers = [new TagRef { Tag = "Wri One" }],
                Producers = [new TagRef { Tag = "Pro One" }],
                Roles = [new TagRef { Tag = "Actor One", TagKey = "ak1", Role = "Hero" }],
                Ratings = [new RatingRef { Image = "imdb://image.rating", Type = "audience", Value = 7.3 }]
            }
        });
        try
        {
            var video = new XmlVideo { Title = "Movie.One.2020" };

            metadata.Apply(movie("Movie.One.2020.mkv"), video);

            Assert.Multiple(() =>
            {
                Assert.That(video.Title, Is.EqualTo("Movie One"));
                Assert.That(video.TitleSort, Is.EqualTo("Movie One"));
                Assert.That(video.Year, Is.EqualTo("2020"));
                Assert.That(video.Studio, Is.EqualTo("Indie Films"));
                Assert.That(video.Summary, Is.EqualTo("A summary."));
                Assert.That(video.Tagline, Is.EqualTo("A tagline."));
                Assert.That(video.ContentRating, Is.EqualTo("R"));
                Assert.That(video.ContentRatingAge, Is.EqualTo("17"));
                Assert.That(video.AudienceRating, Is.EqualTo("7.3"));
                Assert.That(video.Guid, Is.EqualTo("plex://movie/abc"));
                // The item's own plex guid leads, then the external source ids.
                Assert.That(video.Guids.Select(g => g.Id),
                    Is.EqualTo(new[] { "plex://movie/abc", "imdb://tt1234567", "tmdb://99" }));
                Assert.That(video.Genres.Select(g => g.Tag), Is.EqualTo(new[] { "Drama", "Crime" }));
                Assert.That(video.Countries.Select(c => c.Tag), Is.EqualTo(new[] { "United States" }));
                Assert.That(video.Directors.Select(d => d.Tag), Is.EqualTo(new[] { "Dir One" }));
                Assert.That(video.Writers.Select(w => w.Tag), Is.EqualTo(new[] { "Wri One" }));
                Assert.That(video.Roles.Select(r => r.Role), Is.EqualTo(new[] { "Hero" }));
                Assert.That(video.Ratings.Select(r => r.Value), Is.EqualTo(new[] { "7.3" }));
            });
        }
        finally
        {
            resetCache();
        }
    }

    [Test]
    public void Apply_falls_back_to_a_zero_rating_when_the_record_carries_none()
    {
        setCache(new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
        {
            ["movieone2020"] = new SidecarItem { Title = "Movie One", DetailChecked = true }
        });
        try
        {
            var video = new XmlVideo { Title = "Movie.One.2020" };

            metadata.Apply(movie("Movie.One.2020.mkv"), video);

            Assert.That(video.Ratings, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(video.Ratings[0].Image, Is.EqualTo("imdb://image.rating"));
                Assert.That(video.Ratings[0].Type, Is.EqualTo("audience"));
                Assert.That(video.Ratings[0].Value, Is.EqualTo("0"));
            });
        }
        finally
        {
            resetCache();
        }
    }

    [Test]
    public void Apply_with_no_record_leaves_video_untouched()
    {
        setCache(new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal));
        try
        {
            var video = new XmlVideo { Title = "Untouched", Guid = "local://guid" };

            metadata.Apply(movie("Unknown.Film.1999.mkv"), video);

            Assert.Multiple(() =>
            {
                Assert.That(video.Title, Is.EqualTo("Untouched"));
                Assert.That(video.Guid, Is.EqualTo("local://guid"));
                Assert.That(video.Summary, Is.Empty);
                Assert.That(video.Guids, Is.Empty);
                Assert.That(video.Genres, Is.Empty);
                Assert.That(video.Ratings, Is.Empty);
            });
        }
        finally
        {
            resetCache();
        }
    }
}
