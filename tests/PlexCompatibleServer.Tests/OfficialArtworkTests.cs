using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using PlexCompatibleServer.Api;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Data;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class OfficialArtworkTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcs-official-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] FakeJpeg()
    {
        var bytes = new byte[600];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;

        return bytes;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(_respond());
        }
    }

    private static HttpResponseMessage Ok(HttpContent content) =>
        new(System.Net.HttpStatusCode.OK) { Content = content };

    [Test]
    public async Task EnsureAsync_downloads_once_and_reuses_the_cached_file()
    {
        var handler = new StubHandler(() => Ok(new ByteArrayContent(FakeJpeg())));
        var cache = new RemoteArtworkCache(new MediaArtOptions { CacheDirectory = _dir }, handler);
        var url = "https://metadata-static.plex.tv/poster.jpg";

        var first = await cache.EnsureAsync(url, CancellationToken.None);
        Assert.That(first, Is.Not.Null);
        Assert.That(File.Exists(first!), Is.True);
        Assert.That(first!, Does.EndWith(".jpg"));
        Assert.That(Path.GetFileName(first!), Does.StartWith("official-"));
        Assert.That(handler.Calls, Is.EqualTo(1));

        var second = await cache.EnsureAsync(url, CancellationToken.None);
        Assert.That(second, Is.EqualTo(first), "a cached URL must not be fetched again");
        Assert.That(handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task EnsureAsync_rejects_non_image_bodies_and_http_errors()
    {
        var html = new StubHandler(() => Ok(new StringContent(
            "<html>" + new string('x', 600) + "</html>", Encoding.UTF8, "text/html")));
        var cache = new RemoteArtworkCache(new MediaArtOptions { CacheDirectory = _dir }, html);

        Assert.That(await cache.EnsureAsync("https://example/fake.jpg", CancellationToken.None),
            Is.Null, "an error page must never be written where the image routes serve from");

        var missing = new StubHandler(() =>
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var cache2 = new RemoteArtworkCache(new MediaArtOptions { CacheDirectory = _dir }, missing);
        Assert.That(await cache2.EnsureAsync("https://example/gone.jpg", CancellationToken.None),
            Is.Null);

        Assert.That(Directory.GetFiles(_dir, "official-*"), Is.Empty);
    }

    [Test]
    public void SniffExtension_accepts_servable_formats_only()
    {
        Assert.That(RemoteArtworkCache.SniffExtension(FakeJpeg()), Is.EqualTo(".jpg"));

        var png = new byte[600];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4E;
        png[3] = 0x47;
        Assert.That(RemoteArtworkCache.SniffExtension(png), Is.EqualTo(".png"));

        var webp = new byte[600];
        webp[0] = (byte)'R';
        webp[1] = (byte)'I';
        webp[2] = (byte)'F';
        webp[3] = (byte)'F';
        webp[8] = (byte)'W';
        webp[9] = (byte)'E';
        webp[10] = (byte)'B';
        webp[11] = (byte)'P';
        Assert.That(RemoteArtworkCache.SniffExtension(webp), Is.EqualTo(".webp"));

        var tiny = new byte[] { 0xFF, 0xD8, 0xFF };
        Assert.That(RemoteArtworkCache.SniffExtension(tiny), Is.Null, "a stub body is not a poster");
        Assert.That(RemoteArtworkCache.SniffExtension(Array.Empty<byte>()), Is.Null);
    }

    private static MediaItem Item(int id, LibraryType type, string? officialParent = null,
                                  string? officialGrandparent = null)
    {
        var library = new MediaLibrary
        {
            Id = type == LibraryType.Movie ? 1 : 2,
            Name = type == LibraryType.Movie ? "Movies" : "TV",
            RootPath = "Z:\\",
            Type = type
        };

        return new MediaItem
        {
            Id = id,
            LibraryId = library.Id,
            Library = library,
            FilePath = type == LibraryType.Movie
                ? @"Z:\Movies\Film.2024.mkv"
                : @"Z:\TV\Show.S01E01.mkv",
            Title = type == LibraryType.Movie ? "Film" : "Show",
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(1790948816),
            OfficialParentPosterPath = officialParent,
            OfficialGrandparentPosterPath = officialGrandparent
        };
    }

    [Test]
    public void ToVideo_states_season_and_show_poster_urls_only_with_official_art()
    {
        var covered = Item(44, LibraryType.Show,
            officialParent: @"C:\art\season.jpg",
            officialGrandparent: @"C:\art\show.jpg");
        var video = LibraryController.ToVideo(covered);

        Assert.That(video.ParentThumb, Is.EqualTo("/library/metadata/44/parentThumb/1790948816"));
        Assert.That(video.GrandparentThumb, Is.EqualTo("/library/metadata/44/grandparentThumb/1790948816"));

        // Before the artwork sync has run there is nothing for those routes to serve: stating
        // them anyway would leave the card blank where the client could still fall back.
        var bare = Item(45, LibraryType.Show);
        var bareVideo = LibraryController.ToVideo(bare);
        Assert.That(bareVideo.ParentThumb, Is.Empty);
        Assert.That(bareVideo.GrandparentThumb, Is.Empty);

        var movie = Item(3, LibraryType.Movie);
        var movieVideo = LibraryController.ToVideo(movie);
        Assert.That(movieVideo.ParentThumb, Is.Empty);
        Assert.That(movieVideo.GrandparentThumb, Is.Empty);
    }

    private sealed class FakeRepo(MediaItem item) : IMediaRepository
    {
        public Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaLibrary>>([item.Library]);

        public Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct)
            => Task.FromResult<MediaLibrary?>(item.Library);

        public Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaItem>>([item]);

        public Task<MediaItem?> GetItemAsync(int id, CancellationToken ct)
            => Task.FromResult(item.Id == id ? item : null);

        public Task<IReadOnlyList<MediaItem>> GetItemsByLibrariesAsync(IReadOnlyList<int> libraryIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaItem>>([item]);

        public Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct)
            => Task.CompletedTask;

        public Task SaveProgressAsync(int id, long timeMs, long durationMs, DateTimeOffset viewedAt,
                                      CancellationToken ct)
            => Task.CompletedTask;

        public Task MarkWatchedAsync(int id, DateTimeOffset viewedAt, CancellationToken ct)
            => Task.CompletedTask;

        public Task ClearProgressAsync(int id, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<MediaItem>> GetInProgressAsync(int? libraryId, int limit,
                                                                 CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task DismissFromContinueWatchingAsync(int id, CancellationToken ct)
            => Task.CompletedTask;

        public Task SaveOfficialArtworkAsync(int id, string? posterPath, string? artPath,
                                             string? parentPosterPath, string? grandparentPosterPath,
                                             CancellationToken ct)
            => Task.CompletedTask;
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, FakeJpeg());

        return path;
    }

    [Test]
    public async Task MetadataThumb_prefers_the_official_poster_and_falls_back_to_the_frame()
    {
        var official = Touch("official-movie.jpg");
        var frame = Touch("frame-movie.jpg");
        var item = Item(3, LibraryType.Movie);
        item.OfficialPosterPath = official;
        item.PosterPath = frame;
        var controller = new MetadataController(new FakeRepo(item), new ServerOptions(),
                                                new StreamSelectionStore(), new ExternalMetadata());

        var hit = await controller.Thumb(3, CancellationToken.None) as PhysicalFileResult;
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.FileName, Is.EqualTo(official));

        item.OfficialPosterPath = Path.Combine(_dir, "deleted.jpg");
        var fallback = await controller.Thumb(3, CancellationToken.None) as PhysicalFileResult;
        Assert.That(fallback!.FileName, Is.EqualTo(frame),
            "a vanished official file must fall back to the frame extract");
    }

    [Test]
    public async Task MetadataParentThumb_chains_season_show_then_frame()
    {
        var season = Touch("official-season.jpg");
        var show = Touch("official-show.jpg");
        var frame = Touch("frame-episode.jpg");
        var item = Item(44, LibraryType.Show, season, show);
        item.PosterPath = frame;
        var controller = new MetadataController(new FakeRepo(item), new ServerOptions(),
                                                new StreamSelectionStore(), new ExternalMetadata());

        var all = await controller.ParentThumb(44, CancellationToken.None) as PhysicalFileResult;
        Assert.That(all!.FileName, Is.EqualTo(season));

        item.OfficialParentPosterPath = Path.Combine(_dir, "deleted.jpg");
        var showOnly = await controller.ParentThumb(44, CancellationToken.None) as PhysicalFileResult;
        Assert.That(showOnly!.FileName, Is.EqualTo(show));

        item.OfficialGrandparentPosterPath = null;
        var frameOnly = await controller.ParentThumb(44, CancellationToken.None) as PhysicalFileResult;
        Assert.That(frameOnly!.FileName, Is.EqualTo(frame),
            "the season grid must never get a 404 from a card it was told to draw");

        var grand = await controller.GrandparentThumb(44, CancellationToken.None) as PhysicalFileResult;
        Assert.That(grand!.FileName, Is.EqualTo(frame));
    }

    [Test]
    public async Task PhotoTranscode_serves_the_season_and_show_slots_from_official_art()
    {
        var season = Touch("official-season.jpg");
        var show = Touch("official-show.jpg");
        var poster = Touch("frame-poster.jpg");
        var item = Item(44, LibraryType.Show, season, show);
        item.PosterPath = poster;
        var controller = new PhotoController(new FakeRepo(item), Transcoder());

        var parent = await controller.Transcode("/library/metadata/44/parentThumb/1790948816",
                                                600, 900, "") as PhysicalFileResult;
        Assert.That(parent!.FileName, Is.EqualTo(season));

        var grandparent = await controller.Transcode(
            "/library/metadata/44/grandparentThumb/1790948816", 600, 900, "")
            as PhysicalFileResult;
        Assert.That(grandparent!.FileName, Is.EqualTo(show));

        var thumb = await controller.Transcode("/library/metadata/44/thumb/1790948816",
                                               600, 900, "") as PhysicalFileResult;
        Assert.That(thumb!.FileName, Is.EqualTo(poster));

        // Never 404: with nothing on disk at all the placeholder still comes back.
        var empty = Item(9, LibraryType.Show);
        var placeholder = new PhotoController(new FakeRepo(empty), Transcoder());
        var result = await placeholder.Transcode("/library/metadata/9/thumb/1", 600, 900, "");
        Assert.That(result, Is.InstanceOf<FileContentResult>());
    }

    private ImageTranscoder Transcoder() =>
        new(new MediaArtOptions { CacheDirectory = _dir }, NullLogger<ImageTranscoder>.Instance);

    [Test]
    public void FitWithin_preserves_aspect_and_only_enlarges_when_asked()
    {
        Assert.That(ImageTranscoder.FitWithin(960, 1440, 240, 360, true), Is.EqualTo((240, 360)));
        Assert.That(ImageTranscoder.FitWithin(1280, 720, 240, 360, true), Is.EqualTo((240, 135)));
        Assert.That(ImageTranscoder.FitWithin(1600, 900, 320, 180, false), Is.EqualTo((320, 180)));
        Assert.That(ImageTranscoder.FitWithin(100, 150, 240, 360, false), Is.EqualTo((100, 150)),
            "without upscale the source must go back untouched");
        Assert.That(ImageTranscoder.FitWithin(100, 150, 240, 360, true), Is.EqualTo((240, 360)));
        Assert.That(ImageTranscoder.FitWithin(1, 1000, 360, 240, true), Is.EqualTo((1, 240)),
            "a sliver of an image still keeps at least one pixel of width");
    }

    [Test]
    public void ImageSize_reads_png_and_jpeg_headers_and_rejects_anything_else()
    {
        var png = Path.Combine(_dir, "pixel.png");
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        Assert.That(ImageSize.TryGet(png, out var pixelW, out var pixelH), Is.True);
        Assert.That((pixelW, pixelH), Is.EqualTo((1, 1)));

        var jpeg = Path.Combine(_dir, "sized.jpg");
        File.WriteAllBytes(jpeg, JpegHeader(200, 300));
        Assert.That(ImageSize.TryGet(jpeg, out var jpegW, out var jpegH), Is.True);
        Assert.That((jpegW, jpegH), Is.EqualTo((200, 300)));

        var junk = Path.Combine(_dir, "junk.bin");
        File.WriteAllBytes(junk, [1, 2, 3]);
        Assert.That(ImageSize.TryGet(junk, out _, out _), Is.False);
    }

    [Test]
    public async Task ResizeAsync_returns_cached_resizes_and_falls_back_to_the_original()
    {
        var transcoder = Transcoder();

        // Dimensions already match the box: the source goes back with no work at all.
        var exact = Path.Combine(_dir, "exact.jpg");
        File.WriteAllBytes(exact, JpegHeader(240, 360));
        Assert.That(await transcoder.ResizeAsync(exact, 240, 360, true, default), Is.EqualTo(exact));

        // A cached resize short-circuits before ffmpeg is ever consulted.
        var source = Path.Combine(_dir, "source.jpg");
        File.WriteAllBytes(source, JpegHeader(960, 1440));
        var cached = Path.Combine(_dir, $"{ImageTranscoder.SourceKey(source)}-240x360.jpg");
        File.WriteAllBytes(cached, FakeJpeg());
        Assert.That(await transcoder.ResizeAsync(source, 240, 360, true, default), Is.EqualTo(cached));

        // Undecodable payload (or no ffmpeg on the machine): the original comes back either way.
        var undecodable = Path.Combine(_dir, "undecodable.jpg");
        File.WriteAllBytes(undecodable, JpegHeader(960, 1440));
        Assert.That(await transcoder.ResizeAsync(undecodable, 240, 360, true, default),
            Is.EqualTo(undecodable));

        // No size asked, or nothing on disk: pass the input through untouched.
        Assert.That(await transcoder.ResizeAsync(source, 0, 0, true, default), Is.EqualTo(source));
        var missing = Path.Combine(_dir, "missing.jpg");
        Assert.That(await transcoder.ResizeAsync(missing, 240, 360, true, default),
            Is.EqualTo(missing));
    }

    private static byte[] JpegHeader(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10,                     // APP0/JFIF, 16 bytes
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
        0x01, 0x02, 0x00, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x11, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0x02, 0x11, 0x01,
        0x03, 0x11, 0x01,
        0xFF, 0xD9
    ];

    private sealed class TestFactory(DbContextOptions<MediaDbContext> options)
        : IDbContextFactory<MediaDbContext>
    {
        public MediaDbContext CreateDbContext() => new(options);
    }

    [Test]
    public async Task SaveOfficialArtworkAsync_writes_new_paths_and_null_keeps_current_ones()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            "pcs-official-art-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        var factory = new TestFactory(options);

        int id;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            var library = new MediaLibrary
            {
                Name = "Movies",
                RootPath = _dir,
                Type = LibraryType.Movie
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var item = new MediaItem
            {
                LibraryId = library.Id,
                FilePath = Path.Combine(_dir, "Film.mkv"),
                Title = "Film"
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            id = item.Id;
        }

        try
        {
            var repo = new MediaRepository(factory);
            await repo.SaveOfficialArtworkAsync(id, "/art/poster.jpg", "/art/backdrop.jpg",
                                                null, null, CancellationToken.None);

            var first = await repo.GetItemAsync(id, CancellationToken.None);
            Assert.That(first!.OfficialPosterPath, Is.EqualTo("/art/poster.jpg"));
            Assert.That(first.OfficialArtPath, Is.EqualTo("/art/backdrop.jpg"));
            Assert.That(first.OfficialParentPosterPath, Is.Null);

            // A partial sync must never erase what an earlier run downloaded.
            await repo.SaveOfficialArtworkAsync(id, null, null, "/art/season.jpg", null,
                                                CancellationToken.None);
            var second = await repo.GetItemAsync(id, CancellationToken.None);
            Assert.That(second!.OfficialPosterPath, Is.EqualTo("/art/poster.jpg"));
            Assert.That(second.OfficialArtPath, Is.EqualTo("/art/backdrop.jpg"));
            Assert.That(second.OfficialParentPosterPath, Is.EqualTo("/art/season.jpg"));
        }
        finally
        {
            foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(p); } catch { }
        }
    }
}
