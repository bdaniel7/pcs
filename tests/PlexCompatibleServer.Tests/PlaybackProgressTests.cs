using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Data;

namespace PlexCompatibleServer.Tests;

public class PlaybackProgressTests
{
    private string dbPath = "";
    private string root = "";
    private TestFactory factory = null!;
    private MediaRepository repo = null!;
    private MediaLibrary library = null!;
    private MediaItem movie = null!;

    private sealed class TestFactory(DbContextOptions<MediaDbContext> options)
        : IDbContextFactory<MediaDbContext>
    {
        public MediaDbContext CreateDbContext() => new(options);
    }

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "pcs-progress-" + Guid.NewGuid().ToString("N"));
        dbPath = Path.Combine(Path.GetTempPath(), "pcs-progress-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(root);

        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        factory = new TestFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            library = new MediaLibrary { Name = "Movies", RootPath = root, Type = LibraryType.Movie };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            movie = new MediaItem
            {
                LibraryId = library.Id,
                FilePath = Path.Combine(root, "Movie.Two.Thousand.mkv"),
                Title = "Movie Two Thousand",
                FileSize = 123,
                DurationMs = 6416960
            };
            db.Items.Add(movie);
            await db.SaveChangesAsync();
        }

        repo = new MediaRepository(factory);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(root, true); } catch { }
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            try { File.Delete(p); } catch { }
    }

    private async Task<MediaItem> reloadAsync(int id)
    {
        await using var db = factory.CreateDbContext();
        return await db.Items.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static TimelineController timelineFor(MediaRepository repo)
    {
        var controller = new TimelineController(repo)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    [Test]
    public async Task Timeline_report_persists_and_surfaces_on_responses()
    {
        var controller = timelineFor(repo);
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 36980, duration: 6416960,
            state: "playing", playQueueItemId: 1);

        var item = await reloadAsync(movie.Id);
        Assert.That(item.ViewOffset, Is.EqualTo(36980), "the reported position must be stored");
        Assert.That(item.LastViewedAt, Is.Not.Null, "recency must be recorded for Continue Watching");
        Assert.That(item.ViewCount, Is.EqualTo(0), "a partial view is not a completed view");

        var fresh = await repo.GetItemAsync(movie.Id, CancellationToken.None);
        var video = VideoMapper.ToVideo(fresh!);
        Assert.That(video.ViewOffset, Is.EqualTo("36980"));
        Assert.That(video.ViewCount, Is.Empty, "no viewCount before the watched threshold");
        Assert.That(video.LastViewedAt, Is.Not.Empty);

        // The client parses JSON: viewOffset has to arrive as a number, like duration does.
        var json = PlexJson.Serialize(new XmlMediaContainer { Videos = { video } });
        Assert.That(json, Does.Contain("\"viewOffset\":36980"));
        Assert.That(json, Does.Contain("\"lastViewedAt\":"));
    }

    [Test]
    public async Task Timeline_at_zero_never_erases_a_resume_point()
    {
        var controller = timelineFor(repo);
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 36980, duration: 6416960,
            state: "playing", playQueueItemId: 1);
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 0, duration: 6416960,
            state: "buffering", playQueueItemId: 1);

        var item = await reloadAsync(movie.Id);
        Assert.That(item.ViewOffset, Is.EqualTo(36980),
            "the buffering heartbeat at 0 that a resume seek produces must not clobber the offset");
    }

    [Test]
    public async Task Crossing_ninety_percent_counts_exactly_one_view()
    {
        var controller = timelineFor(repo);
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 6000000, duration: 6416960, state: "playing");
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 6100000, duration: 6416960, state: "playing");
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 6416960, duration: 6416960, state: "stopped");

        var item = await reloadAsync(movie.Id);
        Assert.That(item.ViewCount, Is.EqualTo(1),
            "heartbeats past the threshold are the same session, not new views");
        Assert.That(item.ViewOffset, Is.GreaterThanOrEqualTo(6000000));
    }

    [Test]
    public async Task Scrobble_marks_watched_once_and_unscrobble_forgets()
    {
        var controller = timelineFor(repo);
        await controller.Scrobble(key: $"/library/metadata/{movie.Id}",
            identifier: "com.plexapp.plugins.library");

        var item = await reloadAsync(movie.Id);
        Assert.That(item.ViewCount, Is.EqualTo(1));
        Assert.That(item.ViewOffset, Is.EqualTo(6416960), "watched pins the offset at the end");

        // A second scrobble seconds later belongs to the same viewing (the 90% timeline rule
        // may already have counted it too).
        await controller.Scrobble(key: movie.Id.ToString(), identifier: "com.plexapp.plugins.library");
        Assert.That((await reloadAsync(movie.Id)).ViewCount, Is.EqualTo(1));

        await controller.Unscrobble(key: movie.Id.ToString(), identifier: "com.plexapp.plugins.library");
        item = await reloadAsync(movie.Id);
        Assert.That(item.ViewCount, Is.Zero);
        Assert.That(item.ViewOffset, Is.Null);
        Assert.That(item.LastViewedAt, Is.Null);
    }

    [Test]
    public async Task Continue_watching_lists_half_watched_items_newest_first()
    {
        var controller = timelineFor(repo);

        // In progress.
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 36980, duration: 6416960, state: "playing");

        // Started but nothing to resume yet (never progressed): stays out.
        await using (var db = factory.CreateDbContext())
        {
            db.Items.Add(new MediaItem
            {
                LibraryId = library.Id,
                FilePath = Path.Combine(root, "Fresh.mkv"),
                Title = "Fresh",
                FileSize = 1,
                DurationMs = 1000000
            });
            // Finished: past the 90% threshold, stays out.
            db.Items.Add(new MediaItem
            {
                LibraryId = library.Id,
                FilePath = Path.Combine(root, "Done.mkv"),
                Title = "Done",
                FileSize = 1,
                DurationMs = 1000000,
                ViewOffset = 1000000,
                LastViewedAt = DateTimeOffset.UtcNow,
                ViewCount = 1
            });
            await db.SaveChangesAsync();
        }

        var inProgress = await repo.GetInProgressAsync(null, 10, CancellationToken.None);
        Assert.That(inProgress.Select(x => x.Title), Is.EqualTo(new[] { "Movie Two Thousand" }),
            "only the half-watched item qualifies for the shelf");

        var hub = new HubController(repo, new ServerOptions(), new ExternalMetadata())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = await hub.HomeContinueWatching(CancellationToken.None);

        var xml = (ContentResult)result;
        var doc = System.Xml.Linq.XDocument.Parse(xml.Content!);
        var container = doc.Root!;
        Assert.That((string?)container.Attribute("size"), Is.EqualTo("1"));
        var video = container.Elements("Video").Single();
        Assert.That((string?)video.Attribute("viewOffset"), Is.EqualTo("36980"));
        Assert.That((string?)video.Attribute("title"), Is.EqualTo("Movie Two Thousand"));
    }

    [Test]
    public async Task Remove_from_continue_watching_hides_the_shelf_but_keeps_progress()
    {
        var controller = timelineFor(repo);
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 36980, duration: 6416960,
            state: "playing", playQueueItemId: 1);

        var hub = new HubController(repo, new ServerOptions(), new ExternalMetadata())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        static string? HubSize(System.Xml.Linq.XDocument doc)
            => (string?)doc.Root!.Attribute("size");

        var before = HubSize(System.Xml.Linq.XDocument.Parse(
            ((ContentResult)await hub.HomeContinueWatching(CancellationToken.None)).Content!));
        Assert.That(before, Is.EqualTo("1"), "the half-watched item starts on the shelf");

        await controller.RemoveFromContinueWatching(
            ratingKey: movie.Id.ToString(), identifier: "com.plexapp.plugins.library");

        var dismissed = await reloadAsync(movie.Id);
        Assert.That(dismissed.ContinueWatchingDismissedAt, Is.Not.Null, "the dismissal must persist");
        Assert.That(dismissed.ViewOffset, Is.EqualTo(36980), "removal must not touch the resume point");
        Assert.That(dismissed.ViewCount, Is.Zero, "removal must not mark the item watched");

        Assert.That(await repo.GetInProgressAsync(null, 10, CancellationToken.None), Is.Empty,
            "the dismissed card must leave every Continue Watching shelf");
        var after = HubSize(System.Xml.Linq.XDocument.Parse(
            ((ContentResult)await hub.HomeContinueWatching(CancellationToken.None)).Content!));
        Assert.That(after, Is.EqualTo("0"));

        // A buffering heartbeat at 0 is not the viewer playing the item and must not resurrect it.
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 0, duration: 6416960, state: "buffering");
        Assert.That((await reloadAsync(movie.Id)).ContinueWatchingDismissedAt, Is.Not.Null);

        // The next real position report reverses the removal, like on a real Plex server.
        await controller.Timeline(
            ratingKey: movie.Id, key: "", time: 40000, duration: 6416960,
            state: "playing", playQueueItemId: 1);
        Assert.That((await reloadAsync(movie.Id)).ContinueWatchingDismissedAt, Is.Null);
        Assert.That(await repo.GetInProgressAsync(null, 10, CancellationToken.None), Has.Count.EqualTo(1),
            "playing the item again must bring the card back");
    }

    [Test]
    public async Task Timeline_for_an_unknown_or_missing_key_is_harmless()
    {
        var controller = timelineFor(repo);
        await controller.Timeline(ratingKey: 999999, key: "", time: 5000, duration: 10000, state: "playing");
        await controller.Timeline(ratingKey: 0, key: "", time: 5000, duration: 10000, state: "playing");
        await controller.Scrobble(key: "not-a-key", identifier: "");

        Assert.That((await reloadAsync(movie.Id)).ViewOffset, Is.Null);
    }

    [Test]
    public void ParseKey_accepts_bare_and_path_forms()
    {
        Assert.That(TimelineController.ParseKey("5"), Is.EqualTo(5));
        Assert.That(TimelineController.ParseKey("/library/metadata/5"), Is.EqualTo(5));
        Assert.That(TimelineController.ParseKey(""), Is.EqualTo(0));
        Assert.That(TimelineController.ParseKey(null), Is.EqualTo(0));
        Assert.That(TimelineController.ParseKey("abc"), Is.EqualTo(0));
    }
}
